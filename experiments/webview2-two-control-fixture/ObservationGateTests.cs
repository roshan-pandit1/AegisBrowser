using System;

namespace AegisBrowser.WebView2TwoControl;

// Synthetic fault injection against the actual observation gate. These tests
// do not execute WebView2 or prove request ownership/native quiescence.
internal static class ObservationGateTests
{
    private static int checks;
    private static void Check(bool value, string name)
    {
        checks++;
        if (!value) throw new InvalidOperationException("FAIL: " + name);
    }

    private static ObservationGate Ready()
    {
        var gate = new ObservationGate();
        Check(gate.CompleteInitialization(0, true), "initialization A");
        Check(gate.CompleteInitialization(1, true), "initialization B");
        return gate;
    }

    internal static int Run()
    {
        checks = 0;
        var g = new ObservationGate();
        Check(g.PendingOperations == 2, "two outstanding initializations");
        g.RequestClose();
        Check(!g.CanAttemptObservedDisposal, "close during initialization blocked");
        Check(g.CompleteInitialization(0, true), "late initialization A");
        Check(!g.CanAttemptObservedDisposal, "other initialization still pending");
        Check(g.CompleteInitialization(1, true), "late initialization B");
        Check(g.CanAttemptObservedDisposal, "both initializations complete");
        Check(!g.TryBeginDispatch(0), "no dispatch after close");

        g = Ready();
        Check(g.TryBeginDispatch(0), "dispatch A");
        Check(g.TryBeginDispatch(1), "dispatch B");
        Check(!g.TryBeginDispatch(0), "overlap not admitted");
        Check(g.PendingOperations == 2, "two pending dispatches");
        g.RequestClose();
        g.RequestClose();
        Check(!g.CanAttemptObservedDisposal, "repeated close does not release");
        var startA = g.ObserveStart(0, 2, true, true, false);
        var startB = g.ObserveStart(1, 2, true, true, false);
        Check(startA.Candidate && startA.Cancel, "A start associated before cancel");
        Check(startB.Candidate && startB.Cancel, "B same ID is independent");
        Check(g.HasCandidate(0) && g.HasCandidate(1), "per-control candidates");
        Check(g.PendingOperations == 2, "cancellation does not release");
        g.BeginResource(0);
        Check(g.PendingForSlot(0) == 2, "resource counted after cancel");
        Check(g.ObserveCompletion(0, 2, false, true).Expected, "A terminal after cancel");
        Check(g.PendingForSlot(0) == 1 && !g.CanAttemptObservedDisposal, "in-flight resource blocks");
        g.EndResource(0);
        Check(!g.CanAttemptObservedDisposal, "B still pending");
        Check(g.ObserveCompletion(1, 2, false, true).Expected, "B terminal after cancel");
        Check(g.PendingOperations == 0 && g.CanAttemptObservedDisposal, "observed operations drained");
        Check(!g.ObserveCompletion(1, 2, false, true).Expected, "duplicate terminal rejected");
        Check(g.IsAmbiguous && !g.CanAttemptObservedDisposal, "duplicate prevents disposal");

        g = Ready();
        Check(g.TryBeginDispatch(0), "normal dispatch");
        Check(!g.ObserveStart(0, 10, true, true, false).Cancel, "normal candidate proceeds");
        Check(g.ObserveCompletion(0, 10, true, false).Expected, "normal terminal success");
        Check(!g.IsDispatchPending(0), "exactly one terminal clears pending");
        Check(g.TryBeginDispatch(0), "second fresh dispatch");
        Check(!g.ObserveStart(0, 10, true, true, false).Candidate, "retired ID replay rejected");
        Check(g.IsAmbiguous && g.IsDispatchPending(0), "replay keeps dispatch unresolved");

        g = Ready();
        Check(g.TryBeginDispatch(0), "competing dispatch candidate");
        Check(g.ObserveStart(0, 11, true, true, false).Candidate, "matching candidate is only an observation");
        Check(!g.ObserveStart(0, 12, true, true, false).Candidate, "same-tuple competitor rejected when observed");
        g.RequestClose();
        Check(!g.CanAttemptObservedDisposal, "observed competing start blocks");

        g = Ready();
        Check(g.TryBeginDispatch(0), "stale identity dispatch");
        Check(!g.ObserveStart(0, 20, true, false, false).Candidate, "header mismatch rejected");
        Check(g.IsAmbiguous && g.IsDispatchPending(0), "unmatched identity unresolved");

        g = Ready();
        Check(g.TryBeginDispatch(0), "unrelated dispatch");
        Check(!g.ObserveStart(0, 21, false, true, false).Candidate, "unrelated URI rejected");
        Check(g.IsAmbiguous, "unrelated start marks ambiguity");

        g = Ready();
        Check(g.TryBeginDispatch(0), "redirect dispatch");
        Check(!g.ObserveStart(0, 22, true, true, true).Candidate, "redirect rejected");
        Check(g.IsAmbiguous, "redirect marks ambiguity");

        g = Ready();
        Check(g.TryBeginDispatch(0), "terminal mismatch dispatch");
        Check(g.ObserveStart(0, 23, true, true, false).Candidate, "terminal mismatch candidate");
        Check(!g.ObserveCompletion(0, 24, true, false).Matched, "wrong terminal ID rejected");
        Check(g.IsAmbiguous && g.IsDispatchPending(0), "wrong terminal retains dispatch");

        g = Ready();
        Check(g.TryBeginDispatch(0), "dispatch exception case");
        g.DispatchUncertain(0);
        g.RequestClose();
        Check(g.IsAmbiguous && g.IsDispatchPending(0), "dispatch exception retains work");
        Check(!g.CanAttemptObservedDisposal, "exception never authorizes disposal");

        g = Ready();
        Check(g.TryBeginDispatch(0), "missing terminal case");
        g.RequestClose();
        Check(g.ObserveStart(0, 25, true, true, false).Cancel, "missing terminal canceled");
        Check(!g.CanAttemptObservedDisposal && g.IsDispatchPending(0), "missing terminal retains work");
        // A deadline is deliberately not an event accepted by this gate.
        Check(!g.CanAttemptObservedDisposal, "elapsed deadline cannot release work");

        g = Ready();
        Check(g.TryBeginDispatch(0), "unexpected completion outcome dispatch");
        g.RequestClose();
        Check(g.ObserveStart(0, 26, true, true, false).Candidate, "unexpected completion candidate");
        Check(!g.ObserveCompletion(0, 26, true, false).Expected, "noncancel terminal rejected");
        Check(g.IsAmbiguous && !g.CanAttemptObservedDisposal, "unexpected outcome blocks");

        g = Ready();
        g.EndResource(1);
        g.RequestClose();
        Check(g.IsAmbiguous && !g.CanAttemptObservedDisposal, "unmatched resource end blocks");

        g = new ObservationGate();
        Check(!g.CompleteInitialization(0, false), "initialization failure reported");
        Check(g.CompleteInitialization(1, true), "other initialization can finish");
        g.RequestClose();
        Check(g.IsAmbiguous && !g.CanAttemptObservedDisposal, "initialization failure blocks");

        g = Ready();
        for (ulong id = 0; id < 256; id++)
        {
            if (!g.TryBeginDispatch(0) || !g.ObserveStart(0, id, true, true, false).Candidate ||
                !g.ObserveCompletion(0, id, true, false).Expected)
                throw new InvalidOperationException("Failed during bounded-ID setup.");
        }
        Check(g.TryBeginDispatch(0), "dispatch after bounded-ID limit");
        Check(!g.ObserveStart(0, 256, true, true, false).Candidate, "ID capacity fails closed");
        Check(g.IsAmbiguous && g.IsDispatchPending(0), "capacity retains unresolved dispatch");
        return checks;
    }
}
