namespace AegisBrowser.Core;

public enum ViewportZoomQueryStatus
{
    Started, Pending, Succeeded, Failed, RetryDelay, Stale, TimedOut, Blocked
}

public readonly record struct ViewportZoomQueryResult(ViewportZoomQueryStatus Status, double Zoom = 0, bool Warn = false);

/// <summary>
/// UI-thread-owned state for one browser's zoom query. A CEF request cannot be
/// cancelled here: after a timeout this state drops the task and suppresses
/// replacement requests until the browser itself is retired.
/// </summary>
public sealed class ViewportZoomQuery(TimeProvider? clock = null)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private Task<double>? pending;
    private long started;
    private long failed;
    private bool retryDelayed;
    private bool timedOut;
    private bool stale;
    private bool warned;

    public bool HasPendingRequest => pending is not null;

    // Retirement discards the application's task reference. It does not
    // assert that the underlying CEF operation was cancelled.
    public void Retire() { pending = null; timedOut = true; stale = false; warned = true; }

    // Navigation invalidates a still-pending measurement. Do not replace it:
    // CEF can still complete it and cannot be assumed to support cancellation.
    public void Invalidate() { if (pending is not null) stale = true; }

    // This can run for background tabs without starting a new CEF request.
    // A completion first observed after the deadline is conservatively stale:
    // the UI thread cannot infer whether CEF actually completed before it.
    public bool Expire()
    {
        if (pending is null || clock.GetElapsedTime(started) < Timeout) return false;
        pending = null;
        stale = false;
        timedOut = true;
        return true;
    }

    // A completed background result may no longer match that tab's current
    // document. Drop it; selection starts a fresh measurement instead.
    public void DiscardCompletedBackground()
    {
        if (pending is null || !pending.IsCompleted) return;
        pending = null;
        stale = false;
    }

    public ViewportZoomQueryResult Poll(Func<Task<double>> request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (timedOut) return new(ViewportZoomQueryStatus.Blocked, Warn: MarkWarning());
        if (Expire()) return new(ViewportZoomQueryStatus.TimedOut, Warn: MarkWarning());
        if (pending is null)
        {
            if (retryDelayed && clock.GetElapsedTime(failed) < RetryDelay)
                return new(ViewportZoomQueryStatus.RetryDelay);
            retryDelayed = false;
            try
            {
                pending = request() ?? throw new InvalidOperationException("Zoom request returned no task.");
                started = clock.GetTimestamp();
                // Observe a fault even if timeout or tab closure removes our reference.
                _ = pending.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return new(ViewportZoomQueryStatus.Started);
            }
            catch (Exception) { return Failure(); }
        }

        if (!pending.IsCompleted) return new(ViewportZoomQueryStatus.Pending);

        var completed = pending;
        pending = null;
        if (stale) { stale = false; return new(ViewportZoomQueryStatus.Stale); }
        if (!completed.IsCompletedSuccessfully) return Failure();
        var zoom = completed.GetAwaiter().GetResult(); // Already completed successfully; never blocks.
        if (!double.IsFinite(zoom)) return Failure();
        warned = false;
        return new(ViewportZoomQueryStatus.Succeeded, zoom);
    }

    private ViewportZoomQueryResult Failure()
    {
        failed = clock.GetTimestamp();
        retryDelayed = true;
        return new(ViewportZoomQueryStatus.Failed, Warn: MarkWarning());
    }

    private bool MarkWarning()
    {
        if (warned) return false;
        warned = true;
        return true;
    }
}
