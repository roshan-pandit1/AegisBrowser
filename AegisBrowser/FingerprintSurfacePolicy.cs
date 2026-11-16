using CefSharp;

namespace AegisBrowser;

/// <summary>
/// Narrow, per-browser CEF restrictions that reduce exposure of selected
/// fingerprintable rendering surfaces. These are not a Tor Browser port,
/// hardware identity modifications, or a promise of browser unlinkability.
/// </summary>
internal static class FingerprintSurfacePolicy
{
    internal static IBrowserSettings? CreateSettings(bool disableWebGl, bool disableRemoteFonts)
    {
        // Null preserves the unmodified Chromium defaults for older profiles
        // and for profiles that do not opt into either compatibility tradeoff.
        if (!disableWebGl && !disableRemoteFonts) return null;

        // ChromiumWebBrowser takes ownership through autoDispose, and the
        // settings must be installed before the native browser is created.
        return new BrowserSettings(autoDispose: true)
        {
            WebGl = disableWebGl ? CefState.Disabled : CefState.Default,
            RemoteFonts = disableRemoteFonts ? CefState.Disabled : CefState.Default
        };
    }
}
