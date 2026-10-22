using CefSharp;
using CefSharp.WinForms;

namespace AegisBrowser;

// The native child must finish closing before the adapter is disposed: Dispose
// itself requests CloseBrowser, so disposing at DoClose can queue a second close.
internal sealed class BrowserView(string address, IRequestContext? requestContext = null)
    : ChromiumWebBrowser(address, requestContext: requestContext)
{
    private Task? releaseTask;

    internal Task ReleaseNativeHostAsync()
    {
        if (InvokeRequired) throw new InvalidOperationException("Release the browser host on its WinForms thread.");
        return releaseTask ??= ReleaseNativeHostCoreAsync();
    }

    private async Task ReleaseNativeHostCoreAsync()
    {
        var lifetime = (SafePopups)LifeSpanHandler;
        await lifetime.OwnerCloseReady;
        if (IsHandleCreated) DestroyHandle();
        await lifetime.OwnerClosed.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
