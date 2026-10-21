using CefSharp;
using CefSharp.WinForms;

namespace AegisBrowser;

// The native child must finish closing before the adapter is disposed: Dispose
// itself requests CloseBrowser, so disposing at DoClose can queue a second close.
internal sealed class BrowserView(string address, IRequestContext? requestContext = null)
    : ChromiumWebBrowser(address, requestContext: requestContext)
{
    internal async Task ReleaseNativeHostAsync()
    {
        var lifetime = (SafePopups)LifeSpanHandler;
        await lifetime.OwnerCloseReady;
        if (IsHandleCreated) DestroyHandle();
        await lifetime.OwnerClosed.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
