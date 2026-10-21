using AegisBrowser.Core;
using CefSharp;
using CefSharp.Handler;

namespace AegisBrowser;

// No asynchronous permission grants to survive navigation or closure: all requests fail closed.
internal sealed class DenyPermissions(Action<string> report) : PermissionHandler
{
    protected override bool OnShowPermissionPrompt(IWebBrowser control, IBrowser browser, ulong promptId,
        string origin, PermissionRequestType permissions, IPermissionPromptCallback callback)
    {
        using (callback) callback.Continue(PermissionRequestResult.Deny);
        report($"Permission blocked: {permissions} ({origin})");
        return true;
    }

    protected override bool OnRequestMediaAccessPermission(IWebBrowser control, IBrowser browser, IFrame frame,
        string origin, MediaAccessPermissionType permissions, IMediaAccessCallback callback)
    {
        using (callback) callback.Cancel();
        report($"Media access blocked ({origin})");
        return true;
    }
}

internal sealed class SafeRequests(Action<string> openTab, Action<string> report, Func<NetworkRequest, FilterDecision>? filter = null,
    Action<NetworkRequest, FilterDecision>? observe = null) : RequestHandler
{
    // During document replacement CEF can expose an empty MainFrame.Url to early
    // subresource callbacks. Keep only URL strings, never native frame wrappers.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> navigationSites = new();

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
        return new FilteringResourceHandler(filter, top, observe);
    }

    protected override bool OnBeforeBrowse(IWebBrowser control, IBrowser browser, IFrame frame,
        IRequest request, bool userGesture, bool isRedirect)
    {
        if (!frame.IsMain) return false;
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
        if (userGesture && BrowserPolicy.IsWebUrl(url)) openTab(url);
        return true;
    }

    protected override bool OnCertificateError(IWebBrowser control, IBrowser browser, CefErrorCode code,
        string url, ISslInfo sslInfo, IRequestCallback callback)
    {
        callback.Dispose();
        report($"Certificate rejected: {code}");
        return false;
    }
}

internal sealed class SafePopups(Action<string> openTab, Action<string> report) : LifeSpanHandler
{
    protected override bool OnBeforePopup(IWebBrowser control, IBrowser browser, IFrame frame,
        string targetUrl, string targetFrameName, WindowOpenDisposition disposition, bool userGesture,
        IPopupFeatures features, IWindowInfo windowInfo, IBrowserSettings settings,
        ref bool noJavascriptAccess, out IWebBrowser newBrowser)
    {
        newBrowser = null!;
        if (userGesture && BrowserPolicy.IsWebUrl(targetUrl)) openTab(targetUrl);
        else report("Popup blocked.");
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
