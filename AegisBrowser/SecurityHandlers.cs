using AegisBrowser.Core;
using CefSharp;
using CefSharp.Handler;

namespace AegisBrowser;

// Chromium owns the origin-labelled consent UI and cancellation lifecycle. We never
// manufacture a grant or persist an application-side permission decision.
internal sealed class OriginPermissions(Action<string> report) : PermissionHandler
{
    protected override bool OnShowPermissionPrompt(IWebBrowser control, IBrowser browser, ulong promptId,
        string origin, PermissionRequestType permissions, IPermissionPromptCallback callback)
    {
        const PermissionRequestType supported = PermissionRequestType.Geolocation | PermissionRequestType.Notifications;
        using var main = browser.MainFrame;
        if (permissions != PermissionRequestType.None && (permissions & ~supported) == 0
            && BrowserPolicy.CanPromptPermission(origin, main.Url))
        {
            callback.Dispose();
            report($"Permission decision requested for {BrowserPolicy.DisplayOrigin(origin)}: {permissions}");
            return false; // Chrome runtime displays its native, origin-scoped permission UI.
        }
        using (callback) callback.Continue(PermissionRequestResult.Deny);
        report($"Permission blocked: {permissions} ({origin})");
        return true;
    }

    protected override bool OnRequestMediaAccessPermission(IWebBrowser control, IBrowser browser, IFrame frame,
        string origin, MediaAccessPermissionType permissions, IMediaAccessCallback callback)
    {
        const MediaAccessPermissionType supported = MediaAccessPermissionType.AudioCapture | MediaAccessPermissionType.VideoCapture;
        using var main = browser.MainFrame;
        if (permissions != MediaAccessPermissionType.None && (permissions & ~supported) == 0
            && BrowserPolicy.CanPromptPermission(origin, main.Url)
            && BrowserPolicy.CanPromptPermission(frame.Url, main.Url))
        {
            callback.Dispose();
            report($"Media decision requested for {BrowserPolicy.DisplayOrigin(origin)}: {permissions}");
            return false; // Native device consent. No blanket capture grant or screen-selection bypass.
        }
        using (callback) callback.Cancel();
        report($"Media access blocked ({origin})");
        return true;
    }
}

internal sealed class SafeRequests(Action<string> report) : RequestHandler
{
    protected override bool OnBeforeBrowse(IWebBrowser control, IBrowser browser, IFrame frame,
        IRequest request, bool userGesture, bool isRedirect)
    {
        if (!frame.IsMain || BrowserPolicy.IsWebUrl(request.Url) || request.Url == "about:blank") return false;
        report("Navigation blocked: unsupported scheme or embedded credentials.");
        return true;
    }

    protected override bool OnOpenUrlFromTab(IWebBrowser control, IBrowser browser, IFrame frame,
        string url, WindowOpenDisposition disposition, bool userGesture)
    {
        // Preserve CEF's disposition/opener handling instead of synthesizing an unrelated tab.
        return !BrowserPolicy.IsWebUrl(url) && url != "about:blank";
    }

    protected override void OnRenderProcessTerminated(IWebBrowser control, IBrowser browser,
        CefTerminationStatus status, int errorCode, string errorMessage)
    {
        report($"Renderer stopped: {status} ({errorCode}). Reload to recover; unsaved edits may be lost.");
    }

    protected override bool OnCertificateError(IWebBrowser control, IBrowser browser, CefErrorCode code,
        string url, ISslInfo sslInfo, IRequestCallback callback)
    {
        callback.Dispose();
        report($"Certificate rejected: {code}");
        return false;
    }
}

internal sealed class SafePopups(Action<string> report) : LifeSpanHandler
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, IBrowser> popups = new();
    private volatile bool closing;
    internal int PopupCount => popups.Count;

    internal void ClosePopups()
    {
        closing = true;
        Cef.UIThreadTaskFactory.StartNew(() =>
        {
            foreach (var popup in popups.Values)
                if (!popup.IsDisposed) popup.GetHost().CloseBrowser(true);
        });
    }

    protected override void OnAfterCreated(IWebBrowser control, IBrowser browser)
    {
        if (!browser.IsPopup) return;
        popups[browser.Identifier] = browser;
        if (closing) browser.GetHost().CloseBrowser(true);
    }

    protected override void OnBeforeClose(IWebBrowser control, IBrowser browser) =>
        popups.TryRemove(browser.Identifier, out _);

    protected override bool OnBeforePopup(IWebBrowser control, IBrowser browser, IFrame frame,
        string targetUrl, string targetFrameName, WindowOpenDisposition disposition, bool userGesture,
        IPopupFeatures features, IWindowInfo windowInfo, IBrowserSettings settings,
        ref bool noJavascriptAccess, out IWebBrowser newBrowser)
    {
        newBrowser = null!;
        // Keep CEF's original browsing context: window.opener, POST data, named-window
        // reuse and postMessage are required by OAuth. CEF still enforces COOP/SOP.
        if (!closing && userGesture && (string.IsNullOrEmpty(targetUrl) || targetUrl == "about:blank"
            || BrowserPolicy.IsWebUrl(targetUrl))) return false;
        report("Popup blocked: a user gesture and a web destination are required.");
        return true;
    }
}

internal sealed class DenyDownloads(Action<string> report) : DownloadHandler
{
    protected override bool CanDownload(IWebBrowser control, IBrowser browser, string url, string method)
    {
        report("Downloads are disabled in this version.");
        return false;
    }
}

internal sealed class DiagnosticsFactory : ISchemeHandlerFactory
{
    public IResourceHandler Create(IBrowser browser, IFrame frame, string schemeName, IRequest request)
    {
        using var stream = typeof(DiagnosticsFactory).Assembly.GetManifestResourceStream("AegisBrowser.diagnostics.html")
            ?? throw new InvalidOperationException("Diagnostics resource missing.");
        using var reader = new StreamReader(stream);
        return ResourceHandler.FromString(reader.ReadToEnd(), mimeType: "text/html");
    }
}
