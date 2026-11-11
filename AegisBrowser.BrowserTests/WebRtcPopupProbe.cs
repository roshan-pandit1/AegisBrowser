using System.Text.Json;
using AegisBrowser;
using CefSharp;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // A native CEF popup is created through the real application handler. The
    // same-origin, test-owned opener runs the probe in the popup's own realm.
    private static async Task ObserveWebRtcPopup(BrowserView browser, bool restrict, int port)
    {
        var life = browser.LifeSpanHandler as SafePopups ?? throw new InvalidOperationException("Popup handler unavailable.");
        var url = "http://127.0.0.1:" + port + "/fp03-rtc-popup";
        await Cef.UIThreadTaskFactory.StartNew(() =>
        {
            var host = browser.GetBrowserHost();
            var id = host.GetNextDevToolsMessageId();
            var expression = "window.__fp03popup=window.open(" + JsonSerializer.Serialize(url) + ",'fp03-rtc-popup')";
            if (host.ExecuteDevToolsMethod(id, "Runtime.evaluate", new Dictionary<string, object>
                { ["expression"] = expression, ["userGesture"] = true }) != id)
                throw new InvalidOperationException("Popup input was not submitted.");
        });
        await Wait(() => life.PopupCount == 1, "native WebRTC popup created");
        await WaitForAsync(async () => await Js(browser,
            "!!window.__fp03popup && !window.__fp03popup.closed && window.__fp03popup.location.href === " + JsonSerializer.Serialize(url)
            + " && window.__fp03popup.document.readyState === 'complete'") == "True", "native WebRTC popup ready");
        using var observer = new RtcLoopbackObserver();
        var code = "(()=>{if(typeof RTCPeerConnection!=='function')return 'UNSUPPORTED';"
            + "window.__fp03={state:'new',offer:'pending',candidates:0};"
            + "const p=window.__fp03peer=new RTCPeerConnection({iceServers:" + observer.IceServersJson() + "});"
            + "p.onicegatheringstatechange=()=>window.__fp03.state=p.iceGatheringState;"
            + "p.onicecandidate=e=>{if(e.candidate)window.__fp03.candidates++;};"
            + "p.createDataChannel('fixture');p.createOffer().then(o=>p.setLocalDescription(o))"
            + ".then(()=>window.__fp03.offer='applied').catch(()=>window.__fp03.offer='failed');return 'STARTED';})()";
        var started = await Js(browser, "window.__fp03popup.eval(" + JsonSerializer.Serialize(code) + ")");
        Check(started == "STARTED", "native popup started WebRTC in its own document realm");
        var observed = await observer.Results();
        using var state = JsonDocument.Parse(await Js(browser, "JSON.stringify(window.__fp03popup.__fp03)"));
        var ice = state.RootElement.GetProperty("state").GetString();
        var offer = state.RootElement.GetProperty("offer").GetString();
        var count = state.RootElement.GetProperty("candidates").GetInt32();
        Console.WriteLine($"P4FP03_RTC: run={Guid.NewGuid():N} mode={(restrict ? "restricted" : "baseline")} context=popup origin=loopback request_context=opener udp4={observed.Udp4} udp6={observed.Udp6} tcp4={observed.Tcp4} tcp6={observed.Tcp6} ice={ice} offer={offer} candidates={count} timeout_seconds=8 evidence=os_listener_and_browser");
        if (restrict && (observed.Udp4 == "STUN_OBSERVED" || observed.Udp6 == "STUN_OBSERVED"))
            throw new InvalidOperationException("Restricted native popup sent STUN to a controlled listener.");
        await Js(browser, "window.__fp03popup.__fp03peer.close(); window.__fp03popup.close(); 'closed'");
        await Wait(() => life.PopupCount == 0, "native WebRTC popup closed");
        Check(await Js(browser, "document.title") == "webrtc", "opener remains usable after WebRTC popup closure");
    }
}
