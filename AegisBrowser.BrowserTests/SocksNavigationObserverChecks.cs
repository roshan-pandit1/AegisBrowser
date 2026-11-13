using CefSharp;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Deterministic negative checks; these exercise the test observer, not CEF.
    private static void VerifySocksNavigationObserver()
    {
        const string expected = "http://aegis-socks.invalid/offline";
        var missing = new SocksNavigationObserver(expected);
        missing.ObserveError(true, "http://aegis-socks.invalid/other", CefErrorCode.ProxyConnectionFailed);
        missing.ObserveError(false, expected, CefErrorCode.ProxyConnectionFailed);
        Check(!missing.Completion.IsCompleted && missing.Snapshot.WrongNavigationEvents == 2,
            "wrong frame or URL cannot satisfy SOCKS navigation");
        missing.MarkTimedOut();
        missing.ObserveError(true, expected, CefErrorCode.ProxyConnectionFailed);
        Check(missing.Snapshot.Outcome == SocksNavigationOutcome.TimedOut && missing.Snapshot.LateEvents == 1,
            "missing callback times out and late error cannot change result");
        missing.ObserveEnd(true, expected, 200);
        Check(missing.Snapshot.Outcome == SocksNavigationOutcome.TimedOut && missing.Snapshot.ContradictorySuccess,
            "late success remains visible without overturning timeout");

        var timeoutRace = new SocksNavigationObserver(expected);
        timeoutRace.ObserveError(true, expected, CefErrorCode.ProxyConnectionFailed);
        // Model a callback arriving between WaitAsync timing out and the catch
        // acquiring the observer lock to record that timeout.
        timeoutRace.MarkTimedOut();
        timeoutRace.ObserveError(true, expected, CefErrorCode.Aborted);
        Check(timeoutRace.Snapshot.Outcome == SocksNavigationOutcome.TimedOut && timeoutRace.Snapshot.LateEvents == 1,
            "callback racing timeout recording cannot relabel timeout");

        var successful = new SocksNavigationObserver(expected);
        successful.ObserveEnd(true, expected, 204);
        Check(successful.Snapshot.Outcome == SocksNavigationOutcome.UnexpectedSuccess,
            "successful SOCKS failure navigation is rejected");

        var network = new SocksNavigationObserver(expected);
        network.ObserveError(true, expected, CefErrorCode.ProxyConnectionFailed);
        network.ObserveError(true, expected, CefErrorCode.ProxyConnectionFailed);
        network.ObserveEnd(true, expected, 0);
        Check(network.Snapshot.Outcome == SocksNavigationOutcome.NetworkFailure && network.Snapshot.DuplicateEvents == 1,
            "duplicate failure cannot complete twice and HTTP zero is not success");
        network.ObserveEnd(true, expected, 200);
        Check(network.Snapshot.ContradictorySuccess, "success following a failure remains a violation");

        var cancellation = new SocksNavigationObserver(expected);
        cancellation.NoteCancellationRequested();
        cancellation.ObserveError(true, expected, CefErrorCode.Aborted);
        Check(cancellation.Snapshot.Outcome == SocksNavigationOutcome.ExplicitCancellation,
            "explicit cancellation is separate from a network failure");
        var aborted = new SocksNavigationObserver(expected);
        aborted.ObserveError(true, expected, CefErrorCode.Aborted);
        Check(aborted.Snapshot.Outcome == SocksNavigationOutcome.AbortedWithoutStop,
            "unrequested abort is not a network failure");
        var invalid = new SocksNavigationObserver(expected);
        invalid.ObserveError(true, expected, CefErrorCode.None);
        Check(invalid.Snapshot.Outcome == SocksNavigationOutcome.InvalidError,
            "empty error is not a network failure");
        var closed = new SocksNavigationObserver(expected);
        closed.MarkBrowserClosed();
        closed.ObserveError(true, expected, CefErrorCode.Aborted);
        Check(closed.Snapshot.Outcome == SocksNavigationOutcome.BrowserClosed && closed.Snapshot.LateEvents == 1,
            "closing before a terminal outcome rejects a late abort");
    }
}
