using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // All certificate material belongs to one disposable test process. No
    // private key is written to the repository or exported to the CI artifact.
    private sealed class PrivacyCertificates : IDisposable
    {
        private readonly RSA rootKey = RSA.Create(2048);
        public X509Certificate2 Root { get; }
        public X509Certificate2 Server { get; }
        public X509Certificate2 WrongHost { get; }
        public X509Certificate2 Untrusted { get; }
        public string Id { get; } = Guid.NewGuid().ToString("N");

        public PrivacyCertificates()
        {
            var request = new CertificateRequest("CN=AegisBrowser P403 Disposable Test CA " + Id,
                rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            Root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
            Server = MakeServer(Root, includeLoopbackIps: true);
            WrongHost = MakeServer(Root, includeLoopbackIps: false);
            using var otherKey = RSA.Create(2048);
            var other = new CertificateRequest("CN=localhost", otherKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var otherNames = new SubjectAlternativeNameBuilder(); otherNames.AddDnsName("localhost");
            other.CertificateExtensions.Add(otherNames.Build());
            using var selfSigned = other.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            Untrusted = ImportForSchannel(selfSigned);
        }

        private static X509Certificate2 MakeServer(X509Certificate2 root, bool includeLoopbackIps)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost");
            if (includeLoopbackIps) { names.AddIpAddress(IPAddress.Loopback); names.AddIpAddress(IPAddress.IPv6Loopback); }
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            var purposes = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(purposes, false));
            var serial = RandomNumberGenerator.GetBytes(16); serial[0] &= 0x7f; serial[0] |= 1;
            using var signed = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-4), DateTimeOffset.UtcNow.AddHours(1), serial);
            using var withKey = signed.CopyWithPrivateKey(key);
            return ImportForSchannel(withKey);
        }

        private static X509Certificate2 ImportForSchannel(X509Certificate2 certificate) =>
            new(certificate.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.UserKeySet);

        public void Dispose()
        {
            Untrusted.Dispose(); WrongHost.Dispose(); Server.Dispose(); Root.Dispose(); rootKey.Dispose();
        }
    }

    // A read/write user root store is used only on a declared disposable
    // GitHub-hosted runner. A public DER copy is written before installation
    // so a separate always-run workflow step can recover after interruption.
    private sealed class DisposableRunnerTrust : IDisposable
    {
        private readonly X509Certificate2 certificate;
        private bool installed;
        public static void RequireHostedRunner()
        {
            if (!OperatingSystem.IsWindows()
                || !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"), "github-hosted", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test CA installation is restricted to a disposable GitHub-hosted Windows runner.");
        }

        public DisposableRunnerTrust(X509Certificate2 root, string directory, string id)
        {
            RequireHostedRunner();
            var runnerTemp = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP")
                ?? throw new InvalidOperationException("RUNNER_TEMP is required."));
            var target = Path.GetFullPath(directory);
            if (!target.StartsWith(Path.TrimEndingDirectorySeparator(runnerTemp) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(target))
                throw new InvalidOperationException("The trust manifest directory must already exist under RUNNER_TEMP.");
            if ((File.GetAttributes(runnerTemp) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked trust directories are not supported.");
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidOperationException("Invalid test CA identity.");
            certificate = new X509Certificate2(root.Export(X509ContentType.Cert));
            if (certificate.HasPrivateKey) throw new InvalidOperationException("The trust manifest must contain only a public certificate.");
            var path = Path.Combine(target, "p403-ca-" + id + ".cer");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var encoded = certificate.Export(X509ContentType.Cert);
                stream.Write(encoded); stream.Flush(true);
            }
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
            if (Matches(store, certificate) != 0) throw new InvalidOperationException("The new test root already exists.");
            store.Add(certificate);
            installed = true;
            if (Matches(store, certificate) != 1) throw new InvalidOperationException("Could not verify test root installation.");
        }

        private static int Matches(X509Store store, X509Certificate2 cert) =>
            store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false)
                .Cast<X509Certificate2>().Count(c => c.RawData.AsSpan().SequenceEqual(cert.RawData));

        public void Dispose()
        {
            try
            {
                if (!installed) return;
                using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
                store.Remove(certificate);
                if (Matches(store, certificate) != 0) throw new InvalidOperationException("Test root remains in the user trust store.");
                Console.WriteLine("P403_TRUST: temporary user root removed");
            }
            finally { certificate.Dispose(); }
        }
    }

    private readonly record struct HttpsCookieObservation(bool Normal, bool HttpOnly, bool Lax, bool Strict, bool SetCookie);

    // Actual TLS and HTTP over loopback sockets. Only booleans for known
    // synthetic cookie markers are retained; raw request headers are dropped.
    private sealed class PrivacyHttpsServer : IDisposable
    {
        private readonly TcpListener ipv4;
        private readonly TcpListener ipv6;
        private readonly X509Certificate2 certificate;
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentBag<Task> clients = new();
        private readonly ConcurrentDictionary<string, ConcurrentQueue<HttpsCookieObservation>> observations = new();
        private readonly Task[] loops;
        private int accepted;
        public int Port { get; }
        public string TopA => $"https://127.0.0.1:{Port}/";
        public string TopB => $"https://[::1]:{Port}/";
        public string Third => $"https://localhost:{Port}/";

        public PrivacyHttpsServer(X509Certificate2 certificate, int port = 0)
        {
            this.certificate = certificate;
            ipv4 = new TcpListener(IPAddress.Loopback, port); ipv4.Start();
            Port = ((IPEndPoint)ipv4.LocalEndpoint).Port;
            try
            {
                ipv6 = new TcpListener(IPAddress.IPv6Loopback, Port);
                ipv6.Server.DualMode = false;
                ipv6.Start();
            }
            catch { ipv4.Stop(); throw; }
            loops = [Accept(ipv4), Accept(ipv6)];
        }

        public HttpsCookieObservation[] Take(string path) =>
            observations.TryRemove(path, out var queue) ? queue.ToArray() : [];

        private async Task Accept(TcpListener listener)
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    if (Interlocked.Increment(ref accepted) > 256)
                    {
                        client.Dispose();
                        throw new InvalidOperationException("HTTPS fixture connection limit exceeded.");
                    }
                    clients.Add(Serve(client));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(8));
                    using var stream = new SslStream(client.GetStream());
                    await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var line = await reader.ReadLineAsync(timeout.Token);
                    if (line is null) return;
                    var parts = line.Split(' ');
                    if (parts.Length < 2 || parts[0] != "GET") return;
                    var path = parts[1].Split('?')[0];
                    var normal = false; var httpOnly = false; var lax = false; var strict = false; var set = false;
                    for (var i = 0; i < 100; i++)
                    {
                        var header = await reader.ReadLineAsync(timeout.Token);
                        if (string.IsNullOrEmpty(header)) break;
                        if (!header.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) continue;
                        normal = header.Contains("p403normal=1", StringComparison.Ordinal);
                        httpOnly = header.Contains("p403http=1", StringComparison.Ordinal);
                        lax = header.Contains("p403lax=1", StringComparison.Ordinal);
                        strict = header.Contains("p403strict=1", StringComparison.Ordinal);
                        set = header.Contains("p403embedded=1", StringComparison.Ordinal);
                    }
                    observations.GetOrAdd(path, _ => new()).Enqueue(new(normal, httpOnly, lax, strict, set));
                    var body = path == "/embedded" || path == "/early-child"
                        ? "<!doctype html><script>parent.postMessage('p403:done','*');</script>"
                        : path == "/early-top"
                        ? "<!doctype html><script>window.p403early=false;addEventListener('message',e=>{if(e.origin==='"
                            + Third.TrimEnd('/') + "'&&e.data==='p403:done')window.p403early=true;});</script><iframe src='"
                            + Third + "early-child'></iframe>"
                        : "<!doctype html><title>Controlled HTTPS privacy fixture</title>";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var headers = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\n";
                    if (path == "/seed")
                        headers += "Set-Cookie: p403normal=1; Max-Age=3600; Path=/; SameSite=None; Secure\r\n"
                            + "Set-Cookie: p403http=1; Max-Age=3600; Path=/; SameSite=None; Secure; HttpOnly\r\n"
                            + "Set-Cookie: p403lax=1; Max-Age=3600; Path=/; SameSite=Lax; Secure\r\n"
                            + "Set-Cookie: p403strict=1; Max-Age=3600; Path=/; SameSite=Strict; Secure\r\n"
                            + "Set-Cookie: p403embedded=; Max-Age=0; Path=/; SameSite=None; Secure\r\n";
                    if (path is "/embedded" or "/early-child")
                        headers += "Set-Cookie: p403embedded=1; Max-Age=3600; Path=/; SameSite=None; Secure\r\n";
                    var prefix = Encoding.ASCII.GetBytes(headers + "Content-Length: " + bytes.Length + "\r\n\r\n");
                    await stream.WriteAsync(prefix, timeout.Token); await stream.WriteAsync(bytes, timeout.Token);
                }
                catch (OperationCanceledException) { }
                catch (AuthenticationException) { }
                catch (IOException) { }
                catch (SocketException) { }
            }
        }

        public void Dispose()
        {
            stop.Cancel(); ipv4.Stop(); ipv6.Stop();
            try { Task.WhenAll(loops).GetAwaiter().GetResult(); Task.WhenAll(clients.ToArray()).GetAwaiter().GetResult(); }
            finally { stop.Dispose(); }
        }
    }
}
