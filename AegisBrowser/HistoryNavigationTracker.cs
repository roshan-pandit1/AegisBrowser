using AegisBrowser.Core;
using CefSharp;

namespace AegisBrowser;

// CEF callbacks for main browsers and native popups share this tracker. It
// retains identifiers and sanitized addresses, never native browser objects.
internal sealed class HistoryNavigationTracker(SessionHistoryService history)
{
    private sealed class State(Guid tab, long generation)
    {
        internal readonly Guid Tab = tab;
        internal long Generation = generation;
        internal string? Committed;
        internal string? LatestAddress;
        internal bool Completed;
        internal bool Ending;
    }
    private readonly object gate = new();
    private readonly Dictionary<int, State> browsers = [];
    private bool closed;

    internal void BeforeBrowse(int browserId, bool redirect)
    {
        lock (gate)
        {
            if (closed || redirect) return;
            if (!browsers.TryGetValue(browserId, out var state))
            {
                var tab = Guid.NewGuid();
                browsers[browserId] = new State(tab, history.BeginNavigation(tab));
                return;
            }
            state.Generation = history.BeginNavigation(state.Tab);
            state.Committed = null;
            state.LatestAddress = null;
            state.Completed = false;
            state.Ending = false;
        }
    }

    internal void Start(FrameLoadStartEventArgs e)
    {
        if (!e.Frame.IsMain) return;
        lock (gate)
        {
            if (closed || !browsers.TryGetValue(e.Browser.Identifier, out var state)) return;
            state.Committed = SessionHistoryService.TrySanitizeUrl(e.Url, out var url) ? url : null;
            state.LatestAddress = state.Committed;
            var source = e.TransitionType & TransitionType.SourceMask;
            var backForward = (e.TransitionType & TransitionType.ForwardBack) != 0;
            history.SetReload(state.Tab, state.Generation, source == TransitionType.Reload && !backForward);
        }
    }

    internal void End(FrameLoadEndEventArgs e)
    {
        if (!e.Frame.IsMain) return;
        var browserId = e.Browser.Identifier;
        Guid tab; long generation; string committed;
        lock (gate)
        {
            if (closed || !browsers.TryGetValue(browserId, out var state) || state.Completed || state.Ending || state.Committed is null ||
                !SessionHistoryService.TrySanitizeUrl(e.Url, out var url) || (url != state.Committed && url != state.LatestAddress)) return;
            state.Ending = true;
            (tab, generation, committed) = (state.Tab, state.Generation, state.Committed);
        }
        // Notifications happen outside the tracker lock. pushState can change
        // the frame URL before load-end; record the committed source once.
        var recorded = history.CompleteNavigation(tab, generation, committed, e.HttpStatusCode);
        var latest = committed;
        lock (gate)
        {
            if (closed || !browsers.TryGetValue(browserId, out var state) || state.Generation != generation) return;
            state.Ending = false;
            state.Completed = recorded;
            if (!recorded) { state.Committed = null; state.LatestAddress = null; }
            else latest = state.LatestAddress ?? committed;
        }
        // CEF may report the title before load-end and not send it again.
        // Query the current document once the corresponding visit exists.
        if (recorded) QueryCurrentTitle(e.Browser, tab, generation, committed, latest);
    }

    internal void Address(AddressChangedEventArgs e)
    {
        lock (gate)
        {
            if (closed || !browsers.TryGetValue(e.Browser.Identifier, out var state) || state.Committed is null ||
                !SessionHistoryService.TrySanitizeUrl(e.Address, out var current)) return;
            state.LatestAddress = current;
        }
    }

    internal void Error(LoadErrorEventArgs e)
    {
        if (!e.Frame.IsMain) return;
        lock (gate)
        {
            if (closed || !browsers.TryGetValue(e.Browser.Identifier, out var state)) return;
            if (state.Committed is not null && SessionHistoryService.TrySanitizeUrl(e.FailedUrl, out var failed) && failed == state.Committed)
            {
                history.FailNavigation(state.Tab, state.Generation);
                state.Committed = null;
                state.LatestAddress = null;
            }
        }
    }

    internal void Title(TitleChangedEventArgs e)
    {
        Guid tab; long generation; string expected, latest;
        lock (gate)
        {
            if (closed || !browsers.TryGetValue(e.Browser.Identifier, out var state) || !state.Completed || state.Committed is null) return;
            (tab, generation, expected, latest) = (state.Tab, state.Generation, state.Committed, state.LatestAddress ?? state.Committed);
        }
        QueryCurrentTitle(e.Browser, tab, generation, expected, latest);
    }

    private void QueryCurrentTitle(IBrowser browser, Guid tab, long generation, string expected, string latest)
    {
        try
        {
            using var frame = browser.MainFrame;
            if (!SessionHistoryService.TrySanitizeUrl(frame.Url, out var current) || (current != expected && current != latest)) return;
            // A title callback has no navigation ID. Query the current document
            // rather than attributing its possibly late event payload to a page.
            var query = frame.EvaluateScriptAsync("document.title");
            _ = UpdateCurrentTitle(query, tab, generation);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    private async Task UpdateCurrentTitle(Task<JavascriptResponse> query, Guid tab, long generation)
    {
        try
        {
            var response = await query.ConfigureAwait(false);
            if (response.Success && response.Result is string title) history.UpdateTitle(tab, generation, title);
        }
        catch (Exception) { /* A closing renderer need not provide a title. */ }
    }

    internal void BrowserClosed(int browserId)
    {
        lock (gate)
        {
            if (browsers.Remove(browserId, out var state)) history.CloseTab(state.Tab);
        }
    }

    internal void Close()
    {
        lock (gate)
        {
            closed = true;
            foreach (var state in browsers.Values) history.CloseTab(state.Tab);
            browsers.Clear();
        }
    }
}

// CefSharp only raises control events for the main browser. Its explicit
// handlers also receive native popup callbacks; keep the existing main-browser
// event path intact and observe popup events here without owning the popup.
internal sealed class HistoryPopupLoadHandler(HistoryNavigationTracker tracker) : CefSharp.Handler.LoadHandler
{
    protected override void OnFrameLoadStart(IWebBrowser browser, FrameLoadStartEventArgs e)
    { if (e.Browser.IsPopup) tracker.Start(e); }
    protected override void OnFrameLoadEnd(IWebBrowser browser, FrameLoadEndEventArgs e)
    { if (e.Browser.IsPopup) tracker.End(e); }
    protected override void OnLoadError(IWebBrowser browser, LoadErrorEventArgs e)
    { if (e.Browser.IsPopup) tracker.Error(e); }
}

internal sealed class HistoryPopupDisplayHandler(HistoryNavigationTracker tracker) : CefSharp.Handler.DisplayHandler
{
    protected override void OnAddressChanged(IWebBrowser browser, AddressChangedEventArgs e)
    { if (e.Browser.IsPopup) tracker.Address(e); }
    protected override void OnTitleChanged(IWebBrowser browser, TitleChangedEventArgs e)
    { if (e.Browser.IsPopup) tracker.Title(e); }
}
