using CefSharp;
using CefSharp.Internals;

namespace AegisBrowser;

// Chromium's profile preference is applied on its UI thread before any tab
// is created. This is separate from Aegis resource filtering.
internal static class ThirdPartyCookiePolicy
{
    internal const string Preference = "profile.cookie_controls_mode";
    private const int BlockThirdParty = 1;

    internal readonly record struct Result(bool Succeeded, string Status, string? Error);
    internal static bool PreventBrowsing(bool? requested, Result result) => requested == true && !result.Succeeded;
    internal interface IPreference
    {
        bool Exists { get; }
        bool Writable { get; }
        bool Set(object? value, out string error);
        object? Value { get; }
    }

    private sealed class CefPreference(IRequestContext context) : IPreference
    {
        public bool Exists => context.HasPreference(Preference);
        public bool Writable => context.CanSetPreference(Preference);
        public bool Set(object? value, out string error) => context.SetPreference(Preference, value, out error);
        public object? Value => context.GetPreference(Preference);
    }

    internal static Result ApplyAtStartup(bool? requested)
    {
        if (requested is null) return new(true, "Existing Chromium setting (no Aegis override)", null);
        using var context = Cef.GetGlobalRequestContext();
        return CefThread.CurrentlyOnUiThread
            ? ApplyOnUiThread(context, requested.Value)
            : Cef.UIThreadTaskFactory.StartNew(() => ApplyOnUiThread(context, requested.Value)).GetAwaiter().GetResult();
    }

    // Caller must be on the CEF UI thread. Reset to the Chromium default when
    // an explicit opt-in is removed; do not force the "allow all" mode.
    internal static Result ApplyOnUiThread(IRequestContext context, bool requested)
        => Apply(new CefPreference(context), requested);

    internal static Result Apply(IPreference preference, bool requested)
    {
        if (!preference.Exists || !preference.Writable)
            return new(false, "Unavailable", "Chromium does not expose a writable cookie-controls preference.");
        if (!preference.Set(requested ? BlockThirdParty : null, out var error))
            return new(false, "Unavailable", "Chromium rejected the cookie-controls preference: " + error);
        if (requested && preference.Value is not BlockThirdParty)
            return new(false, "Unavailable", "Chromium did not retain the requested cookie-controls preference.");
        return requested
            ? new(true, "Third-party cookie blocking enabled at startup", null)
            : new(true, "Browser default restored at startup", null);
    }
}
