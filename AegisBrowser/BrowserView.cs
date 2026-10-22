using System.ComponentModel;
using System.Runtime.InteropServices;
using CefSharp;
using CefSharp.WinForms;

namespace AegisBrowser;

// The native child must finish closing before the adapter is disposed: Dispose
// itself requests CloseBrowser, so disposing at DoClose can queue a second close.
internal sealed class BrowserView(string address, IRequestContext? requestContext = null)
    : ChromiumWebBrowser(address, requestContext: requestContext)
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

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
        // CEF created the native child on its UI thread. Destroying its parent
        // from WinForms can race Aura callbacks on that other thread.
        await Cef.UIThreadTaskFactory.StartNew(() =>
        {
            using var host = this.GetBrowserHost();
            var child = host.GetWindowHandle();
            // Hide first so Aura clears activation/focus while its window tree
            // is still valid. Destruction alone can query a detached toplevel.
            if (child != IntPtr.Zero) ShowWindow(child, 0); // SW_HIDE
            if (child == IntPtr.Zero || !DestroyWindow(child))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not release the Chromium child window.");
        });
        await lifetime.OwnerClosed.WaitAsync(TimeSpan.FromSeconds(10));
        if (IsHandleCreated) DestroyHandle();
    }
}
