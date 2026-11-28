using System;
using System.Threading.Tasks;

namespace AegisBrowser.WebView2Feasibility;

// Synthetic callback orderings against the actual tracker used by the form.
// No WebView2 process is started and ownership is not independently verified.
internal static class NavigationLeaseTrackerTests
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("Navigation check " + (count + 1)); count++; }
        static LifecycleCoordinator Ready()
        {
            var lifecycle = new LifecycleCoordinator();
            if (!lifecycle.TryBeginInitialization() || !lifecycle.FinishInitialization(true, null))
                throw new InvalidOperationException("Lifecycle setup failed.");
            return lifecycle;
        }
        static IDisposable Begin(LifecycleCoordinator lifecycle)
        {
            if (!lifecycle.TryBeginNavigation(out var lease) || lease is null)
                throw new InvalidOperationException("Navigation setup failed.");
            return lease;
        }
        static void Dispatch(NavigationLeaseTracker tracker) => tracker.Dispatch(() => { });

        // Close before a starting callback: its ID remains a candidate. Even
        // matching completion is not ownership proof and cannot release work.
        var coordinator = Ready();
        var tracker = new NavigationLeaseTracker();
        Check(tracker.TryBegin(Begin(coordinator)));
        Dispatch(tracker);
        Check(coordinator.TryBeginClose());
        Check(!await coordinator.WaitForQuiescenceAsync(Task.CompletedTask));
        var start = tracker.ObserveStart(41, expectedUri: true, redirected: false, closing: true);
        Check(start.Cancel && start.Event == "CANDIDATE_START_CANCELLED" && tracker.CandidateId == 41);
        Check(tracker.HasPending && !await coordinator.WaitForQuiescenceAsync(Task.CompletedTask));
        var terminal = tracker.ObserveCompletion(41);
        Check(!terminal.Released && terminal.Event == "CANDIDATE_TERMINAL_UNVERIFIED");
        Check(tracker.HasPending && tracker.IsAmbiguous && !await coordinator.WaitForQuiescenceAsync(Task.CompletedTask));
        Check(!tracker.ObserveCompletion(41).Released);

        // Counterexample: an unrelated about:blank starts and completes first,
        // before the actual issued request's ID appears. URI/ordering cannot
        // distinguish this from the request; no completion may release it.
        coordinator = Ready(); tracker = new NavigationLeaseTracker();
        Check(tracker.TryBegin(Begin(coordinator)));
        Dispatch(tracker);
        Check(!tracker.ObserveStart(51, true, false, false).Cancel);
        Check(!tracker.ObserveCompletion(51).Released && tracker.HasPending);
        Check(tracker.ObserveStart(52, true, false, false).Cancel);
        Check(!tracker.ObserveCompletion(52).Released && tracker.HasPending);

        // Redirects keep an ID; cancelling a disallowed redirect or receiving
        // its terminal callback cannot release candidate-only ownership.
        coordinator = Ready(); tracker = new NavigationLeaseTracker();
        Check(tracker.TryBegin(Begin(coordinator)));
        Dispatch(tracker);
        Check(!tracker.ObserveStart(11, true, false, false).Cancel);
        Check(!tracker.ObserveStart(11, true, true, false).Cancel);
        Check(tracker.ObserveStart(11, false, true, false).Cancel);
        Check(!tracker.ObserveCompletion(11).Released && tracker.HasPending);
        Check(coordinator.TryBeginClose());
        Check(!await coordinator.WaitForQuiescenceAsync(Task.CompletedTask));

        // Overlap, stale events, nonmatching completions and duplicate starts
        // never release or reassign the tracked lease.
        coordinator = Ready(); tracker = new NavigationLeaseTracker();
        Check(tracker.TryBegin(Begin(coordinator)));
        Dispatch(tracker);
        Check(!tracker.ObserveStart(21, true, false, false).Cancel);
        Check(tracker.ObserveStart(22, true, false, false).Event == "OVERLAPPING_START");
        Check(!tracker.ObserveCompletion(22).Released);
        Check(!tracker.ObserveCompletion(21).Released && tracker.HasPending && tracker.IsAmbiguous);
        Check(coordinator.TryBeginClose());
        Check(!await coordinator.WaitForQuiescenceAsync(Task.CompletedTask));
        coordinator = Ready(); tracker = new NavigationLeaseTracker();
        Check(tracker.TryBegin(Begin(coordinator)));
        Dispatch(tracker);
        Check(!tracker.ObserveCompletion(31).Released && tracker.IsAmbiguous);
        Check(tracker.ObserveStart(32, true, false, false).Cancel && tracker.IsAmbiguous);
        Check(!tracker.ObserveCompletion(32).Released && tracker.HasPending);
        coordinator = Ready(); tracker = new NavigationLeaseTracker();
        Check(tracker.TryBegin(Begin(coordinator)));
        Dispatch(tracker);
        tracker.ObserveStart(61, true, false, false);
        Check(tracker.ObserveStart(61, true, false, false).Event == "DUPLICATE_START");
        Check(!tracker.ObserveCompletion(61).Released && tracker.HasPending);

        // Explicit failure before dispatch is the only releasable failure.
        coordinator = Ready(); tracker = new NavigationLeaseTracker();
        Check(tracker.TryBegin(Begin(coordinator)));
        Check(tracker.FailSynchronousRequest());
        Check(!tracker.FailSynchronousRequest());
        Check(coordinator.TryBeginClose() && await coordinator.WaitForQuiescenceAsync(Task.CompletedTask));

        // Native call can queue work then throw before any callback. The
        // exception is not evidence of pre-dispatch failure or cancellation.
        coordinator = Ready(); tracker = new NavigationLeaseTracker();
        Check(tracker.TryBegin(Begin(coordinator)));
        try { tracker.Dispatch(() => throw new InvalidOperationException()); }
        catch (InvalidOperationException) { }
        Check(!tracker.FailSynchronousRequest() && tracker.HasPending && tracker.IsAmbiguous);
        Check(tracker.ObserveStart(71, true, false, false).Cancel);
        Check(!tracker.ObserveCompletion(71).Released && tracker.HasPending);
        Check(coordinator.TryBeginClose());
        Check(!await coordinator.WaitForQuiescenceAsync(Task.CompletedTask));

        // A callback reentrant inside the native call also retains its lease
        // when the call subsequently throws.
        coordinator = Ready(); tracker = new NavigationLeaseTracker();
        Check(tracker.TryBegin(Begin(coordinator)));
        try { tracker.Dispatch(() => { tracker.ObserveStart(81, true, false, false); throw new InvalidOperationException(); }); }
        catch (InvalidOperationException) { }
        Check(!tracker.FailSynchronousRequest() && tracker.IsAmbiguous);
        Check(!tracker.ObserveCompletion(81).Released && tracker.HasPending);

        // An observed unowned navigation blocks a new call until its own
        // completion. Its retired ID cannot be reused by a future request.
        coordinator = Ready(); tracker = new NavigationLeaseTracker();
        Check(tracker.ObserveStart(91, false, false, false).Event == "UNOWNED_START");
        var unused = Begin(coordinator);
        Check(!tracker.TryBegin(unused));
        unused.Dispose(); // no native call was attempted
        Check(!tracker.ObserveCompletion(91).Released);
        Check(tracker.TryBegin(Begin(coordinator)));
        Dispatch(tracker);
        Check(tracker.ObserveStart(91, true, false, false).Event == "STALE_START");
        Check(!tracker.ObserveCompletion(91).Released && tracker.HasPending);

        // Once the bounded stale-ID history is full, refuse a new attempt
        // instead of forgetting an ID that could be mistaken for a candidate.
        tracker = new NavigationLeaseTracker();
        for (ulong id = 1000; id < 1256; id++)
        {
            if (!tracker.ObserveStart(id, false, false, false).Cancel || tracker.ObserveCompletion(id).Released)
                throw new InvalidOperationException("Navigation capacity setup failed.");
        }
        var released = 0;
        var refused = new CountedLease(() => released++);
        Check(!tracker.TryBegin(refused) && tracker.IsAmbiguous && released == 0);
        refused.Dispose(); // no native call was attempted
        Check(released == 1);
        return count;
    }

    private sealed class CountedLease : IDisposable
    {
        private Action? release;
        internal CountedLease(Action release) => this.release = release;
        public void Dispose()
        {
            var callback = release;
            release = null;
            callback?.Invoke();
        }
    }
}
