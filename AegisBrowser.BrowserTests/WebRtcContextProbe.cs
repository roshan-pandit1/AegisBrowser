using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AegisBrowser;
using CefSharp;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // These are independent, test-owned loopback listeners. They do not observe
    // unrelated traffic, record packet contents, or establish network containment.
    private sealed class RtcLoopbackObserver : IDisposable
    {
        private readonly UdpClient udp4 = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly TcpListener tcp4 = new(IPAddress.Loopback, 0);
        private readonly UdpClient? udp6;
        private readonly TcpListener? tcp6;
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
        private readonly Task<string> udp4Result, udp6Result, tcp4Result, tcp6Result;

        internal RtcLoopbackObserver()
        {
            tcp4.Start();
            try { udp6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0)); }
            catch (SocketException) { }
            try { tcp6 = new TcpListener(IPAddress.IPv6Loopback, 0); tcp6.Start(); }
            catch (SocketException) { tcp6 = null; }
            udp4Result = ObserveUdp(udp4, timeout.Token);
            udp6Result = udp6 is null ? Task.FromResult("UNSUPPORTED") : ObserveUdp(udp6, timeout.Token);
            tcp4Result = ObserveTcp(tcp4, timeout.Token);
            tcp6Result = tcp6 is null ? Task.FromResult("UNSUPPORTED") : ObserveTcp(tcp6, timeout.Token);
        }

        internal string IceServersJson()
        {
            var servers = new List<object>
            {
                new { urls = "stun:127.0.0.1:" + ((IPEndPoint)udp4.Client.LocalEndPoint!).Port },
                new { urls = "turn:127.0.0.1:" + ((IPEndPoint)tcp4.LocalEndpoint).Port + "?transport=tcp", username = "fixture", credential = "fixture" }
            };
            if (udp6 is not null) servers.Add(new { urls = "stun:[::1]:" + ((IPEndPoint)udp6.Client.LocalEndPoint!).Port });
            if (tcp6 is not null) servers.Add(new { urls = "turn:[::1]:" + ((IPEndPoint)tcp6.LocalEndpoint).Port + "?transport=tcp", username = "fixture", credential = "fixture" });
            return JsonSerializer.Serialize(servers);
        }

        private static async Task<string> ObserveUdp(UdpClient socket, CancellationToken token)
        {
            try
            {
                var packet = await socket.ReceiveAsync(token);
                var data = packet.Buffer;
                return data.Length >= 20 && (data[0] & 0xc0) == 0 && BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4)) == 0x2112a442
                    ? "STUN_OBSERVED" : "OTHER_PACKET";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return "NOT_OBSERVED"; }
        }

        private static async Task<string> ObserveTcp(TcpListener listener, CancellationToken token)
        {
            try { using var client = await listener.AcceptTcpClientAsync(token); return "CONNECTION_OBSERVED"; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return "NOT_OBSERVED"; }
        }

        internal async Task<(string Udp4, string Udp6, string Tcp4, string Tcp6)> Results()
        {
            await Task.WhenAll(udp4Result, udp6Result, tcp4Result, tcp6Result);
            return (await udp4Result, await udp6Result, await tcp4Result, await tcp6Result);
        }
        public void Dispose() { timeout.Cancel(); udp4.Dispose(); udp6?.Dispose(); tcp4.Stop(); tcp6?.Stop(); timeout.Dispose(); }
    }

    private static async Task<string> FrameScript(IFrame frame, string script)
    {
        if (!frame.IsValid) throw new InvalidOperationException("The controlled CEF frame disappeared.");
        var response = await frame.EvaluateScriptAsync(script, frame.Url, 1, TimeSpan.FromSeconds(10), false);
        if (!response.Success) throw new InvalidOperationException("Controlled frame script failed: " + response.Message);
        return response.Result?.ToString() ?? "";
    }

    private static async Task ObserveWebRtcContexts(BrowserView browser, bool restrict, int originPort)
    {
        var root = "http://127.0.0.1:" + originPort;
        var same = root + "/fp03-same";
        var cross = "http://localhost:" + originPort + "/fp03-cross";
        Check(new Uri(same).Host != new Uri(cross).Host, "WebRTC fixture uses distinct loopback origins");
        await Js(browser, "(()=>{for(const [name,url] of " + JsonSerializer.Serialize(new[] { new[] { "fp03-same", same }, new[] { "fp03-cross", cross } })
            + "){const f=document.createElement('iframe');f.name=name;f.src=url;document.body.appendChild(f);}return 'created';})()");
        foreach (var (name, expected) in new[] { ("fp03-same", same), ("fp03-cross", cross) })
            await Wait(() => { using var frame = browser.GetBrowser().GetFrameByName(name); return frame is { IsValid: true } && frame.Url == expected; }, "controlled WebRTC frame " + name);

        foreach (var (label, name, expected) in new[] { ("main", "", root + "/webrtc"), ("same", "fp03-same", same), ("cross", "fp03-cross", cross) })
        {
            using var frame = name.Length == 0 ? browser.GetBrowser().MainFrame : browser.GetBrowser().GetFrameByName(name);
            if (frame is null || !frame.IsValid || frame.Url != expected || frame.IsMain != (name.Length == 0))
                throw new InvalidOperationException("Controlled WebRTC frame identity does not match " + label);
            using var parent = frame.Parent;
            if (name.Length != 0 && (parent is null || !parent.IsValid || !parent.IsMain))
                throw new InvalidOperationException("Controlled WebRTC iframe parent is not the main frame.");
            var run = Guid.NewGuid().ToString("N");
            using var observer = new RtcLoopbackObserver();
            var script = "(()=>{if(typeof RTCPeerConnection!=='function')return 'UNSUPPORTED';"
                + "window.__fp03={state:'new',offer:'pending',candidates:0,host:0,srflx:0,relay:0};"
                + "const p=window.__fp03peer=new RTCPeerConnection({iceServers:" + observer.IceServersJson() + "});"
                + "p.onicegatheringstatechange=()=>window.__fp03.state=p.iceGatheringState;"
                + "p.onicecandidate=e=>{if(!e.candidate)return;window.__fp03.candidates++;"
                + "const m=e.candidate.candidate.match(/ typ (host|srflx|relay)\\b/);if(m)window.__fp03[m[1]]++;};"
                + "p.createDataChannel('fixture');p.createOffer().then(o=>p.setLocalDescription(o))"
                + ".then(()=>window.__fp03.offer='applied').catch(()=>window.__fp03.offer='failed');return 'STARTED';})()";
            var started = await FrameScript(frame, script);
            if (started != "STARTED") throw new InvalidOperationException("Controlled WebRTC operation unavailable in " + label + ": " + started);
            var observed = await observer.Results();
            var state = await FrameScript(frame, "JSON.stringify(window.__fp03)");
            using var parsed = JsonDocument.Parse(state);
            var ice = parsed.RootElement.GetProperty("state").GetString();
            var offer = parsed.RootElement.GetProperty("offer").GetString();
            var count = parsed.RootElement.GetProperty("candidates").GetInt32();
            Console.WriteLine($"P4FP03_RTC: run={run} mode={(restrict ? "restricted" : "baseline")} context={label} origin=loopback request_context=global udp4={observed.Udp4} udp6={observed.Udp6} tcp4={observed.Tcp4} tcp6={observed.Tcp6} ice={ice} offer={offer} candidates={count} timeout_seconds=8 evidence=os_listener_and_browser");
            if (restrict && (observed.Udp4 == "STUN_OBSERVED" || observed.Udp6 == "STUN_OBSERVED"))
                throw new InvalidOperationException("Restricted WebRTC sent STUN to a controlled loopback listener in " + label);
            await FrameScript(frame, "window.__fp03peer.close();'closed'");
        }
    }
}
