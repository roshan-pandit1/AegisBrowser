using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AegisBrowser;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // An observation only: absence of a packet cannot prove that WebRTC or DNS is isolated.
    private static async Task ObserveWebRtcUdp(BrowserView browser)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var receive = udp.ReceiveAsync(timeout.Token).AsTask();
        var script = "(()=>{if(typeof RTCPeerConnection!=='function')return 'unsupported';" +
            "window.__aegisProbePeer=new RTCPeerConnection({iceServers:[{urls:'stun:127.0.0.1:" + port + "'}]});" +
            "window.__aegisProbePeer.createDataChannel('probe');" +
            "window.__aegisProbePeer.createOffer().then(o=>window.__aegisProbePeer.setLocalDescription(o)).catch(()=>{});return 'started';})()";
        try
        {
            if (await Js(browser, script) != "started")
            {
                timeout.Cancel();
                try { _ = await receive; } catch (OperationCanceledException) { }
                Console.WriteLine("INCONCLUSIVE: WebRTC is unavailable in the controlled probe.");
                return;
            }
            try
            {
                var packet = await receive;
                var data = packet.Buffer;
                if (data.Length >= 20 && (data[0] & 0xc0) == 0 && BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4)) == 0x2112a442)
                    Console.WriteLine("OBSERVED: WebRTC sent a STUN UDP packet directly to the controlled local listener while HTTP used the proxy fixture.");
                else Console.WriteLine("INCONCLUSIVE: the controlled UDP listener received a non-STUN packet.");
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            { Console.WriteLine("INCONCLUSIVE: no STUN UDP packet was observed; this does not establish network isolation."); }
        }
        finally
        {
            timeout.Cancel();
            try { await Js(browser, "window.__aegisProbePeer?.close(); 'closed'"); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { }
        }
    }
}
