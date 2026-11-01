// Synthetic loopback receiver for research only. It is not WFP telemetry and does not attribute a packet to a PID.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AegisContainmentResearch
{
    public sealed class IndependentTcpObserver : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly ConcurrentDictionary<string, byte> registered = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> received = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        private readonly Task loop;
        private volatile bool stopped;
        private volatile string error = "";
        private int unregistered;
        public int Port { get; private set; }
        public string Error { get { return error; } }
        public int Unregistered { get { return Volatile.Read(ref unregistered); } }

        public IndependentTcpObserver()
        {
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = Task.Run((Action)AcceptLoop);
        }
        public void Register(string id)
        {
            Guid parsed;
            if (id == null || id.Length != 32 || !Guid.TryParseExact(id, "N", out parsed) || !registered.TryAdd(id, 0))
                throw new ArgumentException("A unique synthetic transaction identifier is required.");
        }
        public int Received(string id) { int count; return received.TryGetValue(id, out count) ? count : 0; }
        private void AcceptLoop()
        {
            try
            {
                while (!stopped)
                {
                    TcpClient client = listener.AcceptTcpClient();
                    Task.Run(() => ReadClient(client));
                }
            }
            catch (SocketException) { if (!stopped) error = "TCP accept failed"; }
            catch (ObjectDisposedException) { if (!stopped) error = "TCP observer disposed unexpectedly"; }
        }
        private void ReadClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 3000;
                    using (var stream = client.GetStream())
                    {
                        byte[] data = new byte[64];
                        int count = 0, value;
                        while (count < data.Length && (value = stream.ReadByte()) >= 0)
                        {
                            if (value == '\n') break;
                            data[count++] = (byte)value;
                        }
                        string id = Encoding.ASCII.GetString(data, 0, count);
                        if (registered.ContainsKey(id)) received.AddOrUpdate(id, 1, (_, previous) => previous + 1);
                        else Interlocked.Increment(ref unregistered);
                    }
                }
                catch (IOException) { Interlocked.Increment(ref unregistered); }
                catch (SocketException) { Interlocked.Increment(ref unregistered); }
                catch (ObjectDisposedException) { }
            }
        }
        public void Dispose()
        {
            stopped = true;
            listener.Stop();
            if (!loop.Wait(5000)) throw new InvalidOperationException("Independent TCP observer did not stop.");
        }
    }
}
