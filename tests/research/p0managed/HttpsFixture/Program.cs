using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

// A single synthetic loopback fixture on disposable hosted runners only. The
// public CA manifest is persisted before trust installation so cleanup can be
// performed in an independent always-run step after a crash or timeout.
internal static class Program
{
    private const string Prefix = "CN=Aegis P0 Disposable Test CA ";
    private static int Main(string[] args)
    {
        try
        {
            RequireRunner();
            if (args.Length == 2 && args[0] == "cleanup") { Cleanup(args[1]); return 0; }
            if (args.Length != 3 || args[0] != "serve") throw new ArgumentException("EXPECTED_SERVE_OR_CLEANUP");
            Serve(args[1], args[2]);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("P0_HTTPS_FIXTURE_FAILED: " + ex.GetType().Name + " " + ex.Message); return 1; }
    }

    private static void RequireRunner()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("CI") != "true" || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("DISPOSABLE_GITHUB_HOSTED_WINDOWS_REQUIRED");
    }

    private static string TrustDirectory(string path)
    {
        string temp = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? throw new InvalidOperationException("RUNNER_TEMP_REQUIRED"));
        string dir = Path.GetFullPath(path);
        if (!dir.StartsWith(Path.TrimEndingDirectorySeparator(temp) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(dir) || (File.GetAttributes(temp) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("UNSAFE_TRUST_DIRECTORY");
        return dir;
    }

    private static void Certutil(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "certutil.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("CERTUTIL_START_FAILED");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.WriteLine("Y");
        process.StandardInput.Close();
        if (!process.WaitForExit(20000)) { process.Kill(); process.WaitForExit(); throw new TimeoutException("CERTUTIL_TIMEOUT"); }
        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException("CERTUTIL_EXIT_" + process.ExitCode);
    }

    private static int Count(X509Certificate2 cert)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false)
            .Cast<X509Certificate2>().Count(c => c.RawData.AsSpan().SequenceEqual(cert.RawData));
    }

    private static void Cleanup(string path)
    {
        string dir = TrustDirectory(path);
        var files = Directory.GetFileSystemEntries(dir);
        if (files.Length > 1) throw new InvalidOperationException("UNEXPECTED_CA_MANIFESTS");
        foreach (string file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (!name.StartsWith("p0-ca-", StringComparison.Ordinal) || !Guid.TryParseExact(name[6..], "N", out _) ||
                Path.GetExtension(file) != ".cer" || (File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidOperationException("INVALID_CA_MANIFEST");
            using var cert = new X509Certificate2(file);
            if (cert.HasPrivateKey || cert.Subject != Prefix + name[6..]) throw new InvalidOperationException("CA_IDENTITY_MISMATCH");
            int count = Count(cert);
            if (count > 1) throw new InvalidOperationException("CA_NOT_UNIQUE");
            if (count == 1) Certutil("-user", "-f", "-delstore", "Root", cert.Thumbprint);
            if (Count(cert) != 0) throw new InvalidOperationException("CA_CLEANUP_FAILED");
            File.Delete(file);
        }
        Console.WriteLine("P0_HTTPS_TRUST_REMOVED");
    }

    private static void Serve(string directory, string readyPath)
    {
        string dir = TrustDirectory(directory);
        string ready = Path.GetFullPath(readyPath);
        string temp = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP")!);
        if (!ready.StartsWith(Path.TrimEndingDirectorySeparator(temp) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(ready) != Path.GetDirectoryName(dir)) throw new InvalidOperationException("UNSAFE_READY_PATH");
        string id = Guid.NewGuid().ToString("N");
        string manifest = Path.Combine(dir, "p0-ca-" + id + ".cer");
        using var rootKey = RSA.Create(2048);
        var rootReq = new CertificateRequest(Prefix + id, rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var root = rootReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); req.CertificateExtensions.Add(names.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        var serial = RandomNumberGenerator.GetBytes(16); serial[0] = (byte)((serial[0] & 0x7f) | 1);
        using var signed = req.Create(root, DateTimeOffset.UtcNow.AddMinutes(-4), DateTimeOffset.UtcNow.AddMinutes(45), serial);
        using var withKey = signed.CopyWithPrivateKey(key);
        using var server = new X509Certificate2(withKey.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.UserKeySet);
        File.WriteAllBytes(manifest, root.Export(X509ContentType.Cert));
        try
        {
            if (Count(root) != 0) throw new InvalidOperationException("TEST_CA_ALREADY_TRUSTED");
            Certutil("-user", "-f", "-addstore", "Root", manifest);
            if (Count(root) != 1) throw new InvalidOperationException("TEST_CA_INSTALL_NOT_VERIFIED");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            File.WriteAllText(ready, "https://127.0.0.1:" + port + "/");
            var stop = Task.Run(() => Console.ReadLine());
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(100));
            while (!stop.IsCompleted && !cancel.IsCancellationRequested)
            {
                var accept = listener.AcceptTcpClientAsync(cancel.Token).AsTask();
                if (Task.WhenAny(accept, stop).GetAwaiter().GetResult() == stop) break;
                using var client = accept.GetAwaiter().GetResult();
                client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
                using var tls = new SslStream(client.GetStream());
                try
                {
                    tls.AuthenticateAsServer(server, false, SslProtocols.Tls12 | SslProtocols.Tls13, false);
                    using var reader = new StreamReader(tls, Encoding.ASCII, false, 1024, true);
                    string? line; int count = 0;
                    while (!string.IsNullOrEmpty(line = reader.ReadLine()) && ++count < 100) { }
                    byte[] body = Encoding.UTF8.GetBytes("<!doctype html><body>AEGIS_P0_HTTPS_OK</body>");
                    byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    tls.Write(header); tls.Write(body); tls.Flush();
                }
                catch (Exception ex) when (ex is IOException or AuthenticationException) { Console.WriteLine("P0_HTTPS_CONNECTION_FAILED: " + ex.GetType().Name); }
            }
            File.Delete(ready);
        }
        finally { if (File.Exists(ready)) File.Delete(ready); Cleanup(dir); }
    }
}
