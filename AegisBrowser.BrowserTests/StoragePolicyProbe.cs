using AegisBrowser;
using CefSharp;
using CefSharp.WinForms;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AegisBrowser.Core;

namespace AegisBrowser.BrowserTests;

// Isolated research probe. HTTPS resources are supplied by an in-process CEF
// scheme handler; they are not socket traffic or a substitute for TLS testing.
internal static partial class Program
{
    private const string StorageThird = "https://p402-third.invalid/";
    private const string StorageTopA = "https://p402-site-a.invalid/";
    private const string StorageTopB = "https://p402-site-b.invalid/";
    private const string CookieModePreference = "profile.cookie_controls_mode";

    private static int StoragePolicyProbe(string profileRoot, string phase)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var root = Path.GetFullPath(profileRoot);
        Directory.CreateDirectory(root);
        var initialized = false;
        var result = 1;
        try
        {
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(new CefSettings
            {
                RootCachePath = root, CachePath = Path.Combine(root, "cache"),
                CommandLineArgsDisabled = true, LogFile = Path.Combine(root, "cef-storage-probe.log")
            });
            if (!initialized) throw new InvalidOperationException("CEF initialization failed.");
            using var context = Cef.GetGlobalRequestContext();
            var fixture = new StorageFixture();
            using var server = new StorageHttpFixture();
            foreach (var origin in new[] { StorageThird, StorageTopA, StorageTopB })
                if (!context.RegisterSchemeHandlerFactory("https", new Uri(origin).Host, fixture))
                    throw new InvalidOperationException("Could not register an isolated fixture origin.");
            foreach (var hostName in new[] { "browser-tests.invalid", "provider-tests.invalid" })
                if (!context.RegisterSchemeHandlerFactory("https", hostName, new Fixture()))
                    throw new InvalidOperationException("Could not register an OAuth fixture origin.");
            using var host = new Form { Width = 800, Height = 600 };
            host.Shown += async (_, _) =>
            {
                BrowserView? browser = null;
                try
                {
                    CookiePolicyFailureTests();
                    if (phase == "storage-policy-read")
                    {
                        var saved = new PrivacySettingsStore(root).Load();
                        Check(saved.BlockThirdPartyCookies == true, "cookie policy setting survives a process restart");
                        var startup = ThirdPartyCookiePolicy.ApplyAtStartup(saved.BlockThirdPartyCookies);
                        Check(startup.Succeeded, "production cookie policy is applied before browser creation");
                    }
                    browser = new BrowserView(StorageThird) { Dock = DockStyle.Fill };
                    browser.RequestHandler = new SafeRequests(Console.WriteLine);
                    Observe(browser);
                    host.Controls.Add(browser);
                    await Ready(browser);
                    var initial = await Cef.UIThreadTaskFactory.StartNew(() =>
                        (exists: context.HasPreference(CookieModePreference),
                         writable: context.CanSetPreference(CookieModePreference),
                         value: context.GetPreference(CookieModePreference)?.ToString() ?? "(absent)"));
                    Console.WriteLine($"P402_PREFERENCE: exists={initial.exists} writable={initial.writable} initial={initial.value}");
                    Check(initial.exists && initial.writable, "pinned CEF exposes the writable cookie preference");
                    if (phase == "storage-policy-read")
                    {
                        await LoadReady(browser, server.Third + "inspect");
                        Check(await Js(browser, "document.cookie.includes('p402net=1')") == "True",
                            "first-party persistent cookie survives a process restart");
                        var afterRestart = await StorageScenario(browser, fixture, server, "restart-enabled");
                        CheckBlocked(afterRestart);
                    }
                    else
                    {
                        await StorageScenario(browser, fixture, server, "default");
                        var baseline = await Cef.UIThreadTaskFactory.StartNew(() =>
                        {
                            var ok = context.SetPreference(CookieModePreference, 0, out var error);
                            return (ok, error, readback: context.GetPreference(CookieModePreference)?.ToString() ?? "(absent)");
                        });
                        Check(baseline.ok && baseline.readback == "0", "controlled baseline cookie mode is accepted");
                        var allowed = await StorageScenario(browser, fixture, server, "baseline-allowed");
                        Check(allowed.FirstParty && allowed.Total == 1 && allowed.Visible == 1 && allowed.HttpOnly == 1
                            && allowed.Lax == 0 && allowed.Strict == 0 && allowed.EmbeddedStored,
                            "baseline sends and stores controlled cross-site cookies");
                        var enabled = await Cef.UIThreadTaskFactory.StartNew(() => ThirdPartyCookiePolicy.ApplyOnUiThread(context, true));
                        Check(enabled.Succeeded, "production cookie policy is accepted and read back");
                        var blocked = await StorageScenario(browser, fixture, server, "protection-enabled");
                        CheckBlocked(blocked);
                        await CookiePolicyOAuth(browser);
                        if (phase == "storage-policy-write")
                        {
                            new PrivacySettingsStore(root).Save(new PrivacyConfiguration { BlockThirdPartyCookies = true });
                            using var manager = context.GetCookieManager(null);
                            using var flush = new Completion();
                            Check(manager.FlushStore(flush), "persistent cookie flush was accepted");
                            await flush.Done.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        }
                    }
                    await CloseWeb(browser);
                    host.Controls.Remove(browser);
                    browser = null;
                    Console.WriteLine("PASS: isolated CEF storage policy probe completed");
                    result = 0;
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                finally
                {
                    // A failure is never reported as a successful close; use the
                    // existing native close protocol if an initialized browser remains.
                    if (browser is { IsDisposed: false, IsBrowserInitialized: true })
                    {
                        try { await CloseWeb(browser); } catch (Exception ex) { Console.Error.WriteLine("Probe close incomplete: " + ex.Message); }
                    }
                    host.Close();
                }
            };
            Application.Run(host);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
        finally
        {
            if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); }
        }
        if (result == 0) Console.WriteLine("PASS: isolated CEF storage policy probe shutdown completed");
        return result;
    }

    private readonly record struct HttpStorageObservation(bool FirstParty, int Total, int Visible, int HttpOnly,
        int Lax, int Strict, bool EmbeddedStored);

    private static void CookiePolicyFailureTests()
    {
        var fake = new FakeCookiePreference { Exists = false };
        var unavailable = ThirdPartyCookiePolicy.Apply(fake, true);
        Check(!unavailable.Succeeded && fake.SetCalls == 0, "missing cookie preference fails closed");
        Check(ThirdPartyCookiePolicy.PreventBrowsing(true, unavailable)
            && !ThirdPartyCookiePolicy.PreventBrowsing(false, unavailable),
            "failed opt-in prevents browsing while a failed opt-out remains recoverable");
        fake.Exists = true; fake.Writable = false;
        Check(!ThirdPartyCookiePolicy.Apply(fake, true).Succeeded && fake.SetCalls == 0, "read-only cookie preference fails closed");
        fake.Writable = true; fake.Accept = false;
        Check(!ThirdPartyCookiePolicy.Apply(fake, true).Succeeded, "rejected cookie preference fails closed");
        fake.Accept = true; fake.Mismatch = true;
        Check(!ThirdPartyCookiePolicy.Apply(fake, true).Succeeded, "incorrect cookie preference readback fails closed");
        fake.Mismatch = false;
        Check(ThirdPartyCookiePolicy.Apply(fake, true).Succeeded && Equals(fake.LastValue, 1), "opt-in selects Chromium block mode");
        Check(ThirdPartyCookiePolicy.Apply(fake, false).Succeeded && fake.LastValue is null, "opt-out restores the Chromium default");
        Check(ThirdPartyCookiePolicy.ApplyAtStartup(null).Succeeded, "legacy setting preserves the existing browser preference");
    }

    private sealed class FakeCookiePreference : ThirdPartyCookiePolicy.IPreference
    {
        public bool Exists { get; set; } = true;
        public bool Writable { get; set; } = true;
        public bool Accept { get; set; } = true;
        public bool Mismatch { get; set; }
        public int SetCalls { get; private set; }
        public object? LastValue { get; private set; }
        public object? Value => Mismatch ? 2 : LastValue;
        public bool Set(object? value, out string error)
        {
            SetCalls++;
            LastValue = value;
            error = Accept ? "" : "test rejection";
            return Accept;
        }
    }

    private static void CheckBlocked(HttpStorageObservation observation) =>
        Check(observation.FirstParty && observation.Total == 1 && observation.Visible == 0 && observation.HttpOnly == 0
            && observation.Lax == 0 && observation.Strict == 0 && !observation.EmbeddedStored,
            "protection preserves first-party cookies and suppresses third-party send and save");

    private static async Task CookiePolicyOAuth(BrowserView browser)
    {
        await LoadReady(browser, Origin);
        await Js(browser, """
            document.cookie='p402auth=1; Max-Age=3600; Secure; SameSite=Lax; Path=/';
            window.oauthDone=false;
            window.addEventListener('message', e => {
                if(e.origin===location.origin && e.data==='oauth-ok') window.oauthDone=true;
            });
            'ready'
            """);
        await Cef.UIThreadTaskFactory.StartNew(() => browser.GetBrowserHost().ExecuteDevToolsMethod(0, "Runtime.evaluate",
            new Dictionary<string, object> { ["expression"] = "window.open('https://provider-tests.invalid/authorize','p402-oauth')", ["userGesture"] = true }));
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (await Js(browser, "window.oauthDone") != "True")
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Protected-profile OAuth fixture did not complete.");
            await Task.Delay(100);
        }
        Check(await Js(browser, "document.cookie.includes('p402auth=1')") == "True",
            "OAuth-style popup redirect and postMessage preserve first-party cookies under protection");
        await Wait(() => ((SafePopups)browser.LifeSpanHandler).PopupCount == 0, "protected OAuth popup closure");
    }

    private static async Task<HttpStorageObservation> StorageScenario(BrowserView browser, StorageFixture fixture, StorageHttpFixture server, string label)
    {
        await LoadReady(browser, StorageThird);
        var firstParty = await Js(browser, """
            document.cookie='p402probe=1; Max-Age=3600; Path=/; SameSite=None; Secure';
            document.cookie.includes('p402probe=1')
            """);
        fixture.Reset();
        var caseId = Guid.NewGuid().ToString("N");
        async Task<string> Embed(string top, bool write)
        {
            await LoadReady(browser, top);
            await Js(browser, """
                window.p402result='pending';
                window.addEventListener('message', e => {
                    if(e.origin==='https://p402-third.invalid' && typeof e.data==='string'
                       && e.data.startsWith('p402:')) window.p402result=e.data;
                });
                'ready'
                """);
            await Js(browser, "const f=document.createElement('iframe'); f.src='" + StorageThird
                + "embedded?case=" + caseId + (write ? "&write=1" : "") + "'; document.body.appendChild(f); 'started'");
            var deadline = DateTime.UtcNow.AddSeconds(20);
            string value;
            while ((value = await Js(browser, "window.p402result")) == "pending")
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Cross-site fixture did not complete.");
                await Task.Delay(100);
            }
            return value;
        }
        var siteA = await Embed(StorageTopA, true);
        var siteAAgain = await Embed(StorageTopA, false);
        var siteB = await Embed(StorageTopB, false);
        var counts = fixture.Counts();
        Console.WriteLine($"P402_HTTPS_SCHEME: scenario={label} first_party_cookie={firstParty} site_a_write={siteA} site_a_read={siteAAgain} site_b_read={siteB} embedded_requests={counts.total} request_cookie_present={counts.withCookie}");
        Check(counts.total == 3, "both distinct-site embedded HTTPS fixture requests completed");
        Check(siteA.EndsWith("storage=0", StringComparison.Ordinal)
            && siteAAgain.EndsWith("storage=1", StringComparison.Ordinal)
            && siteB.EndsWith("storage=0", StringComparison.Ordinal),
            "same embedded origin has separate localStorage in two controlled top-level sites");
        return await HttpStorageScenario(browser, server, label);
    }

    private static async Task<HttpStorageObservation> HttpStorageScenario(BrowserView browser, StorageHttpFixture server, string label)
    {
        Check(SiteClassifier.Default.IsThirdParty(server.Third, server.Top) == true,
            "localhost and loopback IP are different test sites");
        await LoadReady(browser, server.Third + "seed");
        var firstParty = await Js(browser, "document.cookie.includes('p402net=1')");
        server.Reset();
        await LoadReady(browser, server.Top);
        await Js(browser, """
            window.p402http='pending';
            window.addEventListener('message', e => {
                if(e.origin===window.p402third && typeof e.data==='string' && e.data.startsWith('p402http:'))
                    window.p402http=e.data;
            });
            'ready'
            """);
        await Js(browser, "window.p402third='" + server.Third.TrimEnd('/') + "';"
            + "const f=document.createElement('iframe'); f.src='" + server.Third + "embedded'; document.body.appendChild(f); 'started'");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        string result;
        while ((result = await Js(browser, "window.p402http")) == "pending")
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Loopback iframe did not complete.");
            await Task.Delay(100);
        }
        var counts = server.Counts();
        await LoadReady(browser, server.Third + "inspect");
        var embeddedCookieStored = await Js(browser, "document.cookie.includes('p402third=1')");
        Console.WriteLine($"P402_HTTP_LOOPBACK: scenario={label} first_party_cookie={firstParty} iframe={result} requests={counts.total} visible_cookie_sent={counts.visible} httponly_cookie_sent={counts.httpOnly} lax_cookie_sent={counts.lax} strict_cookie_sent={counts.strict} embedded_cookie_stored={embeddedCookieStored}");
        Check(counts.total == 1, "cross-site iframe reached the controlled loopback server");
        return new(firstParty == "True", counts.total, counts.visible, counts.httpOnly, counts.lax, counts.strict, embeddedCookieStored == "True");
    }

    private sealed class StorageFixture : ISchemeHandlerFactory
    {
        private readonly ConcurrentQueue<bool> embedded = new();
        public void Reset() { while (embedded.TryDequeue(out _)) { } }
        public (int total, int withCookie) Counts()
        {
            var snapshot = embedded.ToArray();
            return (snapshot.Length, snapshot.Count(present => present));
        }
        public IResourceHandler Create(IBrowser browser, IFrame frame, string schemeName, IRequest request)
        {
            var uri = new Uri(request.Url);
            var body = "<!doctype html><title>Local storage policy fixture</title>";
            if (uri.Host == new Uri(StorageThird).Host && uri.AbsolutePath == "/embedded")
            {
                // Record only presence of the fixed synthetic marker, never a
                // cookie value, URL query or other request headers.
                embedded.Enqueue((request.Headers["Cookie"] ?? "").Contains("p402probe=1", StringComparison.Ordinal));
                var query = uri.Query.TrimStart('?').Split('&');
                var caseValue = query.Single(x => x.StartsWith("case=", StringComparison.Ordinal))[5..];
                if (!Guid.TryParseExact(caseValue, "N", out _)) throw new InvalidOperationException("Invalid fixture case.");
                var write = query.Contains("write=1", StringComparer.Ordinal) ? "true" : "false";
                body += """
                    <script>
                    let cookie='0', storage='unavailable';
                    try { cookie=document.cookie.includes('p402probe=1')?'1':'0'; } catch (_) { cookie='denied'; }
                    try {
                    """ + "const key='p402probe-" + caseValue + "'; storage=localStorage.getItem(key)==='1'?'1':'0';"
                        + "if (" + write + ") localStorage.setItem(key,'1');" + """
                    } catch (_) { storage='denied'; }
                    parent.postMessage('p402:cookie='+cookie+',storage='+storage, '*');
                    </script>
                    """;
            }
            return ResourceHandler.FromString(body, mimeType: "text/html");
        }
    }

    // HTTP loopback is intentionally separate from the HTTPS scheme-handler
    // fixture. It measures real socket requests without bypassing TLS checks.
    private sealed class StorageHttpFixture : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentQueue<(bool visible, bool httpOnly, bool lax, bool strict)> embedded = new();
        private readonly ConcurrentBag<Task> clients = new();
        private int accepted;
        private readonly Task loop;
        public string Top { get; }
        public string Third { get; }
        public StorageHttpFixture()
        {
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Top = $"http://127.0.0.1:{port}/";
            Third = $"http://localhost:{port}/";
            loop = Task.Run(Listen);
        }
        public void Reset() { while (embedded.TryDequeue(out _)) { } }
        public (int total, int visible, int httpOnly, int lax, int strict) Counts()
        {
            var snapshot = embedded.ToArray();
            return (snapshot.Length, snapshot.Count(x => x.visible), snapshot.Count(x => x.httpOnly),
                snapshot.Count(x => x.lax), snapshot.Count(x => x.strict));
        }
        private async Task Listen()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    if (Interlocked.Increment(ref accepted) > 128)
                    {
                        client.Dispose();
                        throw new InvalidOperationException("Loopback fixture exceeded its connection limit.");
                    }
                    clients.Add(Serve(client));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
            finally { await Task.WhenAll(clients); }
        }
        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var line = await reader.ReadLineAsync(timeout.Token);
                    if (line is null) return;
                    var parts = line.Split(' ');
                    if (parts.Length < 2) return;
                    var path = parts[1].Split('?')[0];
                    var visible = false;
                    var httpOnly = false;
                    var lax = false;
                    var strict = false;
                    for (var i = 0; i < 100; i++)
                    {
                        var header = await reader.ReadLineAsync(timeout.Token);
                        if (string.IsNullOrEmpty(header)) break;
                        if (header.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
                        {
                            visible = header.Contains("p402net=1", StringComparison.Ordinal);
                            httpOnly = header.Contains("p402net-http=1", StringComparison.Ordinal);
                            lax = header.Contains("p402lax=1", StringComparison.Ordinal);
                            strict = header.Contains("p402strict=1", StringComparison.Ordinal);
                        }
                    }
                    if (path == "/embedded") embedded.Enqueue((visible, httpOnly, lax, strict));
                    var body = path == "/embedded"
                        ? "<!doctype html><script>parent.postMessage('p402http:cookie='+(document.cookie.includes('p402net=1')?'1':'0'), '*');</script>"
                        : "<!doctype html><title>Loopback cookie fixture</title>";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var headers = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\n";
                    if (path == "/seed")
                        headers += "Set-Cookie: p402net=1; Max-Age=3600; Path=/; SameSite=None; Secure\r\n"
                            + "Set-Cookie: p402net-http=1; Max-Age=3600; Path=/; SameSite=None; Secure; HttpOnly\r\n"
                            + "Set-Cookie: p402lax=1; Max-Age=3600; Path=/; SameSite=Lax; Secure\r\n"
                            + "Set-Cookie: p402strict=1; Max-Age=3600; Path=/; SameSite=Strict; Secure\r\n"
                            + "Set-Cookie: p402third=; Max-Age=0; Path=/; SameSite=None; Secure\r\n";
                    if (path == "/embedded")
                        headers += "Set-Cookie: p402third=1; Max-Age=3600; Path=/; SameSite=None; Secure\r\n";
                    var prefix = Encoding.ASCII.GetBytes(headers + "Content-Length: " + bytes.Length + "\r\n\r\n");
                    await stream.WriteAsync(prefix, timeout.Token);
                    await stream.WriteAsync(bytes, timeout.Token);
                }
                // Chromium may open speculative connections without sending a
                // request, or close a connection while the fixture is stopping.
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (SocketException) { }
            }
        }
        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
            try { loop.GetAwaiter().GetResult(); }
            finally { stop.Dispose(); }
        }
    }
}
