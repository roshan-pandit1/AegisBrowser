namespace AegisBrowser;

internal static class PreviewStatus
{
    internal const string Label = "DEVELOPMENT PREVIEW";
    internal const string Details = "This preview uses a Chromium build whose current security-fix coverage has not been qualified. " +
        "The stock CefSharp binding does not support the CEF renderer sandbox. " +
        "Protected-proxy profiles remain blocked until containment is independently verified.";
}
