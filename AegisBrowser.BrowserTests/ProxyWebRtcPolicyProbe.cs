using AegisBrowser;
using CefSharp;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    private static async Task ObserveWebRtcPolicy(BrowserView browser, IRequestContext context)
    {
        var fake = new FakeWebRtcPreference { Exists = false };
        var absent = WebRtcPrivacyPolicy.Apply(fake, true);
        Check(!absent.Succeeded && WebRtcPrivacyPolicy.PreventBrowsing(true, absent), "synthetic missing WebRTC preference blocks tabs");
        fake.Exists = true; fake.Writable = false;
        Check(!WebRtcPrivacyPolicy.Apply(fake, true).Succeeded && fake.SetCalls == 0, "synthetic read-only WebRTC preference fails closed");
        fake.Writable = true; fake.Accepted = false;
        Check(!WebRtcPrivacyPolicy.Apply(fake, true).Succeeded, "synthetic rejected WebRTC preference fails closed");
        fake.Accepted = true; fake.Readback = "default";
        Check(!WebRtcPrivacyPolicy.Apply(fake, true).Succeeded, "synthetic incorrect WebRTC readback fails closed");
        fake.Readback = null;
        Check(!WebRtcPrivacyPolicy.Apply(fake, true).Succeeded, "synthetic missing WebRTC readback fails closed");
        fake.Readback = WebRtcPrivacyPolicy.RestrictNonProxiedUdp;
        Check(WebRtcPrivacyPolicy.Apply(fake, true).Succeeded && Equals(fake.LastValue, fake.Readback), "synthetic WebRTC policy reads back exact value");
        Check(WebRtcPrivacyPolicy.Apply(fake, false).Succeeded && fake.LastValue is null, "synthetic WebRTC opt-out resets browser default");
        Check(WebRtcPrivacyPolicy.ApplyAtStartup(null).Succeeded, "legacy WebRTC setting does not override existing preference");

        var result = await Cef.UIThreadTaskFactory.StartNew(() => WebRtcPrivacyPolicy.ApplyOnUiThread(context, true));
        if (!result.Succeeded)
        {
            Console.WriteLine("INCONCLUSIVE: controlled WebRTC preference was not applied and read back: " + result.Error);
            return;
        }
        Console.WriteLine("OBSERVATION: controlled CEF context accepted and returned the requested WebRTC preference; testing one local STUN attempt after browser creation.");
        await ObserveWebRtcUdp(browser);
        Console.WriteLine("INCONCLUSIVE: a single UDP observation after changing a preference cannot establish full network isolation.");
    }

    private sealed class FakeWebRtcPreference : WebRtcPrivacyPolicy.IPreference
    {
        public bool Exists { get; set; } = true;
        public bool Writable { get; set; } = true;
        public bool Accepted { get; set; } = true;
        public object? Readback { get; set; }
        public object? LastValue { get; private set; }
        public int SetCalls { get; private set; }
        public bool Set(object? value, out string error)
        { SetCalls++; LastValue = value; error = Accepted ? "" : "synthetic rejection"; return Accepted; }
        public object? Value => Readback;
    }
}
