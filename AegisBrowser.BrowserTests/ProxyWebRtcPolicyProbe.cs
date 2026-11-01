using AegisBrowser;
using CefSharp;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    private static async Task ObserveWebRtcPolicy(BrowserView browser, IRequestContext context)
    {
        const string preference = "webrtc.ip_handling_policy";
        var result = await Cef.UIThreadTaskFactory.StartNew(() =>
        {
            if (!context.CanSetPreference(preference)) return false;
            return context.SetPreference(preference, "disable_non_proxied_udp", out _);
        });
        if (!result)
        {
            Console.WriteLine("UNSUPPORTED: the controlled CEF context could not set the WebRTC non-proxied UDP preference.");
            return;
        }
        Console.WriteLine("OBSERVATION: the controlled CEF context accepted the WebRTC non-proxied UDP preference; testing one local STUN attempt.");
        await ObserveWebRtcUdp(browser);
        Console.WriteLine("INCONCLUSIVE: a single UDP observation after changing a preference cannot establish full network isolation.");
    }
}
