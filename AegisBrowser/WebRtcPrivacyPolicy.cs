using CefSharp;
using CefSharp.Internals;

namespace AegisBrowser;

// Profile-process preference, applied before opening tabs. It is not network containment.
internal static class WebRtcPrivacyPolicy
{
    internal const string Preference = "webrtc.ip_handling_policy";
    internal const string RestrictNonProxiedUdp = "disable_non_proxied_udp";
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
        try
        {
            using var context = Cef.GetGlobalRequestContext();
            return CefThread.CurrentlyOnUiThread
                ? ApplyOnUiThread(context, requested.Value)
                : Cef.UIThreadTaskFactory.StartNew(() => ApplyOnUiThread(context, requested.Value)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (IsPreferenceError(ex)) { return Unavailable(ex); }
    }
    internal static Result ApplyOnUiThread(IRequestContext context, bool requested) => Apply(new CefPreference(context), requested);
    internal static Result Apply(IPreference preference, bool requested)
    {
        try { return ApplyCore(preference, requested); }
        catch (Exception ex) when (IsPreferenceError(ex)) { return Unavailable(ex); }
    }
    private static bool IsPreferenceError(Exception ex) => ex is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException;
    private static Result Unavailable(Exception ex) => new(false, "Unavailable", "Chromium WebRTC preference access failed (" + ex.GetType().Name + ").");
    private static Result ApplyCore(IPreference preference, bool requested)
    {
        if (!preference.Exists || !preference.Writable)
            return new(false, "Unavailable", "Chromium does not expose a writable WebRTC IP handling preference.");
        if (!preference.Set(requested ? RestrictNonProxiedUdp : null, out var error))
            return new(false, "Unavailable", "Chromium rejected the WebRTC IP handling preference: " + error);
        if (requested && preference.Value is not RestrictNonProxiedUdp)
            return new(false, "Unavailable", "Chromium did not retain the requested WebRTC IP handling preference.");
        return requested
            ? new(true, "Non-proxied WebRTC UDP restriction requested and read back", null)
            : new(true, "Aegis WebRTC preference override cleared at startup", null);
    }
}
