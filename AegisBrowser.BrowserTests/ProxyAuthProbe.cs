using System.Net;
using System.Net.Sockets;
using System.Text;
using AegisBrowser;
using AegisBrowser.Core;
using CefSharp;
using CefSharp.Handler;
using CefSharp.WinForms;

namespace AegisBrowser.BrowserTests;

// These fake credentials and the callback are confined to the native test process.
internal static partial class Program
{
    private const string FixtureUser = "aegis-fixture-user";
    private const string FixturePassword = "aegis-fixture-password";
    private const string AuthOrigin = "http://aegis-auth.invalid";

    private static int ProxyAuthProbe(string profileRoot)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        var root = Path.GetFullPath(profileRoot); Directory.CreateDirectory(root);
        var initialized = false; var result = 0;
        try
        {
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(new CefSettings
            {
                RootCachePath = root, CachePath = Path.Combine(root, "cache"),
                CommandLineArgsDisabled = true, LogFile = Path.Combine(root, "cef-auth-probe.log")
            });
            if (!initialized) throw new IOException("CEF initialization failed.");
            using var context = Cef.GetGlobalRequestContext();
            using var goodProxy = new AuthProxyFixture();
            using var badProxy = new AuthProxyFixture();
            using var host = new Form { Width = 800, Height = 600, Text = "Local CEF proxy authentication probe" };
            host.Shown += async (_, _) =>
            {
                BrowserView? browser = null;
                try
                {
                    async Task Configure(int port)
                    {
                        var value = new Dictionary<string, object> { ["mode"] = "fixed_servers", ["server"] = "http://127.0.0.1:" + port };
                        var outcome = await Cef.UIThreadTaskFactory.StartNew(() =>
                        {
                            if (!context.CanSetPreference("proxy")) return (false, "Proxy preference is read-only.");
                            var ok = context.SetPreference("proxy", value, out var error); return (ok, error);
                        });
                        if (!outcome.Item1) throw new IOException("CEF rejected the test proxy preference: " + outcome.Item2);
                    }
                    BrowserView Open(ProxyChallengeHandler handler)
                    {
                        var view = new BrowserView("about:blank") { Dock = DockStyle.Fill, RequestHandler = handler };
                        Observe(view); host.Controls.Add(view); return view;
                    }

                    await Configure(goodProxy.Port);
                    var allowed = new ProxyChallengeHandler(goodProxy.Port, FixturePassword);
                    browser = Open(allowed); await Ready(browser);
                    await LoadReady(browser, AuthOrigin + "/allow");
                    Check(await Js(browser, "document.title") == "authenticated" && goodProxy.Accepted > 0 && allowed.Issued is > 0 and <= 2,
                        "CEF Basic proxy challenge reaches a scoped credential callback");
                    browser.Load(AuthOrigin + "/site-auth");
                    await Wait(() => allowed.SiteChallenges > 0 && goodProxy.SiteRequests > 0, "site authentication challenge");
                    await Task.Delay(500);
                    Check(goodProxy.SiteAuthorizationHeaders == 0 && allowed.SiteChallenges > 0,
                        "test callback denies destination-site authentication and sends no origin Authorization header");
                    await CloseWeb(browser); host.Controls.Remove(browser); browser = null;

                    await Configure(badProxy.Port);
                    var rejected = new ProxyChallengeHandler(badProxy.Port, "incorrect-fixture-password");
                    browser = Open(rejected); await Ready(browser);
                    browser.Load(AuthOrigin + "/allow");
                    await Wait(() => badProxy.RejectedCredentials > 0, "incorrect proxy credentials");
                    await Task.Delay(750);
                    Check(badProxy.Accepted == 0 && rejected.Issued <= 2,
                        "incorrect fixture credentials are rejected and the test callback issues at most two attempts");
                    await CloseWeb(browser); host.Controls.Remove(browser); browser = null;
                    Console.WriteLine($"PASS: {checks} CEF proxy authentication probe checks");
                }
                catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
                finally
                {
                    if (browser is not null && browser.IsBrowserInitialized)
                    {
                        try { await CloseWeb(browser); }
                        catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
                    }
                    host.Close();
                }
            };
            Application.Run(host);
        }
        catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
        finally { if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); } }
        if (result == 0) Console.WriteLine("PASS: CEF proxy authentication probe shutdown completed");
        return result;
    }

    private sealed class ProxyChallengeHandler : RequestHandler
    {
        private readonly ProxyDraft settings;
        private readonly string password;
        private int issued, proxyChallenges, siteChallenges;
        public int Issued => Volatile.Read(ref issued);
        public int SiteChallenges => Volatile.Read(ref siteChallenges);
        public ProxyChallengeHandler(int port, string password)
        {
            settings = new(ProxyProtocol.Http, "127.0.0.1", port, true, Guid.NewGuid(), "", true);
            this.password = password;
        }

        protected override bool GetAuthCredentials(IWebBrowser chromiumWebBrowser, IBrowser browser, string originUrl, bool isProxy,
            string host, int port, string realm, string scheme, IAuthCallback callback)
        {
            using (callback)
            {
                if (!isProxy) Interlocked.Increment(ref siteChallenges);
                if (ProxyAuthenticationScope.Allows(settings, isProxy, host, port, scheme) && Interlocked.Increment(ref proxyChallenges) <= 2)
                { Interlocked.Increment(ref issued); callback.Continue(FixtureUser, password); }
                else callback.Cancel();
            }
            return true;
        }
    }

    // This server responds only to a synthetic test origin and never forwards any request.
    private sealed class AuthProxyFixture : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new();
        private readonly System.Collections.Concurrent.ConcurrentBag<Task> clients = new();
        private readonly Task loop;
        private readonly string expected = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(FixtureUser + ":" + FixturePassword));
        private int accepted, rejectedCredentials, siteRequests, siteAuthorizationHeaders;
        public int Port { get; }
        public int Accepted => Volatile.Read(ref accepted);
        public int RejectedCredentials => Volatile.Read(ref rejectedCredentials);
        public int SiteRequests => Volatile.Read(ref siteRequests);
        public int SiteAuthorizationHeaders => Volatile.Read(ref siteAuthorizationHeaders);

        public AuthProxyFixture()
        {
            listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    clients.Add(Serve(client, cancellation.Token));
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (SocketException) when (cancellation.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (cancellation.IsCancellationRequested) { }
        }

        private async Task Serve(TcpClient client, CancellationToken token)
        {
            try
            {
                using (client)
                {
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                    var first = await reader.ReadLineAsync(token);
                    if (first is null || first.Length > 8192) return;
                    var pieces = first.Split(' ', 3);
                    if (pieces.Length != 3 || pieces[0] != "GET" || !Uri.TryCreate(pieces[1], UriKind.Absolute, out var target) ||
                        target.Scheme != "http" || target.Host != "aegis-auth.invalid") return;
                    var hasProxyHeader = false; var validProxyHeader = false; var originHeader = false;
                    for (var i = 0; i < 100; i++)
                    {
                        var line = await reader.ReadLineAsync(token);
                        if (string.IsNullOrEmpty(line)) break;
                        if (line.Length > 8192) throw new IOException("Fixture request headers are too large.");
                        var colon = line.IndexOf(':'); if (colon < 0) continue;
                        var name = line[..colon];
                        if (name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
                        { hasProxyHeader = true; validProxyHeader = line[(colon + 1)..].Trim().Equals(expected, StringComparison.Ordinal); }
                        if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) originHeader = true;
                    }
                    string status, challenge, body;
                    if (!validProxyHeader)
                    {
                        if (hasProxyHeader) Interlocked.Increment(ref rejectedCredentials);
                        status = "407 Proxy Authentication Required"; challenge = "Proxy-Authenticate: Basic realm=\"AegisProxyTest\"\r\n"; body = "";
                    }
                    else if (target.AbsolutePath == "/site-auth")
                    {
                        Interlocked.Increment(ref siteRequests);
                        if (originHeader) Interlocked.Increment(ref siteAuthorizationHeaders);
                        status = "401 Unauthorized"; challenge = "WWW-Authenticate: Basic realm=\"AegisSiteTest\"\r\n"; body = "";
                    }
                    else
                    {
                        Interlocked.Increment(ref accepted);
                        status = "200 OK"; challenge = ""; body = "<!doctype html><title>authenticated</title>";
                    }
                    var bytes = Encoding.ASCII.GetBytes(body);
                    var headers = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\n" + challenge + "Content-Type: text/html\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, token); await stream.WriteAsync(bytes, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        }

        public void Dispose()
        {
            cancellation.Cancel(); listener.Stop(); loop.GetAwaiter().GetResult();
            Task.WhenAll(clients.ToArray()).GetAwaiter().GetResult(); cancellation.Dispose();
        }
    }
}
