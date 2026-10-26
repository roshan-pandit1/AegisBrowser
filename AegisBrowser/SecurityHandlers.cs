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
        report($"Permission blocked: {permissions} ({BrowserPolicy.DisplayOrigin(origin)})");
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
        report($"Media access blocked ({BrowserPolicy.DisplayOrigin(origin)})");
        return true;
    }
}

internal sealed class SafeRequests(Action<string> report, Func<NetworkRequest, FilterDecision>? filter = null, Action<NetworkRequest, FilterDecision>? observe = null, NavigationDiagnostics? diagnostics = null) : RequestHandler
{
    // During document replacement CEF can expose an empty MainFrame.Url to early
    // subresource callbacks. Keep only URL strings, never native frame wrappers.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> navigationSites = new();

    internal void ForgetBrowser(int identifier) => navigationSites.TryRemove(identifier, out _);

    protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser control, IBrowser browser,
        IFrame frame, IRequest request, bool isNavigation, bool isDownload, string requestInitiator,
        ref bool disableDefaultHandling)
    {
        if (filter is null) return null!;
        using var main = browser?.MainFrame;
        var top = request.ResourceType == ResourceType.MainFrame ? request.Url : main?.Url;
        if (browser is not null)
        {
            if (request.ResourceType == ResourceType.MainFrame) navigationSites[browser.Identifier] = request.Url;
            else if (SiteClassifier.Host(top) is null) navigationSites.TryGetValue(browser.Identifier, out top);
        }
        return new FilteringResourceHandler(filter, top, observe, diagnostics);
    }

    protected override bool OnBeforeBrowse(IWebBrowser control, IBrowser browser, IFrame frame,
        IRequest request, bool userGesture, bool isRedirect)
    {
        if (!frame.IsMain) return false;
        diagnostics?.Observe(d => d.Record(DiagnosticKind.Navigation, request.Url, browser.Identifier, request.Identifier, (int)request.ResourceType));
        if (BrowserPolicy.IsWebUrl(request.Url) || request.Url == "about:blank")
        {
            navigationSites[browser.Identifier] = request.Url;
            return false;
        }
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
        diagnostics?.Observe(d => d.Record(DiagnosticKind.CertificateRejected, url, browser?.Identifier ?? 0, error: (int)code));
        report($"Certificate rejected: {code}");
        return false;
    }
}

internal sealed class SafePopups(Action<string> report, Action? ownerCloseReady = null, bool blockAllPopups = false) : LifeSpanHandler
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, IBrowser> popups = new();
    private volatile bool closing;
    private readonly TaskCompletionSource<bool> closeReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task OwnerCloseReady => closeReady.Task;
    private readonly TaskCompletionSource<bool> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task OwnerClosed => closed.Task;

    private bool ownerClosing; // Accessed only on the CEF UI thread.

    private void CompleteOwnerClose()
    {
        if (ownerClosing && popups.IsEmpty && closeReady.TrySetResult(true)) ownerCloseReady?.Invoke();
    }

    protected override bool DoClose(IWebBrowser control, IBrowser browser)
    {
        if (browser.IsPopup) return false;
        // Suppress CEF's WM_CLOSE to the top-level WinForms owner. Release the
        // embedded host only after this native close callback has returned.
        Cef.UIThreadTaskFactory.StartNew(() =>
        {
            ownerClosing = true;
            CompleteOwnerClose();
        });
        return true;
    }

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

    protected override void OnBeforeClose(IWebBrowser control, IBrowser browser)
    {
        if (control.RequestHandler is SafeRequests requests) requests.ForgetBrowser(browser.Identifier);
        if (browser.IsPopup)
        {
            popups.TryRemove(browser.Identifier, out _);
            // Keep the embedded owner and its shared handler alive until all
            // native popup close callbacks have finished.
            Cef.UIThreadTaskFactory.StartNew(CompleteOwnerClose);
        }
        else Cef.UIThreadTaskFactory.StartNew(() => closed.TrySetResult(true));
    }

    protected override bool OnBeforePopup(IWebBrowser control, IBrowser browser, IFrame frame,
        string targetUrl, string targetFrameName, WindowOpenDisposition disposition, bool userGesture,
        IPopupFeatures features, IWindowInfo windowInfo, IBrowserSettings settings,
        ref bool noJavascriptAccess, out IWebBrowser newBrowser)
    {
        newBrowser = null!;
        // Keep CEF's original browsing context: window.opener, POST data, named-window
        // reuse and postMessage are required by OAuth. CEF still enforces COOP/SOP.
        if (!blockAllPopups && !closing && userGesture && (string.IsNullOrEmpty(targetUrl) || targetUrl == "about:blank"
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
    internal const string ContentPolicy = "default-src 'none'; script-src 'sha256-Vr16WnsRDYXpmpdDKnVzCiZXqq9mxdyGweG4e2TxPiU='; style-src 'sha256-vqvBJtFIvTyLNPQhSyytLy/wqz7DM/56idLI0X6a04Q='; connect-src 'none'; img-src 'none'; frame-src 'none'; frame-ancestors 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
    public IResourceHandler Create(IBrowser browser, IFrame frame, string schemeName, IRequest request)
    {
        var permitted = BrowserPolicy.CanServeDiagnostics(request.Url, request.Method,
            frame?.IsMain == true, request.ResourceType == ResourceType.MainFrame);
        string body = "Diagnostics are available only as a top-level local page.";
        if (permitted)
        {
            using var stream = typeof(DiagnosticsFactory).Assembly.GetManifestResourceStream("AegisBrowser.diagnostics.html")
                ?? throw new InvalidOperationException("Diagnostics resource missing.");
            using var reader = new StreamReader(stream);
            body = reader.ReadToEnd();
        }
        var resource = new ResourceHandler(permitted ? "text/html" : "text/plain",
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)), autoDisposeStream: true, charset: "utf-8")
        { StatusCode = permitted ? 200 : 403, StatusText = permitted ? "OK" : "Forbidden" };
        resource.Headers.Clear(); // ResourceHandler's defaults must not grant cross-origin access.
        resource.Headers["Content-Security-Policy"] = ContentPolicy;
        resource.Headers["X-Frame-Options"] = "DENY";
        resource.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        resource.Headers["X-Content-Type-Options"] = "nosniff";
        resource.Headers["Cache-Control"] = "no-store";
        resource.Headers["Referrer-Policy"] = "no-referrer";
        return resource;
    }
}
