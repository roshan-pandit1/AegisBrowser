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
        // A timeout must not permanently cache a failed wait. A later explicit close can
        // wait for the same native close completion; it does not re-dispose the adapter.
        if (releaseTask is { IsFaulted: true } && releaseTask.Exception?.GetBaseException() is TimeoutException)
            releaseTask = null;
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
