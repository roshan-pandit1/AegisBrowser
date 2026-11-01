// A loopback-only observer in a process outside CEF. Not a forwarder or a security boundary.
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AegisContainmentResearch
{
    public sealed class ExternalEgressFixture : IDisposable
    {
        private readonly TcpListener proxy = new TcpListener(IPAddress.Loopback, 0);
        private readonly TcpListener origin = new TcpListener(IPAddress.Loopback, 0);
        private readonly UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly Task proxyLoop, originLoop, udpLoop;
        private volatile bool proxyStopped, stopped;
        private int proxyTarget, originControl, originTarget, originOffline, stunPackets, otherPackets;
        private volatile string error = "";

        public int ProxyPort { get; private set; }
        public int OriginPort { get; private set; }
        public int StunPort { get; private set; }
        public int ProxyTarget { get { return Volatile.Read(ref proxyTarget); } }
        public int OriginControl { get { return Volatile.Read(ref originControl); } }
        public int OriginTarget { get { return Volatile.Read(ref originTarget); } }
        public int OriginOffline { get { return Volatile.Read(ref originOffline); } }
        public int StunPackets { get { return Volatile.Read(ref stunPackets); } }
        public int OtherPackets { get { return Volatile.Read(ref otherPackets); } }
        public string Error { get { return error; } }

        public ExternalEgressFixture()
        {
            proxy.Start(); origin.Start();
            ProxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
            OriginPort = ((IPEndPoint)origin.LocalEndpoint).Port;
            StunPort = ((IPEndPoint)udp.Client.LocalEndPoint).Port;
            proxyLoop = Task.Run(() => AcceptLoop(proxy, true));
            originLoop = Task.Run(() => AcceptLoop(origin, false));
            udpLoop = Task.Run((Action)ReceiveUdp);
        }

        private void AcceptLoop(TcpListener listener, bool isProxy)
        {
            try
            {
                while (!stopped && !(isProxy && proxyStopped))
                {
                    TcpClient client = listener.AcceptTcpClient();
                    Task.Run(() => Serve(client, isProxy));
                }
            }
            catch (SocketException) { if (!stopped && !(isProxy && proxyStopped)) error = "TCP listener failed"; }
            catch (ObjectDisposedException) { if (!stopped && !(isProxy && proxyStopped)) error = "TCP listener disposed unexpectedly"; }
        }

        private void Serve(TcpClient client, bool isProxy)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 3000; client.SendTimeout = 3000;
                    using (var stream = client.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                    {
                        string first = ReadBoundedLine(reader);
                        if (first == null) return;
                        bool headersComplete = false;
                        for (int i = 0; i < 64; i++)
                        {
                            string line = ReadBoundedLine(reader);
                            if (line == null) return;
                            if (line.Length == 0) { headersComplete = true; break; }
                        }
                        if (!headersComplete) return;
                        bool permitted = false;
                        if (isProxy && first.StartsWith("GET http://127.0.0.1:" + OriginPort + "/external-target ", StringComparison.Ordinal))
                        { Interlocked.Increment(ref proxyTarget); permitted = true; }
                        if (!isProxy && first.StartsWith("GET /control ", StringComparison.Ordinal))
                        { Interlocked.Increment(ref originControl); permitted = true; }
                        if (!isProxy && first.StartsWith("GET /external-target ", StringComparison.Ordinal))
                        { Interlocked.Increment(ref originTarget); permitted = true; }
                        if (!isProxy && first.StartsWith("GET /external-offline ", StringComparison.Ordinal))
                        { Interlocked.Increment(ref originOffline); permitted = true; }
                        string body = isProxy ? "<title>external-proxy</title>" : "<title>external-origin</title>";
                        byte[] data = Encoding.ASCII.GetBytes((permitted ? "HTTP/1.1 200 OK\r\n" : "HTTP/1.1 403 Forbidden\r\n") +
                            "Content-Type: text/html\r\nConnection: close\r\nContent-Length: " + body.Length + "\r\n\r\n" + body);
                        stream.Write(data, 0, data.Length);
                    }
                }
                catch (IOException) { /* An aborted local test client is not an egress decision. */ }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
        }

        private static string ReadBoundedLine(StreamReader reader)
        {
            var line = new StringBuilder();
            while (line.Length <= 2048)
            {
                int next = reader.Read();
                if (next < 0) return null;
                if (next == '\n') return line.ToString().TrimEnd('\r');
                line.Append((char)next);
            }
            return null;
        }

        private void ReceiveUdp()
        {
            try
            {
                while (!stopped)
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] packet = udp.Receive(ref remote);
                    if (packet.Length >= 20 && (packet[0] & 0xc0) == 0 && packet[4] == 0x21 &&
                        packet[5] == 0x12 && packet[6] == 0xa4 && packet[7] == 0x42)
                        Interlocked.Increment(ref stunPackets);
                    else Interlocked.Increment(ref otherPackets);
                }
            }
            catch (SocketException) { if (!stopped) error = "UDP listener failed"; }
            catch (ObjectDisposedException) { if (!stopped) error = "UDP listener disposed unexpectedly"; }
        }

        public void StopProxy() { proxyStopped = true; proxy.Stop(); }
        public void Dispose()
        {
            stopped = true; proxyStopped = true;
            proxy.Stop(); origin.Stop(); udp.Close();
            Task.WaitAll(new[] { proxyLoop, originLoop, udpLoop }, 5000);
        }
    }
}
