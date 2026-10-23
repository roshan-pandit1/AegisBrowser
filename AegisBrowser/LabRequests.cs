using AegisBrowser.Core;
using AegisBrowser.Lab;
using CefSharp;
using CefSharp.Handler;
using System.Diagnostics;

namespace AegisBrowser;

internal sealed class LabRequests(LabServer server, PrivacySession privacy) : RequestHandler
{
    protected override bool OnBeforeBrowse(IWebBrowser c, IBrowser b, IFrame f, IRequest r, bool gesture, bool redirect)
        => !server.Contains(r.Url) && r.Url != "about:blank";
    protected override bool OnCertificateError(IWebBrowser c, IBrowser b, CefErrorCode error, string url, ISslInfo ssl, IRequestCallback callback)
    {
        server.Report.Add(LabEventKind.TlsFailure, url, 0, error.ToString());
        callback.Dispose(); return false;
    }
    protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser c, IBrowser b, IFrame f, IRequest r,
        bool navigation, bool download, string initiator, ref bool disableDefaultHandling)
    {
        using var main = b.MainFrame;
        return new Resource(server, privacy, r.ResourceType == ResourceType.MainFrame ? r.Url : main.Url);
    }
    private sealed class Resource(LabServer server, PrivacySession privacy, string top) : ResourceRequestHandler
    {
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser c, IBrowser b, IFrame f, IRequest r, IRequestCallback callback)
        {
            var decision = privacy.Evaluate(new(r.Url, top, r.ResourceType == ResourceType.Script ? "script" : "other", r.ResourceType == ResourceType.MainFrame));
            var blocked = !server.Contains(r.Url) || decision.Blocked;
            server.Report.Add(r.ResourceType == ResourceType.MainFrame ? LabEventKind.Navigation : LabEventKind.Request, r.Url, 0, blocked ? "Blocked" : "Allowed", requestId: r.Identifier);
            return blocked ? CefReturnValue.Cancel : CefReturnValue.Continue;
        }
        protected override void OnResourceRedirect(IWebBrowser c, IBrowser b, IFrame f, IRequest r, IResponse response, ref string newUrl)
        {
            server.Report.Add(LabEventKind.Redirect, r.Url, response.StatusCode, "Redirect", elapsed.ElapsedMilliseconds, r.Identifier);
            if (!server.Contains(newUrl)) newUrl = "about:blank";
        }
        protected override void OnResourceLoadComplete(IWebBrowser c, IBrowser b, IFrame f, IRequest r, IResponse response, UrlRequestStatus status, long length)
        {
            server.Report.Add(LabEventKind.Complete, r.Url, response.StatusCode, status.ToString(), elapsed.ElapsedMilliseconds, r.Identifier);
            if (response.ErrorCode != CefErrorCode.None && response.ErrorCode != CefErrorCode.Aborted)
                server.Report.Add(LabEventKind.NetworkFailure, r.Url, response.StatusCode, response.ErrorCode.ToString(), elapsed.ElapsedMilliseconds, r.Identifier);
        }
    }
}
