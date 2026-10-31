using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AegisBrowser.Core;

internal static class ProxyTransportTests
{
    private static int count;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL proxy transport: " + name); count++; }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch (ArgumentException) { count++; return; }
        throw new Exception("FAIL proxy transport (accepted): " + name);
    }

    public static void Run() => RunAsync(includeWindowsPersistenceAndTls: true).GetAwaiter().GetResult();
    public static void RunTransportOnly() => RunAsync(includeWindowsPersistenceAndTls: false).GetAwaiter().GetResult();

    private static async Task RunAsync(bool includeWindowsPersistenceAndTls)
    {
        count = 0;
        foreach (var invalid in new[] { "", "http://example.com/ip", "https://user:secret@example.com/", "https://example.com/?token=x",
            "https://example.com/#fragment", "file:///C:/data", " https://example.com/", "https://example.com/\n" })
            Reject(() => ProxyTransportTester.ValidateEndpoint(invalid), "unsafe diagnostic endpoint rejected");
        Check(ProxyTransportTester.ValidateEndpoint("https://example.com/ip").Scheme == "https" &&
            ProxyTransportTester.ValidateEndpoint("http://127.0.0.1:1234/ip").IsLoopback, "HTTPS and local HTTP fixtures accepted");

        using var origin = new OriginTrap();
        var endpoint = $"http://127.0.0.1:{origin.Port}/ip";
        var tester = new ProxyTransportTester();
        using (var proxy = new HttpProxyFixture(origin.Port, HttpMode.Success))
        {
            var result = await tester.TestAsync(Draft(proxy.Port), null, endpoint, CancellationToken.None);
            Check(result.Outcome == ProxyTransportOutcome.Connected && result.ReportedIp == "203.0.113.42" && proxy.Requests > 0 && origin.Requests == 0,
                "HTTP preflight reaches only the selected local proxy and parses the reported IP");
        }
        using (var proxy = new HttpProxyFixture(origin.Port, HttpMode.Authenticate))
        {
            var draft = Draft(proxy.Port) with { AuthenticationRequired = true, CredentialId = Guid.NewGuid() };
            var credentials = new ProxyCredentials("transport-fixture-user", "transport-fixture-password");
            var result = await tester.TestAsync(draft, credentials, endpoint, CancellationToken.None);
            Check(result.Outcome == ProxyTransportOutcome.Connected && proxy.ValidProxyAuthentication > 0 && proxy.OriginAuthorization == 0,
                "proxy credentials are used only for the proxy challenge");
            Check(!result.ToString().Contains(credentials.Username, StringComparison.Ordinal) &&
                !result.ToString().Contains(credentials.Password, StringComparison.Ordinal), "diagnostic result redacts credentials");
        }
        using (var proxy = new HttpProxyFixture(origin.Port, HttpMode.Authenticate))
        {
            var draft = Draft(proxy.Port) with { AuthenticationRequired = true, CredentialId = Guid.NewGuid() };
            var result = await tester.TestAsync(draft, new ProxyCredentials("transport-fixture-user", "incorrect"), endpoint, CancellationToken.None);
            Check(result.Outcome == ProxyTransportOutcome.AuthenticationFailed && proxy.InvalidProxyAuthentication > 0,
                "incorrect proxy credentials are classified as authentication failure");
        }
        using (var proxy = new HttpProxyFixture(origin.Port, HttpMode.OriginChallenge))
        {
            var draft = Draft(proxy.Port) with { AuthenticationRequired = true, CredentialId = Guid.NewGuid() };
            var result = await tester.TestAsync(draft, new ProxyCredentials("transport-fixture-user", "transport-fixture-password"), endpoint, CancellationToken.None);
            Check(result.Outcome == ProxyTransportOutcome.EndpointOrProxyFailed && proxy.OriginAuthorization == 0,
                "destination authentication failure does not receive proxy credentials");
        }
        using (var proxy = new HttpProxyFixture(origin.Port, HttpMode.Redirect))
        {
            var result = await tester.TestAsync(Draft(proxy.Port), null, endpoint, CancellationToken.None);
            Check(result.Outcome == ProxyTransportOutcome.EndpointOrProxyFailed && origin.Requests == 0, "redirect is not followed");
        }
        using (var proxy = new HttpProxyFixture(origin.Port, HttpMode.Large))
        {
            var result = await tester.TestAsync(Draft(proxy.Port), null, endpoint, CancellationToken.None);
            Check(result.Outcome == ProxyTransportOutcome.Inconclusive && result.ReportedIp is null, "oversized response is bounded");
        }
        using (var proxy = new HttpProxyFixture(origin.Port, HttpMode.AmbiguousIp))
        {
            var result = await tester.TestAsync(Draft(proxy.Port), null, endpoint, CancellationToken.None);
            Check(result.Outcome == ProxyTransportOutcome.Connected && result.ReportedIp is null, "ambiguous IP response is not reported");
        }
        using (var proxy = new HttpProxyFixture(origin.Port, HttpMode.Delay))
        {
            var result = await new ProxyTransportTester(TimeSpan.FromMilliseconds(400)).TestAsync(Draft(proxy.Port), null, endpoint, CancellationToken.None);
            Check(result.Outcome == ProxyTransportOutcome.TimedOut && origin.Requests == 0, "timeout is bounded without direct fallback");
        }
        using (var proxy = new HttpProxyFixture(origin.Port, HttpMode.Success))
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var result = await tester.TestAsync(Draft(proxy.Port), null, endpoint, cancellation.Token);
            Check(result.Outcome == ProxyTransportOutcome.Canceled && proxy.Requests == 0, "canceled test makes no request");
        }
        var closedPort = ClosedPort();
        var refused = await tester.TestAsync(Draft(closedPort), null, endpoint, CancellationToken.None);
        Check(refused.Outcome == ProxyTransportOutcome.ConnectionFailed && origin.Requests == 0, "unavailable proxy does not fall back to the origin");

        using (var socks = new SocksFixture(origin.Port))
        {
            var result = await tester.TestAsync(Draft(socks.Port) with { Protocol = ProxyProtocol.Socks5 }, null, endpoint, CancellationToken.None);
            Check(result.Outcome == ProxyTransportOutcome.Connected && socks.Handshakes > 0 && origin.Requests == 0,
                "SOCKS5 .NET preflight uses the controlled proxy");
        }
        var unsupported = await tester.TestAsync(Draft(1) with { Protocol = ProxyProtocol.Socks5, AuthenticationRequired = true, CredentialId = Guid.NewGuid() },
            null, endpoint, CancellationToken.None);
        Check(unsupported.Outcome == ProxyTransportOutcome.Unsupported, "SOCKS5 authentication is not advertised");
        if (includeWindowsPersistenceAndTls && OperatingSystem.IsWindows())
        {
            await TestHttpsTunnel(tester);
            await TestTlsRejection(tester, origin.Port, endpoint);
            await TestSavedRevision(tester, origin.Port, endpoint);
        }
        Console.WriteLine($"PASS: {count} proxy transport diagnostic checks");
    }

    private static ProxyDraft Draft(int port) => new(ProxyProtocol.Http, "127.0.0.1", port, false, null, "", true);

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }

    private static async Task TestTlsRejection(ProxyTransportTester tester, int originPort, string endpoint)
    {
        using var tlsProxy = new TlsProxyFixture();
        // First prove that the fixture can complete TLS when its own certificate is
        // explicitly trusted for this one test client. No system trust store changes.
        using (var client = new TcpClient())
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            await client.ConnectAsync(IPAddress.Loopback, tlsProxy.Port, timeout.Token);
            await VerifyFixtureTls(client.GetStream(), tlsProxy.Certificate, "127.0.0.1", timeout.Token);
        }
        var before = tlsProxy.Connections;
        var result = await tester.TestAsync(Draft(tlsProxy.Port) with { Protocol = ProxyProtocol.Https }, null, endpoint, CancellationToken.None);
        Check(tlsProxy.Connections > before && result.Outcome == ProxyTransportOutcome.TlsFailed,
            $"untrusted HTTPS proxy certificate is rejected without a TLS override (connections: {tlsProxy.Connections}, outcome: {result.Outcome})");
    }

    private static async Task VerifyFixtureTls(Stream transport, X509Certificate2 certificate, string host, CancellationToken cancellation)
    {
        using var stream = new SslStream(transport);
        var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        policy.CustomTrustStore.Add(certificate);
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        { TargetHost = host, CertificateChainPolicy = policy }, cancellation);
        Check(stream.IsAuthenticated && stream.IsEncrypted, "controlled TLS fixture completes a handshake with its explicitly trusted certificate");
    }

    private static async Task TestHttpsTunnel(ProxyTransportTester tester)
    {
        using (var tunnel = new HttpTunnelProxyFixture())
        {
            // Exercise the same CONNECT/TLS fixture with a single-client custom root;
            // this proves the following rejection is not just a broken TLS server.
            using (var client = new TcpClient())
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                await client.ConnectAsync(IPAddress.Loopback, tunnel.Port, timeout.Token);
                var network = client.GetStream();
                var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes("transport-fixture-user:transport-fixture-password"));
                await network.WriteAsync(Encoding.ASCII.GetBytes("CONNECT diagnostic.invalid:443 HTTP/1.1\r\nHost: diagnostic.invalid:443\r\nProxy-Authorization: Basic " + auth + "\r\n\r\n"), timeout.Token);
                using (var reader = new StreamReader(network, Encoding.ASCII, false, 4096, leaveOpen: true))
                {
                    Check((await reader.ReadLineAsync(timeout.Token)) == "HTTP/1.1 200 Connection Established", "controlled CONNECT fixture accepts the synthetic credential");
                    for (var i = 0; i < 100; i++) { var line = await reader.ReadLineAsync(timeout.Token); if (string.IsNullOrEmpty(line)) break; }
                }
                await VerifyFixtureTls(network, tunnel.Certificate, "diagnostic.invalid", timeout.Token);
            }
            var before = tunnel.ValidAuthentication;
            var draft = Draft(tunnel.Port) with { AuthenticationRequired = true, CredentialId = Guid.NewGuid() };
            var result = await tester.TestAsync(draft, new ProxyCredentials("transport-fixture-user", "transport-fixture-password"),
                "https://diagnostic.invalid/ip", CancellationToken.None);
            Check(tunnel.ValidAuthentication > before && result.Outcome == ProxyTransportOutcome.TlsFailed,
                $"HTTPS target uses an authenticated proxy CONNECT and rejects its untrusted TLS endpoint (outcome: {result.Outcome})");
        }
        using (var tunnel = new HttpTunnelProxyFixture())
        {
            var draft = Draft(tunnel.Port) with { AuthenticationRequired = true, CredentialId = Guid.NewGuid() };
            var result = await tester.TestAsync(draft, new ProxyCredentials("transport-fixture-user", "incorrect"),
                "https://diagnostic.invalid/ip", CancellationToken.None);
            Check(tunnel.InvalidAuthentication > 0 && tunnel.TlsAttempts == 0 &&
                result.Outcome is ProxyTransportOutcome.AuthenticationFailed or ProxyTransportOutcome.EndpointOrProxyFailed,
                $"rejected proxy CONNECT credentials do not start endpoint TLS (outcome: {result.Outcome})");
        }
    }

    private static async Task TestSavedRevision(ProxyTransportTester tester, int originPort, string endpoint)
    {
        var root = Path.Combine(Path.GetTempPath(), "aegis-proxy-diagnostic-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profiles = new ProfileRepository(root); var profile = profiles.Create("diagnostic");
            var repository = new ProxyRepository(root); var vault = new ProxyCredentialVault(root);
            var editor = new ProxyConfigurationEditor(root, repository, vault);
            using var proxy = new HttpProxyFixture(originPort, HttpMode.Authenticate);
            var draft = Draft(proxy.Port) with { AuthenticationRequired = true };
            var config = editor.Save(profile, draft, 0, new ProxyCredentials("transport-fixture-user", "transport-fixture-password")).Configuration;
            var diagnostics = new ProxySavedDiagnostics(root, repository, vault, tester);
            var result = await diagnostics.TestAsync(profile, config, endpoint, CancellationToken.None);
            Check(result.Outcome == ProxyTransportOutcome.Connected && proxy.ValidProxyAuthentication > 0, "saved revision loads protected credentials for the selected profile");
            vault.Delete(profile.Id, config.Settings.CredentialId!.Value);
            var beforeMissing = proxy.Requests;
            result = await diagnostics.TestAsync(profile, config, endpoint, CancellationToken.None);
            Check(result.Outcome == ProxyTransportOutcome.MissingCredentials && proxy.Requests == beforeMissing,
                "missing protected credentials do not issue a request");
            var updated = editor.Save(profile, new ProxyDraft(ProxyProtocol.Direct, "", 0, false, null, "", false), config.Revision, null);
            var before = proxy.Requests; var stale = false;
            try { await diagnostics.TestAsync(profile, config, endpoint, CancellationToken.None); } catch (IOException) { stale = true; }
            Check(stale && proxy.Requests == before && updated.Configuration.Revision > config.Revision, "stale saved diagnostic does not issue a request");
            using var held = new ProfileLease(BrowserPolicy.ProfilePath(root, profile.Slug));
            var busy = false;
            try { await diagnostics.TestAsync(profile, updated.Configuration, endpoint, CancellationToken.None); } catch (IOException) { busy = true; }
            Check(busy, "saved diagnostic respects the existing profile lease");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private enum HttpMode { Success, Authenticate, OriginChallenge, Redirect, Large, AmbiguousIp, Delay }

    private abstract class LoopbackFixture : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        protected readonly CancellationTokenSource StopToken = new();
        private readonly ConcurrentBag<Task> clients = new();
        private readonly Task loop;
        public int Port { get; }
        protected LoopbackFixture()
        {
            listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = Task.Run(AcceptLoop);
        }
        private async Task AcceptLoop()
        {
            try
            {
                while (!StopToken.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(StopToken.Token);
                    clients.Add(ServeSafe(client, StopToken.Token));
                }
            }
            catch (OperationCanceledException) when (StopToken.IsCancellationRequested) { }
            catch (SocketException) when (StopToken.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (StopToken.IsCancellationRequested) { }
        }
        private async Task ServeSafe(TcpClient client, CancellationToken token)
        {
            try { using (client) await Serve(client, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (AuthenticationException) { }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        }
        protected abstract Task Serve(TcpClient client, CancellationToken token);
        public virtual void Dispose()
        {
            StopToken.Cancel(); listener.Stop(); loop.GetAwaiter().GetResult();
            Task.WhenAll(clients.ToArray()).GetAwaiter().GetResult(); StopToken.Dispose();
        }
    }

    private sealed class OriginTrap : LoopbackFixture
    {
        private int requests;
        public int Requests => Volatile.Read(ref requests);
        protected override async Task Serve(TcpClient client, CancellationToken token)
        {
            Interlocked.Increment(ref requests);
            var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await client.GetStream().WriteAsync(bytes, token);
        }
    }

    private sealed class HttpProxyFixture(int originPort, HttpMode mode) : LoopbackFixture
    {
        private static readonly string Expected = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("transport-fixture-user:transport-fixture-password"));
        private int requests, validProxyAuthentication, invalidProxyAuthentication, originAuthorization;
        public int Requests => Volatile.Read(ref requests);
        public int ValidProxyAuthentication => Volatile.Read(ref validProxyAuthentication);
        public int InvalidProxyAuthentication => Volatile.Read(ref invalidProxyAuthentication);
        public int OriginAuthorization => Volatile.Read(ref originAuthorization);
        protected override async Task Serve(TcpClient client, CancellationToken token)
        {
            using var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            var first = await reader.ReadLineAsync(token); if (first is null || first.Length > 8192) return;
            var parts = first.Split(' ', 3);
            if (parts.Length != 3 || parts[0] != "GET" || !Uri.TryCreate(parts[1], UriKind.Absolute, out var target) ||
                target.Host != "127.0.0.1" || target.Port != originPort) return;
            var hasProxy = false; var validProxy = false; var hasOrigin = false;
            for (var i = 0; i < 100; i++)
            {
                var line = await reader.ReadLineAsync(token); if (string.IsNullOrEmpty(line)) break;
                if (line.Length > 8192) throw new IOException("Fixture request headers are too large.");
                var colon = line.IndexOf(':'); if (colon < 0) continue; var name = line[..colon];
                if (name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
                { hasProxy = true; validProxy = line[(colon + 1)..].Trim().Equals(Expected, StringComparison.Ordinal); }
                if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) hasOrigin = true;
            }
            Interlocked.Increment(ref requests); if (hasOrigin) Interlocked.Increment(ref originAuthorization);
            if (mode == HttpMode.Delay) { await Task.Delay(Timeout.Infinite, token); return; }
            string status, extra, body;
            if (mode is HttpMode.Authenticate or HttpMode.OriginChallenge && !validProxy)
            {
                if (hasProxy) Interlocked.Increment(ref invalidProxyAuthentication);
                status = "407 Proxy Authentication Required"; extra = "Proxy-Authenticate: Basic realm=\"AegisTransportFixture\"\r\n"; body = "";
            }
            else
            {
                if (validProxy) Interlocked.Increment(ref validProxyAuthentication);
                (status, extra, body) = mode switch
                {
                    HttpMode.OriginChallenge => ("401 Unauthorized", "WWW-Authenticate: Basic realm=\"AegisOriginFixture\"\r\n", ""),
                    HttpMode.Redirect => ("302 Found", "Location: http://127.0.0.1:" + originPort + "/redirect-target\r\n", ""),
                    HttpMode.Large => ("200 OK", "", new string('x', 5000)),
                    HttpMode.AmbiguousIp => ("200 OK", "", "{\"ip\":\"203.0.113.42\",\"IP\":\"203.0.113.43\"}"),
                    _ => ("200 OK", "", "{\"ip\":\"203.0.113.42\"}")
                };
            }
            var bytes = Encoding.ASCII.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\n" + extra + "Content-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, token); await stream.WriteAsync(bytes, token);
        }
    }

    private sealed class SocksFixture(int originPort) : LoopbackFixture
    {
        private int handshakes;
        public int Handshakes => Volatile.Read(ref handshakes);
        protected override async Task Serve(TcpClient client, CancellationToken token)
        {
            using var stream = client.GetStream();
            var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, token);
            if (greeting[0] != 5 || greeting[1] is 0 or > 16) return;
            var methods = new byte[greeting[1]]; await stream.ReadExactlyAsync(methods, token);
            if (!methods.Contains((byte)0)) return;
            await stream.WriteAsync(new byte[] { 5, 0 }, token);
            var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
            if (header[0] != 5 || header[1] != 1 || header[2] != 0) return;
            byte[] address;
            if (header[3] == 1) address = new byte[4];
            else if (header[3] == 4) address = new byte[16];
            else if (header[3] == 3) { var length = new byte[1]; await stream.ReadExactlyAsync(length, token); address = new byte[length[0]]; }
            else return;
            await stream.ReadExactlyAsync(address, token); var portBytes = new byte[2]; await stream.ReadExactlyAsync(portBytes, token);
            var requestedPort = (portBytes[0] << 8) | portBytes[1];
            if (requestedPort != originPort) return;
            if (header[3] == 1 && !address.SequenceEqual(IPAddress.Loopback.GetAddressBytes())) return;
            if (header[3] == 3 && Encoding.ASCII.GetString(address) != "127.0.0.1") return;
            if (header[3] == 4 && !new IPAddress(address).Equals(IPAddress.IPv6Loopback)) return;
            Interlocked.Increment(ref handshakes);
            await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, token);
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            var first = await reader.ReadLineAsync(token); if (first is null || !first.StartsWith("GET /ip ", StringComparison.Ordinal)) return;
            for (var i = 0; i < 100; i++) { var line = await reader.ReadLineAsync(token); if (string.IsNullOrEmpty(line)) break; }
            const string body = "203.0.113.42";
            var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n" + body);
            await stream.WriteAsync(bytes, token);
        }
    }

    private sealed class TlsProxyFixture : LoopbackFixture
    {
        private readonly X509Certificate2 certificate;
        private int connections;
        public int Connections => Volatile.Read(ref connections);
        public X509Certificate2 Certificate => certificate;
        public TlsProxyFixture()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(10));
            certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.UserKeySet);
        }
        protected override async Task Serve(TcpClient client, CancellationToken token)
        {
            Interlocked.Increment(ref connections);
            using var tls = new SslStream(client.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, token);
        }
        public override void Dispose() { base.Dispose(); certificate.Dispose(); }
    }

    private sealed class HttpTunnelProxyFixture : LoopbackFixture
    {
        private static readonly string Expected = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("transport-fixture-user:transport-fixture-password"));
        private readonly X509Certificate2 certificate;
        private int validAuthentication, invalidAuthentication, tlsAttempts;
        public int ValidAuthentication => Volatile.Read(ref validAuthentication);
        public int InvalidAuthentication => Volatile.Read(ref invalidAuthentication);
        public int TlsAttempts => Volatile.Read(ref tlsAttempts);
        public X509Certificate2 Certificate => certificate;
        public HttpTunnelProxyFixture()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=diagnostic.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("diagnostic.invalid");
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(10));
            certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.UserKeySet);
        }
        protected override async Task Serve(TcpClient client, CancellationToken token)
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            var first = await reader.ReadLineAsync(token);
            if (first != "CONNECT diagnostic.invalid:443 HTTP/1.1") return;
            string? proxyAuthorization = null;
            for (var i = 0; i < 100; i++)
            {
                var line = await reader.ReadLineAsync(token); if (string.IsNullOrEmpty(line)) break;
                if (line.Length > 8192) throw new IOException("Fixture request headers are too large.");
                var colon = line.IndexOf(':');
                if (colon > 0 && line[..colon].Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
                    proxyAuthorization = line[(colon + 1)..].Trim();
            }
            if (proxyAuthorization != Expected)
            {
                if (proxyAuthorization is not null) Interlocked.Increment(ref invalidAuthentication);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"AegisTunnelFixture\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token);
                return;
            }
            Interlocked.Increment(ref validAuthentication);
            await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), token);
            Interlocked.Increment(ref tlsAttempts);
            using var tls = new SslStream(stream);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, token);
        }
        public override void Dispose() { base.Dispose(); certificate.Dispose(); }
    }
}
