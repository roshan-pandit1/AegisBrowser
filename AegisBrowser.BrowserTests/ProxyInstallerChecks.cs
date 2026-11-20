using AegisBrowser;
using AegisBrowser.Core;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Synthetic failure branches; native routing remains tested separately by
    // ObserveSocksThroughProxy using a real CEF request context.
    private static void CheckProxyInstallerFailures()
    {
        var draft = new ProxyDraft(ProxyProtocol.Socks5, "127.0.0.1", 1080, false, null, "", true);
        var readOnly = new FakeProxyPreference { WritableValue = false };
        Check(ChromiumProxyInstaller.Apply(readOnly, draft).Status == ChromiumProxyInstaller.Outcome.Unavailable
            && readOnly.SetCalls == 0, "read-only Chromium proxy preference is rejected");

        var rejected = new FakeProxyPreference { WritableValue = true, SetResult = false };
        Check(ChromiumProxyInstaller.Apply(rejected, draft).Status == ChromiumProxyInstaller.Outcome.Rejected
            && rejected.SetCalls == 1, "Chromium proxy preference failure is propagated");

        var accepted = new FakeProxyPreference { WritableValue = true, SetResult = true };
        Check(ChromiumProxyInstaller.Apply(accepted, draft).Status == ChromiumProxyInstaller.Outcome.Accepted
            && accepted.SetCalls == 1 && accepted.Mode == "fixed_servers" && accepted.Server == "socks5://127.0.0.1:1080",
            "accepted proxy installer call uses the validated fixed server");

        var invalid = new FakeProxyPreference { WritableValue = true, SetResult = true };
        Check(ChromiumProxyInstaller.Apply(invalid, draft with { RequireProxy = false }).Status == ChromiumProxyInstaller.Outcome.InvalidConfiguration
            && invalid.SetCalls == 0, "invalid required-proxy draft cannot change preferences");

        var unavailable = new FakeProxyPreference { ThrowOnWritable = true };
        Check(ChromiumProxyInstaller.Apply(unavailable, draft).Status == ChromiumProxyInstaller.Outcome.Unavailable
            && unavailable.SetCalls == 0, "proxy preference access error fails closed");

        var setError = new FakeProxyPreference { WritableValue = true, ThrowOnSet = true };
        Check(ChromiumProxyInstaller.Apply(setError, draft).Status == ChromiumProxyInstaller.Outcome.Unavailable
            && setError.SetCalls == 1, "proxy preference setter exception fails closed without details");
    }

    private sealed class FakeProxyPreference : ChromiumProxyInstaller.IPreference
    {
        internal bool WritableValue { get; init; }
        internal bool SetResult { get; init; }
        internal bool ThrowOnWritable { get; init; }
        internal bool ThrowOnSet { get; init; }
        internal int SetCalls { get; private set; }
        internal string? Mode { get; private set; }
        internal string? Server { get; private set; }
        public bool Writable => ThrowOnWritable ? throw new InvalidOperationException("synthetic") : WritableValue;
        public bool Set(Dictionary<string, object> value)
        {
            SetCalls++;
            if (ThrowOnSet) throw new ArgumentException("synthetic sensitive detail");
            Mode = value["mode"] as string;
            Server = value["server"] as string;
            return SetResult;
        }
    }
}
