using System.Runtime.InteropServices;
using AegisBrowser.Core;
using CefSharp;
using CefSharp.Internals;

namespace AegisBrowser;

// Accepting a Chromium preference is not OS containment. This helper is not
// connected to profile startup; EnsureRuntimeReady still blocks every proxy.
internal static class ChromiumProxyInstaller
{
    internal enum Outcome { Accepted, WrongThread, InvalidConfiguration, Unavailable, Rejected }
    internal readonly record struct Result(Outcome Status)
    {
        internal bool Succeeded => Status == Outcome.Accepted;
    }

    internal interface IPreference
    {
        bool Writable { get; }
        bool Set(Dictionary<string, object> value);
    }

    private sealed class CefPreference(IRequestContext context) : IPreference
    {
        public bool Writable => context.CanSetPreference("proxy");
        public bool Set(Dictionary<string, object> value) => context.SetPreference("proxy", value, out _);
    }

    // The caller must dispatch to the CEF UI thread and retain the request
    // context until the call completes. No browser or user profile is created.
    internal static Result ApplyOnUiThread(IRequestContext context, ProxyDraft draft)
    {
        try
        {
            if (!CefThread.CurrentlyOnUiThread) return new(Outcome.WrongThread);
            if (context is null) return new(Outcome.Unavailable);
            return Apply(new CefPreference(context), draft);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            return new(Outcome.Unavailable);
        }
    }

    // Separated to test every failure branch without a native CEF instance.
    internal static Result Apply(IPreference preference, ProxyDraft draft)
    {
        Dictionary<string, object> value;
        try
        {
            value = ChromiumProxyPreference.Create(draft);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return new(Outcome.InvalidConfiguration);
        }
        try
        {
            if (preference is null || !preference.Writable) return new(Outcome.Unavailable);
            return new(preference.Set(value) ? Outcome.Accepted : Outcome.Rejected);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or COMException)
        {
            return new(Outcome.Unavailable);
        }
    }
}
