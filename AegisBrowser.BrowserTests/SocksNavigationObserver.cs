using CefSharp;

namespace AegisBrowser.BrowserTests;

// Test-owned observer; it does not retain CEF frame wrappers or subscribe to events.
// The caller installs and removes event handlers and explicitly marks timeout or close.
internal enum SocksNavigationOutcome
{
    Pending, NetworkFailure, ExplicitCancellation, AbortedWithoutStop,
    UnexpectedSuccess, InvalidError, TimedOut, BrowserClosed
}

internal readonly record struct SocksNavigationSnapshot(
    SocksNavigationOutcome Outcome, CefErrorCode Error, int WrongNavigationEvents,
    int DuplicateEvents, int LateEvents, bool ContradictorySuccess);

internal sealed class SocksNavigationObserver(string expectedUrl)
{
    private readonly object gate = new();
    private readonly TaskCompletionSource<SocksNavigationOutcome> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SocksNavigationOutcome outcome;
    private CefErrorCode error;
    private bool cancellationRequested, contradictorySuccess;
    private int wrongNavigationEvents, duplicateEvents, lateEvents;

    internal Task<SocksNavigationOutcome> Completion => completion.Task;
    internal SocksNavigationSnapshot Snapshot
    {
        get { lock (gate) return new(outcome, error, wrongNavigationEvents, duplicateEvents, lateEvents, contradictorySuccess); }
    }

    internal void NoteCancellationRequested() { lock (gate) cancellationRequested = true; }
    // Called only after the bounded wait has timed out. A callback can race
    // between that timeout and this lock; it must not relabel the wait as a
    // successful network failure. Preserve any contradictory success separately.
    internal void MarkTimedOut()
    {
        lock (gate)
        {
            if (outcome == SocksNavigationOutcome.UnexpectedSuccess) contradictorySuccess = true;
            outcome = SocksNavigationOutcome.TimedOut;
            completion.TrySetResult(SocksNavigationOutcome.TimedOut);
        }
    }
    internal void MarkBrowserClosed() { lock (gate) Finish(SocksNavigationOutcome.BrowserClosed); }

    internal void ObserveError(bool isMainFrame, string? url, CefErrorCode code)
    {
        lock (gate)
        {
            if (!Matches(isMainFrame, url)) return;
            if (outcome != SocksNavigationOutcome.Pending) { RecordLate(); return; }
            error = code;
            Finish(code switch
            {
                CefErrorCode.None => SocksNavigationOutcome.InvalidError,
                CefErrorCode.Aborted when cancellationRequested => SocksNavigationOutcome.ExplicitCancellation,
                CefErrorCode.Aborted => SocksNavigationOutcome.AbortedWithoutStop,
                _ => SocksNavigationOutcome.NetworkFailure
            });
        }
    }

    internal void ObserveEnd(bool isMainFrame, string? url, int statusCode)
    {
        lock (gate)
        {
            if (!Matches(isMainFrame, url) || statusCode < 200 || statusCode >= 400) return;
            if (outcome != SocksNavigationOutcome.Pending)
            {
                contradictorySuccess = true;
                RecordLate();
                return;
            }
            Finish(SocksNavigationOutcome.UnexpectedSuccess);
        }
    }

    private bool Matches(bool isMainFrame, string? url)
    {
        if (isMainFrame && string.Equals(expectedUrl, url, StringComparison.Ordinal)) return true;
        wrongNavigationEvents++;
        return false;
    }

    private void Finish(SocksNavigationOutcome next)
    {
        if (outcome != SocksNavigationOutcome.Pending) return;
        outcome = next;
        completion.TrySetResult(next);
    }

    private void RecordLate()
    {
        if (outcome is SocksNavigationOutcome.TimedOut or SocksNavigationOutcome.BrowserClosed) lateEvents++;
        else duplicateEvents++;
    }
}
