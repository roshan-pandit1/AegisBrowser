using CefSharp;

namespace AegisBrowser;

// Operates on the supplied profile context only; never removes Chromium files.
internal static class ProfileCookieClearing
{
    internal static async Task ClearAsync(IRequestContext context)
    {
        using var cookies = context.GetCookieManager(null)
            ?? throw new InvalidOperationException("The profile cookie manager is unavailable.");
        // The callback count may be unknown (-1); acceptance and callback
        // completion, not that count, indicate that the operation completed.
        using var completion = new TaskDeleteCookiesCallback();
        if (!cookies.DeleteCookies("", "", completion))
            throw new InvalidOperationException("Chromium could not start cookie deletion.");
        await completion.Task;
        if (!await cookies.FlushStoreAsync())
            throw new InvalidOperationException("Chromium could not confirm that the cookie store was saved.");
    }

    internal static async Task ClearCurrentProfileAsync()
    {
        using var context = Cef.GetGlobalRequestContext()
            ?? throw new InvalidOperationException("The profile request context is unavailable.");
        await ClearAsync(context);
    }
}
