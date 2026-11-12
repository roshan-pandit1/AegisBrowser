using AegisBrowser.Core;

internal static class ViewportZoomQueryTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL viewport zoom query: " + name);
            count++;
        }
        static TaskCompletionSource<double> Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new TestClock();
        var state = new ViewportZoomQuery(clock);
        var calls = 0;
        var source = Pending();
        Task<double> Start() { calls++; return source.Task; }
        Check(state.Poll(Start).Status == ViewportZoomQueryStatus.Started && calls == 1, "starts once");
        Check(state.Poll(Start).Status == ViewportZoomQueryStatus.Pending && calls == 1, "pending never duplicates");
        source.SetResult(1.25);
        var result = state.Poll(Start);
        Check(result.Status == ViewportZoomQueryStatus.Succeeded && result.Zoom == 1.25, "completed zoom returned");

        source = Pending();
        state.Poll(Start);
        clock.Advance(TimeSpan.FromSeconds(3));
        result = state.Poll(Start);
        Check(result.Status == ViewportZoomQueryStatus.TimedOut && result.Warn && !state.HasPendingRequest,
            "timeout drops task reference and warns once");
        var timedOutCalls = calls;
        source.SetResult(2);
        for (var i = 0; i < 20; i++)
            Check(state.Poll(Start).Status == ViewportZoomQueryStatus.Blocked && calls == timedOutCalls,
                "late result ignored and no replacement query");

        var completedLate = new ViewportZoomQuery(clock);
        var lateSource = Pending();
        completedLate.Poll(() => lateSource.Task);
        clock.Advance(TimeSpan.FromSeconds(3));
        lateSource.SetResult(42);
        result = completedLate.Poll(() => Task.FromResult(0d));
        Check(result.Status == ViewportZoomQueryStatus.TimedOut && result.Warn && !completedLate.HasPendingRequest,
            "completion first observed at deadline times out instead of applying stale zoom");
        Check(completedLate.Poll(() => Task.FromResult(0d)).Status == ViewportZoomQueryStatus.Blocked,
            "completed-late query cannot be retried or applied");

        var delayedUi = new ViewportZoomQuery(clock);
        var earlySource = Pending();
        delayedUi.Poll(() => earlySource.Task);
        earlySource.SetResult(12);
        clock.Advance(TimeSpan.FromSeconds(4));
        Check(delayedUi.Expire() && delayedUi.Poll(() => Task.FromResult(0d)).Status == ViewportZoomQueryStatus.Blocked,
            "completed result first observed after a delayed UI deadline is conservatively discarded");

        var other = new ViewportZoomQuery(clock);
        Check(other.Poll(() => Task.FromResult(-1d)).Status == ViewportZoomQueryStatus.Started,
            "other tab starts while first is blocked");
        Check(other.Poll(() => Task.FromResult(0d)).Zoom == -1, "other tab succeeds");

        var background = new ViewportZoomQuery(clock);
        var backgroundSource = Pending();
        var backgroundCalls = 0;
        Task<double> BackgroundStart() { backgroundCalls++; return backgroundSource.Task; }
        background.Poll(BackgroundStart);
        clock.Advance(TimeSpan.FromSeconds(3));
        Check(background.Expire() && !background.HasPendingRequest && backgroundCalls == 1,
            "background timeout drops task without starting another");
        backgroundSource.SetResult(5);
        result = background.Poll(BackgroundStart);
        Check(result.Status == ViewportZoomQueryStatus.Blocked && result.Warn && backgroundCalls == 1,
            "background timeout warns on selection and ignores late completion");
        Check(!background.Poll(BackgroundStart).Warn, "background timeout warns only once");
        var backgroundCompleted = new ViewportZoomQuery(clock);
        backgroundCompleted.Poll(() => Task.FromResult(7d));
        backgroundCompleted.DiscardCompletedBackground();
        Check(!backgroundCompleted.HasPendingRequest &&
            backgroundCompleted.Poll(() => Task.FromResult(8d)).Status == ViewportZoomQueryStatus.Started &&
            backgroundCompleted.Poll(() => Task.FromResult(0d)).Zoom == 8,
            "completed background result discarded before fresh selection");

        var stale = new ViewportZoomQuery(clock);
        var old = Pending();
        stale.Poll(() => old.Task);
        stale.Invalidate();
        old.SetResult(3);
        Check(stale.Poll(() => Task.FromResult(0d)).Status == ViewportZoomQueryStatus.Stale,
            "navigation discards pending result");
        Check(stale.Poll(() => Task.FromResult(4d)).Status == ViewportZoomQueryStatus.Started &&
            stale.Poll(() => Task.FromResult(0d)).Zoom == 4, "fresh post-navigation result");

        var switched = new ViewportZoomQuery(clock);
        var oldSelection = Pending();
        switched.Poll(() => oldSelection.Task);
        switched.Invalidate(); // Tab became background before the query completed.
        oldSelection.SetResult(6);
        Check(switched.Poll(() => Task.FromResult(0d)).Status == ViewportZoomQueryStatus.Stale,
            "rapid reselection discards a result from before switching away");
        Check(switched.Poll(() => Task.FromResult(8d)).Status == ViewportZoomQueryStatus.Started &&
            switched.Poll(() => Task.FromResult(0d)).Zoom == 8,
            "rapid reselection applies a fresh query result");

        var failures = new ViewportZoomQuery(clock);
        failures.Poll(() => Task.FromException<double>(new InvalidOperationException("synthetic")));
        result = failures.Poll(() => Task.FromResult(0d));
        Check(result.Status == ViewportZoomQueryStatus.Failed && result.Warn, "fault observed and warning emitted");
        Check(failures.Poll(() => Task.FromResult(0d)).Status == ViewportZoomQueryStatus.RetryDelay,
            "fault retry is bounded");
        clock.Advance(TimeSpan.FromSeconds(3));
        failures.Poll(() => Task.FromCanceled<double>(new CancellationToken(true)));
        result = failures.Poll(() => Task.FromResult(0d));
        Check(result.Status == ViewportZoomQueryStatus.Failed && !result.Warn, "cancellation observed without repeated warning");
        clock.Advance(TimeSpan.FromSeconds(3));
        failures.Poll(() => Task.FromResult(double.NaN));
        Check(failures.Poll(() => Task.FromResult(0d)).Status == ViewportZoomQueryStatus.Failed,
            "invalid zoom rejected");
        clock.Advance(TimeSpan.FromSeconds(3));
        Check(failures.Poll(() => throw new InvalidOperationException()).Status == ViewportZoomQueryStatus.Failed,
            "synchronous query failure contained");

        var retired = new ViewportZoomQuery(clock);
        var closing = Pending();
        retired.Poll(() => closing.Task);
        retired.Retire();
        Check(!retired.HasPendingRequest, "tab retirement releases task reference");
        closing.SetResult(99);
        result = retired.Poll(() => Task.FromResult(0d));
        Check(result.Status == ViewportZoomQueryStatus.Blocked && !result.Warn,
            "retired tab ignores late completion without warning");
        Console.WriteLine($"PASS: {count} viewport zoom query checks");
    }

    private sealed class TestClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}
