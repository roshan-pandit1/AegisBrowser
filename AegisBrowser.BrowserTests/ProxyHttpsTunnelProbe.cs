using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AegisBrowser;
using AegisBrowser.Core;
using CefSharp;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Research only: this fixture handles CONNECT for exactly one synthetic host.
    private static async Task ObserveHttpsThroughProxy(BrowserView browser, IRequestContext context)
    {
        using var proxy = new HttpsTunnelFixture();
        // Prove that the fixture can negotiate TLS when a test client explicitly
        // trusts its certificate. This never changes the machine's trust store.
        using (var client = new TcpClient())
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            await client.ConnectAsync(IPAddress.Loopback, proxy.Port, timeout.Token);
            var network = client.GetStream();
            await network.WriteAsync("CONNECT aegis-tls.invalid:443 HTTP/1.1\r\nHost: aegis-tls.invalid:443\r\n\r\n"u8.ToArray(), timeout.Token);
            using (var reader = new StreamReader(network, Encoding.ASCII, false, 4096, leaveOpen: true))
            {
                Check(await reader.ReadLineAsync(timeout.Token) == "HTTP/1.1 200 Connection Established", "HTTPS test fixture accepts its controlled tunnel");
                for (var i = 0; i < 100; i++) { var line = await reader.ReadLineAsync(timeout.Token); if (string.IsNullOrEmpty(line)) break; }
            }
            using var tls = new SslStream(network);
            var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
            policy.CustomTrustStore.Add(proxy.Certificate);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            { TargetHost = "aegis-tls.invalid", CertificateChainPolicy = policy }, timeout.Token);
            Check(tls.IsAuthenticated && tls.IsEncrypted, "HTTPS test fixture completes TLS with its explicitly trusted certificate");
        }

        var priorTunnels = proxy.Tunnels;
        var configured = await Cef.UIThreadTaskFactory.StartNew(() =>
        {
            if (!context.CanSetPreference("proxy")) return (false, "Proxy preference is read-only.");
            var draft = new ProxyDraft(ProxyProtocol.Http, "127.0.0.1", proxy.Port, false, null, "", true);
            var settings = ChromiumProxyPreference.Create(draft);
            var ok = context.SetPreference("proxy", settings, out var error); return (ok, error);
        });
        if (!configured.Item1) throw new IOException("CEF rejected the test proxy preference: " + configured.Item2);

        const string url = "https://aegis-tls.invalid/untrusted";
        var failed = new TaskCompletionSource<CefErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LoadErrorEventArgs> onError = (_, e) =>
        { if (e.Frame.IsMain && e.FailedUrl == url) failed.TrySetResult(e.ErrorCode); };
        browser.LoadError += onError;
        try
        {
            browser.Load(url);
            var error = await failed.Task.WaitAsync(TimeSpan.FromSeconds(25));
            await Wait(() => proxy.Tunnels > priorTunnels, "CEF HTTPS CONNECT through the controlled proxy");
            Check(error.ToString().StartsWith("Cert", StringComparison.Ordinal),
                "CEF rejects the untrusted HTTPS endpoint through the proxy: " + error);
            await Task.Delay(500);
            Check(proxy.ApplicationRequests == 0, "CEF sends no HTTPS application request to the untrusted endpoint");
        }
        finally { browser.LoadError -= onError; }
    }

    private static async Task ObserveTrustedProxyRoutes(BrowserView browser, IRequestContext context, PrivacyCertificates certificates)
    {
        var tlsOriginPorts = new ConcurrentQueue<int>();
        using var tlsOrigin = new PrivacyHttpsServer(certificates.Server, onRequest: (path, port) =>
        {
            if (path != "/trusted-connect") return;
            tlsOriginPorts.Enqueue(port);
            Console.WriteLine($"PROXY_TLS_DESTINATION: mode=connect tls=completed request=received source_port={port} utc={DateTimeOffset.UtcNow:O}");
        });
        using var tunnel = new HttpsTunnelFixture(tlsOrigin.Port);
        using var httpOrigin = new LocalHttpFixture("secure proxy origin", onRequest: () =>
            Console.WriteLine($"PROXY_TLS_DESTINATION: mode=https_proxy request=received utc={DateTimeOffset.UtcNow:O}"));
        using var secureProxy = new SecureProxyFixture(certificates.Server, httpOrigin.Port);

        async Task Configure(string server)
        {
            var accepted = await Cef.UIThreadTaskFactory.StartNew(() => context.CanSetPreference("proxy") &&
                context.SetPreference("proxy", new Dictionary<string, object>
                {
                    ["mode"] = "fixed_servers", ["server"] = server, ["bypass_list"] = "<-loopback>"
                }, out _));
            Check(accepted, "CEF accepted the trusted fixture proxy preference");
        }

        await Configure($"http://127.0.0.1:{tunnel.Port}");
        await LoadReady(browser, tlsOrigin.TopA + "trusted-connect");
        Check(tunnel.Tunnels > 0 && tlsOrigin.Take("/trusted-connect").Length > 0 &&
            !tlsOriginPorts.IsEmpty && tlsOriginPorts.All(tunnel.UpstreamPorts.Contains),
            "trusted HTTP CONNECT target reached independent TLS origin");

        await Configure($"https://127.0.0.1:{secureProxy.Port}");
        await LoadReady(browser, $"http://aegis-secure-proxy.invalid:{httpOrigin.Port}/trusted-proxy");
        Check(secureProxy.ApplicationRequests > 0 && httpOrigin.SawViaProxy("/trusted-proxy") &&
            httpOrigin.OnlyFrom(secureProxy.UpstreamPorts),
            "trusted HTTPS proxy request reached independent HTTP origin");
    }

    private sealed class HttpsTunnelFixture : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentBag<Task> clients = new();
        private readonly Task loop;
        private int tunnels, applicationRequests;
        public int Port { get; }
        public int Tunnels => Volatile.Read(ref tunnels);
        public int ApplicationRequests => Volatile.Read(ref applicationRequests);
        public X509Certificate2 Certificate { get; }
        private readonly int? forwardPort;
        private readonly string expectedConnect;
        private readonly ConcurrentQueue<int> upstreamPorts = new();
        public int[] UpstreamPorts => upstreamPorts.ToArray();

        public HttpsTunnelFixture(int? forwardPort = null)
        {
            this.forwardPort = forwardPort;
            expectedConnect = forwardPort is int port ? $"CONNECT 127.0.0.1:{port} HTTP/1.1" : "CONNECT aegis-tls.invalid:443 HTTP/1.1";
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=aegis-tls.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("aegis-tls.invalid");
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(10));
            Certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.UserKeySet);
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
                    var network = client.GetStream();
                    using (var reader = new StreamReader(network, Encoding.ASCII, false, 4096, leaveOpen: true))
                    {
                        var first = await reader.ReadLineAsync(token);
                        if (first != expectedConnect) return;
                        for (var i = 0; i < 100; i++)
                        {
                            var line = await reader.ReadLineAsync(token); if (string.IsNullOrEmpty(line)) break;
                            if (line.Length > 8192) return;
                        }
                    }
                    Interlocked.Increment(ref tunnels);
                    if (forwardPort is int port)
                    {
                        using var upstream = new TcpClient(AddressFamily.InterNetwork);
                        await upstream.ConnectAsync(IPAddress.Loopback, port, token);
                        upstreamPorts.Enqueue(((IPEndPoint)upstream.Client.LocalEndPoint!).Port);
                        using var upstreamStream = upstream.GetStream();
                        Console.WriteLine($"PROXY_TLS_RECEIPT: {expectedConnect} utc={DateTimeOffset.UtcNow:O}");
                        await network.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), token);
                        using var relay = CancellationTokenSource.CreateLinkedTokenSource(token);
                        var requestCopy = network.CopyToAsync(upstreamStream, relay.Token);
                        var responseCopy = upstreamStream.CopyToAsync(network, relay.Token);
                        try { await await Task.WhenAny(requestCopy, responseCopy); }
                        finally
                        {
                            relay.Cancel();
                            try { await Task.WhenAll(requestCopy, responseCopy); }
                            catch (OperationCanceledException) when (relay.IsCancellationRequested) { }
                        }
                        return;
                    }
                    await network.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), token);
                    using var tls = new SslStream(network);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, token);
                    using var request = new StreamReader(tls, Encoding.ASCII, false, 4096, leaveOpen: true);
                    var application = await request.ReadLineAsync(token);
                    if (application is not null) Interlocked.Increment(ref applicationRequests);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (AuthenticationException) { } // The untrusted test certificate should be rejected.
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        }

        public void Dispose()
        {
            stop.Cancel(); listener.Stop(); loop.GetAwaiter().GetResult();
            Task.WhenAll(clients.ToArray()).GetAwaiter().GetResult();
            Certificate.Dispose(); stop.Dispose();
        }
    }
}
