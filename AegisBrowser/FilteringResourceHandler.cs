using AegisBrowser.Core;
using CefSharp;
using CefSharp.Handler;

namespace AegisBrowser;

internal sealed class FilteringResourceHandler(Func<NetworkRequest, FilterDecision> filter, string? topUrl,
    Action<NetworkRequest, FilterDecision>? observe = null, NavigationDiagnostics? diagnostics = null) : ResourceRequestHandler
{
    protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser control, IBrowser browser,
        IFrame frame, IRequest request, IRequestCallback callback)
    {
        var classification = ResourceClassification.FromCefName(request.ResourceType.ToString());
        var input = new NetworkRequest(request.Url, topUrl, classification.Type,
            request.ResourceType == ResourceType.MainFrame, classification.Unknown);
        var decision = filter(input);
        observe?.Invoke(input, decision);
        diagnostics?.Observe(d => d.Record(decision.Blocked ? DiagnosticKind.FilterBlocked : DiagnosticKind.FilterAllowed, request.Url,
            browser?.Identifier ?? 0, request.Identifier, (int)request.ResourceType, rule: NavigationDiagnostics.Category(decision)));
        // Synchronous decision; never retain a CEF request, frame or callback.
        return decision.Blocked ? CefReturnValue.Cancel : CefReturnValue.Continue;
    }
    protected override void OnResourceRedirect(IWebBrowser control, IBrowser browser, IFrame frame,
        IRequest request, IResponse response, ref string newUrl)
    {
        var destination = newUrl; // Never mutate CEF's redirect destination.
        diagnostics?.Observe(d => d.Record(DiagnosticKind.Redirect, request.Url, browser?.Identifier ?? 0, request.Identifier,
            (int)request.ResourceType, response.StatusCode, redirectTo: destination));
    }

    protected override void OnResourceLoadComplete(IWebBrowser control, IBrowser browser, IFrame frame,
        IRequest request, IResponse response, UrlRequestStatus status, long receivedContentLength)
        => diagnostics?.Observe(d => d.Record(DiagnosticKind.ResourceComplete, request.Url, browser?.Identifier ?? 0, request.Identifier,
            (int)request.ResourceType, response.StatusCode, (int)response.ErrorCode, outcome: (int)status));
}
