using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AegisBrowser;
using CefSharp;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Research only: a SOCKS5 fixture that never connects to external destinations.
    private static async Task ObserveSocksThroughProxy(BrowserView browser, IRequestContext context)
    {
        using var proxy = new SocksFixture();
        var configured = await Cef.UIThreadTaskFactory.StartNew(() =>
        {
            if (!context.CanSetPreference("proxy")) return (false, "Proxy preference is read-only.");
            var settings = new Dictionary<string, object> { ["mode"] = "fixed_servers", ["server"] = "socks5://127.0.0.1:" + proxy.Port };
            var ok = context.SetPreference("proxy", settings, out var error); return (ok, error);
        });
        if (!configured.Item1) throw new IOException("CEF rejected the test SOCKS5 preference: " + configured.Item2);

        const string url = "http://aegis-socks.invalid/fixture";
        await LoadReady(browser, url);
        await Wait(() => proxy.FixtureRequests > 0, "CEF request through controlled SOCKS5 fixture");
        Check(proxy.DomainConnects > 0 && await Js(browser, "document.title") == "socks fixture",
            "CEF uses an unauthenticated SOCKS5 tunnel and supplies the controlled destination hostname");

        proxy.Stop();
        const string offlineUrl = "http://aegis-socks.invalid/offline";
        var failed = new TaskCompletionSource<CefErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LoadErrorEventArgs> onError = (_, e) =>
        { if (e.Frame.IsMain && e.FailedUrl == offlineUrl) failed.TrySetResult(e.ErrorCode); };
        browser.LoadError += onError;
        try
        {
            browser.Load(offlineUrl);
            var error = await failed.Task.WaitAsync(TimeSpan.FromSeconds(25));
            Check(error != CefErrorCode.None, "CEF reports a load error after the controlled SOCKS5 listener stops");
        }
        finally { browser.LoadError -= onError; }
    }

    private sealed class SocksFixture : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentBag<Task> clients = new();
        private readonly Task loop;
        private int domainConnects, fixtureRequests;
        private bool stopped;
        public int Port { get; }
        public int DomainConnects => Volatile.Read(ref domainConnects);
        public int FixtureRequests => Volatile.Read(ref fixtureRequests);

        public SocksFixture()
        {
            listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    clients.Add(Serve(client, stop.Token));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        }

        private async Task Serve(TcpClient client, CancellationToken token)
        {
            try
            {
                using (client)
                {
                    using var stream = client.GetStream();
                    var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, token);
                    if (greeting[0] != 5 || greeting[1] is 0 or > 16) return;
                    var methods = new byte[greeting[1]]; await stream.ReadExactlyAsync(methods, token);
                    if (!methods.Contains((byte)0)) return;
                    await stream.WriteAsync(new byte[] { 5, 0 }, token);
                    var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
                    if (header[0] != 5 || header[1] != 1 || header[2] != 0 || header[3] != 3) return;
                    var length = new byte[1]; await stream.ReadExactlyAsync(length, token);
                    if (length[0] == 0) return;
                    var name = new byte[length[0]]; await stream.ReadExactlyAsync(name, token);
                    var port = new byte[2]; await stream.ReadExactlyAsync(port, token);
                    if (Encoding.ASCII.GetString(name) != "aegis-socks.invalid" || port[0] != 0 || port[1] != 80) return;
                    Interlocked.Increment(ref domainConnects);
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, token);
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                    var first = await reader.ReadLineAsync(token);
                    if (first is null || !first.StartsWith("GET /fixture ", StringComparison.Ordinal)) return;
                    for (var i = 0; i < 100; i++)
                    {
                        var line = await reader.ReadLineAsync(token); if (string.IsNullOrEmpty(line)) break;
                        if (line.Length > 8192) return;
                    }
                    Interlocked.Increment(ref fixtureRequests);
                    const string body = "<!doctype html><title>socks fixture</title>";
                    var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n" + body);
                    await stream.WriteAsync(response, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        }

        public void Stop()
        {
            if (stopped) return;
            stopped = true; stop.Cancel(); listener.Stop(); loop.GetAwaiter().GetResult();
            Task.WhenAll(clients.ToArray()).GetAwaiter().GetResult();
        }

        public void Dispose() { Stop(); stop.Dispose(); }
    }
}
