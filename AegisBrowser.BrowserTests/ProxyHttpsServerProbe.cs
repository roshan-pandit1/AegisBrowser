using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AegisBrowser;
using CefSharp;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Research only: this HTTPS proxy fixture never forwards any request.
    private static async Task ObserveHttpsProxyCertificate(BrowserView browser, IRequestContext context)
    {
        using var proxy = new SecureProxyFixture();
        // Verify the fixture first with a test-client-only custom trust root.
        using (var client = new TcpClient())
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            await client.ConnectAsync(IPAddress.Loopback, proxy.Port, timeout.Token);
            using var tls = new SslStream(client.GetStream());
            var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
            policy.CustomTrustStore.Add(proxy.Certificate);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            { TargetHost = "127.0.0.1", CertificateChainPolicy = policy }, timeout.Token);
            Check(tls.IsAuthenticated && tls.IsEncrypted, "HTTPS proxy fixture completes TLS with its explicitly trusted certificate");
        }
        var priorConnections = proxy.Connections;
        var configured = await Cef.UIThreadTaskFactory.StartNew(() =>
        {
            if (!context.CanSetPreference("proxy")) return (false, "Proxy preference is read-only.");
            var settings = new Dictionary<string, object> { ["mode"] = "fixed_servers", ["server"] = "https://127.0.0.1:" + proxy.Port };
            var ok = context.SetPreference("proxy", settings, out var error); return (ok, error);
        });
        if (!configured.Item1) throw new IOException("CEF rejected the test HTTPS proxy preference: " + configured.Item2);

        const string url = "http://aegis-secure-proxy.invalid/fixture";
        var failed = new TaskCompletionSource<CefErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<LoadErrorEventArgs> onError = (_, e) =>
        { if (e.Frame.IsMain && e.FailedUrl == url) failed.TrySetResult(e.ErrorCode); };
        browser.LoadError += onError;
        try
        {
            browser.Load(url);
            var error = await failed.Task.WaitAsync(TimeSpan.FromSeconds(25));
            await Wait(() => proxy.Connections > priorConnections, "CEF HTTPS proxy connection");
            Check(error.ToString().Contains("Cert", StringComparison.OrdinalIgnoreCase),
                "CEF reports a certificate error for the untrusted HTTPS proxy: " + error);
            await Task.Delay(500);
            Check(proxy.ApplicationRequests == 0, "CEF sends no application request to the untrusted HTTPS proxy");
        }
        finally { browser.LoadError -= onError; }
    }

    private sealed class SecureProxyFixture : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentBag<Task> clients = new();
        private readonly Task loop;
        private int connections, applicationRequests;
        public int Port { get; }
        public int Connections => Volatile.Read(ref connections);
        public int ApplicationRequests => Volatile.Read(ref applicationRequests);
        public X509Certificate2 Certificate { get; }

        private readonly bool ownsCertificate;
        private readonly int? forwardPort;
        private readonly ConcurrentQueue<int> upstreamPorts = new();
        public int[] UpstreamPorts => upstreamPorts.ToArray();

        public SecureProxyFixture(X509Certificate2? certificate = null, int? forwardPort = null)
        {
            this.forwardPort = forwardPort;
            ownsCertificate = certificate is null;
            Certificate = certificate ?? CreateUntrustedCertificate();
            listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = Task.Run(AcceptLoop);
        }

        private static X509Certificate2 CreateUntrustedCertificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(10));
            return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
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
                    Interlocked.Increment(ref connections);
                    using var tls = new SslStream(client.GetStream());
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, token);
                    using var request = new StreamReader(tls, Encoding.ASCII, false, 4096, leaveOpen: true);
                    var first = await request.ReadLineAsync(token);
                    if (first is null) return;
                    Interlocked.Increment(ref applicationRequests);
                    if (forwardPort is not int port ||
                        first != $"GET http://aegis-secure-proxy.invalid:{port}/trusted-proxy HTTP/1.1") return;
                    using var upstream = new TcpClient(AddressFamily.InterNetwork);
                    await ConnectObservedLoopback(upstream, port, upstreamPorts, token);
                    Console.WriteLine($"PROXY_TLS_RECEIPT: mode=https_proxy tls=completed target=aegis-secure-proxy.invalid:{port} utc={DateTimeOffset.UtcNow:O}");
                    using var upstreamStream = upstream.GetStream();
                    await upstreamStream.WriteAsync("GET /trusted-proxy HTTP/1.1\r\nHost: aegis-secure-proxy.invalid\r\nX-Aegis-Test-Proxy: yes\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
                    await upstreamStream.CopyToAsync(tls, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (AuthenticationException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        }

        public void Dispose()
        {
            stop.Cancel(); listener.Stop(); loop.GetAwaiter().GetResult();
            Task.WhenAll(clients.ToArray()).GetAwaiter().GetResult();
            if (ownsCertificate) Certificate.Dispose();
            stop.Dispose();
        }
    }
}
