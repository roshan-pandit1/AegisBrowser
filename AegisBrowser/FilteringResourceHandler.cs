using AegisBrowser.Core;
using CefSharp;
using CefSharp.Handler;

namespace AegisBrowser;

internal sealed class FilteringResourceHandler(Func<RequestFilter> filter, string? topUrl,
    Action<NetworkRequest, FilterDecision>? observe = null) : ResourceRequestHandler
{
    private static string Type(ResourceType type) => type.ToString() switch
    {
        "Script" => "script", "Stylesheet" => "stylesheet", "Image" or "Favicon" => "image",
        "FontResource" => "font", "Media" => "media", "Xhr" => "xmlhttprequest",
        "SubFrame" => "subdocument", "Ping" => "ping", _ => "other"
    };
    protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser control, IBrowser browser,
        IFrame frame, IRequest request, IRequestCallback callback)
    {
        var input = new NetworkRequest(request.Url, topUrl, Type(request.ResourceType), request.ResourceType == ResourceType.MainFrame);
        var decision = filter().Evaluate(input);
        observe?.Invoke(input, decision);
        // Synchronous decision; never retain a CEF request, frame or callback.
        return decision.Blocked ? CefReturnValue.Cancel : CefReturnValue.Continue;
    }
}
