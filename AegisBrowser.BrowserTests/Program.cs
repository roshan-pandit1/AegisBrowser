using AegisBrowser;
using CefSharp;
using CefSharp.WinForms;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AegisBrowser.BrowserTests;

internal static class Program
{
    private const string Origin = "https://browser-tests.invalid/";
    private static int checks;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("write" or "read")) return 2;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var root = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(root);
        var initialized = false;
        var result = 0;
        try
        {
            foreach (var dependency in new[] { "CefSharp.BrowserSubprocess.exe", "CefSharp.BrowserSubprocess.runtimeconfig.json", "CefSharp.BrowserSubprocess.Core.dll" })
            {
                var dependencyPath = Path.Combine(AppContext.BaseDirectory, dependency);
                Console.WriteLine(dependency + ": " + File.Exists(dependencyPath));
                if (dependency.EndsWith(".json") && File.Exists(dependencyPath)) Console.WriteLine(File.ReadAllText(dependencyPath));
            }
            initialized = Cef.Initialize(new CefSettings
            {
                RootCachePath = root, CachePath = Path.Combine(root, "global"),
                CommandLineArgsDisabled = true, LogFile = Path.Combine(root, "cef-test.log"), LogSeverity = LogSeverity.Info
            });
            if (!initialized) throw new Exception("CEF initialization failed");
            using var global = Cef.GetGlobalRequestContext();
            global.RegisterSchemeHandlerFactory("https", "aegis-diagnostics.invalid", new DiagnosticsFactory());
            using var host = new Form { Width = 1000, Height = 700, Text = "Aegis CEF integration tests" };
            host.Shown += async (_, _) =>
            {
                try
                {
                    if (args[0] == "write") await TabsAndRenderer();
                    await Storage(host, root, args[0]);
                    if (args[0] == "write") await RejectUntrustedTls(host);
                    Console.WriteLine($"PASS: {checks} browser integration checks ({args[0]})");
                }
                catch (Exception ex)
                {
                    result = 1;
                    Console.Error.WriteLine(ex);
                    try
                    {
                        var log = Path.Combine(root, "cef-test.log");
                        if (File.Exists(log)) Console.Error.WriteLine(File.ReadAllText(log));
                    }
                    catch (IOException logError) { Console.Error.WriteLine("Log unavailable: " + logError.Message); }
                }
                finally { host.Close(); }
            };
            Application.Run(host);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (initialized) Cef.Shutdown(); }
        return result;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        checks++;
        Console.WriteLine("PASS: " + name);
    }

    private static async Task Wait(Func<bool> predicate, string name)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(name);
            await Task.Delay(100);
        }
    }

    private static async Task<string> Js(ChromiumWebBrowser browser, string script)
    {
        var response = await browser.EvaluateScriptAsync(script).WaitAsync(TimeSpan.FromSeconds(10));
        if (!response.Success) throw new Exception("JavaScript failed: " + response.Message);
        return response.Result?.ToString() ?? "";
    }

    private static async Task Ready(ChromiumWebBrowser browser)
    {
        try
        {
            await Wait(() => browser.IsBrowserInitialized && browser.CanExecuteJavascriptInMainFrame && !browser.IsLoading,
                "browser ready (" + browser.Address + ")");
        }
        catch
        {
            Console.Error.WriteLine($"Readiness: initialized={browser.IsBrowserInitialized}, javascript={browser.CanExecuteJavascriptInMainFrame}, loading={browser.IsLoading}, address={browser.Address}");
            throw;
        }
        // A completed JS round trip also proves the live renderer is responsive.
        await Js(browser, "document.readyState");
    }

    private static async Task ReloadReady(ChromiumWebBrowser browser)
    {
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<FrameLoadEndEventArgs> handler = (_, e) => { if (e.Frame.IsMain) loaded.TrySetResult(true); };
        browser.FrameLoadEnd += handler;
        try
        {
            browser.Reload();
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(25));
            await Ready(browser);
        }
        finally { browser.FrameLoadEnd -= handler; }
    }

    private static void Observe(ChromiumWebBrowser web)
    {
        // Subscribe before creating the handle, ensuring CEF's load handler is installed.
        web.FrameLoadEnd += (_, e) => { if (e.Frame.IsMain) Console.WriteLine($"Loaded {e.Url}: HTTP {e.HttpStatusCode}"); };
        web.LoadError += (_, e) => Console.WriteLine($"Load error {e.ErrorCode}: {e.FailedUrl}");
        web.IsBrowserInitializedChanged += (_, _) => Console.WriteLine($"Initialized: {web.IsBrowserInitialized}");
    }

    private static async Task Storage(Form host, string root, string phase)
    {
        CefSharp.Handler.RequestContextHandler Handler() => new CefSharp.Handler.RequestContextHandler().OnInitialize(context =>
        {
            Console.WriteLine("Context initialized: " + context.CachePath);
            if (!context.RegisterSchemeHandlerFactory("https", "browser-tests.invalid", new Fixture())
                || !context.RegisterSchemeHandlerFactory("https", "provider-tests.invalid", new Fixture()))
                throw new InvalidOperationException("Fixture scheme registration failed");
        });
        using var a = new RequestContext(new RequestContextSettings { CachePath = Path.Combine(root, "profile-a") }, Handler());
        using var b = new RequestContext(new RequestContextSettings { CachePath = Path.Combine(root, "profile-b") }, Handler());
        using var first = new ChromiumWebBrowser(Origin, requestContext: a) { LifeSpanHandler = new CefSharp.Handler.LifeSpanHandler(), Dock = DockStyle.Left, Width = 450 };
        using var second = new ChromiumWebBrowser(Origin, requestContext: b) { LifeSpanHandler = new CefSharp.Handler.LifeSpanHandler(), Dock = DockStyle.Fill };
        first.RequestHandler = new SafeRequests(Console.WriteLine);
        first.PermissionHandler = new OriginPermissions(Console.WriteLine);
        var popupHandler = new SafePopups(Console.WriteLine);
        first.LifeSpanHandler = popupHandler;
        first.Disposed += (_, _) => popupHandler.ClosePopups();
        foreach (var web in new[] { first, second }) Observe(web);
        host.Controls.Add(second); host.Controls.Add(first);
        await Ready(first); await Ready(second);
        if (phase == "write")
        {
            Check(await Js(first, "document.cookie + localStorage.getItem('marker')") == "null", "fresh profile A");
            Check(await Js(second, "document.cookie + localStorage.getItem('marker')") == "null", "fresh profile B");
            await Js(first, "document.cookie='marker=A; Max-Age=86400; Secure; SameSite=Lax; Path=/'; localStorage.setItem('marker','A'); 'ok'");
            Check(await Js(second, "document.cookie + localStorage.getItem('marker')") == "null", "actual CEF cookie and localStorage isolation");
            await Js(second, "document.cookie='marker=B; Max-Age=86400; Secure; SameSite=Lax; Path=/'; localStorage.setItem('marker','B'); 'ok'");
            using var same = new ChromiumWebBrowser(Origin, requestContext: a) { LifeSpanHandler = new CefSharp.Handler.LifeSpanHandler(), Dock = DockStyle.Bottom, Height = 150 };
            Observe(same); host.Controls.Add(same); await Ready(same);
            Check(await Js(same, "document.cookie + '|' + localStorage.getItem('marker')") == "marker=A|A", "same-profile browser shares storage");
            // User activation is injected through CDP only in this test process. Production has no remote-debugging port.
            await Js(first, "window.oauthDone=false; window.addEventListener('message', e => { if(e.origin===location.origin && e.data==='oauth-ok') window.oauthDone=true; }); 'ok'");
            await Cef.UIThreadTaskFactory.StartNew(() => first.GetBrowserHost().ExecuteDevToolsMethod(0, "Runtime.evaluate",
                new Dictionary<string, object> { ["expression"] = "window.open('https://provider-tests.invalid/authorize','oauth')", ["userGesture"] = true }));
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (await Js(first, "window.oauthDone") != "True")
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("OAuth opener/postMessage round trip");
                await Task.Delay(100);
            }
            Check(true, "native popup preserves opener across provider redirect and postMessage");
            Check(await Js(first, "window.open('about:blank') === null") == "True", "automatic popup denied");
            using var cookiesA = a.GetCookieManager(null);
            using var cookiesB = b.GetCookieManager(null);
            using var flushA = new Completion(); using var flushB = new Completion();
            if (!cookiesA.FlushStore(flushA) || !cookiesB.FlushStore(flushB)) throw new Exception("FlushStore refused");
            await Task.WhenAll(flushA.Done.Task, flushB.Done.Task).WaitAsync(TimeSpan.FromSeconds(10));
            popupHandler.ClosePopups();
        }
        Check(await Js(first, "document.cookie + '|' + localStorage.getItem('marker')") == "marker=A|A", phase + " profile A persistence");
        Check(await Js(second, "document.cookie + '|' + localStorage.getItem('marker')") == "marker=B|B", phase + " profile B persistence");
        host.Controls.Remove(first); host.Controls.Remove(second);
    }

    private static async Task RejectUntrustedTls(Form host)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var connection = await server.AcceptTcpClientAsync(stop.Token);
                    using var stream = new SslStream(connection.GetStream());
                    try
                    {
                        await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, stop.Token);
                        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 9\r\nConnection: close\r\n\r\nuntrusted"), stop.Token);
                    }
                    catch (AuthenticationException) { }
                    catch (IOException) { }
                }
            }
            catch (OperationCanceledException) { }
        });
        using var web = new ChromiumWebBrowser("about:blank") { LifeSpanHandler = new CefSharp.Handler.LifeSpanHandler(), Dock = DockStyle.Fill };
        var failure = new TaskCompletionSource<CefErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        web.RequestHandler = new SafeRequests(Console.WriteLine);
        Observe(web);
        web.LoadError += (_, e) => { if (e.Frame.IsMain && e.FailedUrl.StartsWith("https://localhost:")) failure.TrySetResult(e.ErrorCode); };
        host.Controls.Add(web);
        try
        {
            await Ready(web);
            web.Load($"https://localhost:{port}/");
            var code = await failure.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Check(code.ToString().StartsWith("Cert", StringComparison.Ordinal), "untrusted TLS certificate rejected by production handler: " + code);
        }
        finally { stop.Cancel(); await serving; host.Controls.Remove(web); }
    }

    private static async Task TabsAndRenderer()
    {
        using var form = new MainForm("integration");
        form.Show();
        var tabs = form.Controls.OfType<TabControl>().Single();
        var toolbar = form.Controls.OfType<TableLayoutPanel>().Single();
        var status = form.Controls.OfType<StatusStrip>().Single().Items.OfType<ToolStripStatusLabel>().Last();
        Button Button(string label) => toolbar.Controls.OfType<Button>().Single(b => b.Text == label);
        ChromiumWebBrowser Current() => tabs.SelectedTab!.Controls.OfType<ChromiumWebBrowser>().Single();
        await Wait(() => tabs.TabCount == 1, "initial tab"); await Ready(Current());
        Check(await Js(Current(), "document.querySelector('#result').textContent.includes('userAgent')") == "True", "embedded diagnostics renders in CEF");
        for (var i = 0; i < 5; i++) { Button("+").PerformClick(); Button("×").PerformClick(); }
        Check(tabs.TabCount == 1, "rapid tab creation and disposal during loading");
        Button("+").PerformClick(); await Ready(Current());
        var damaged = Current();
        await Cef.UIThreadTaskFactory.StartNew(() => damaged.GetBrowserHost().ExecuteDevToolsMethod(0, "Page.crash", new Dictionary<string, object>()));
        await Wait(() => status.Text?.Contains("Renderer stopped") == true, "renderer termination diagnostic");
        Check(true, "renderer crash reports recoverable per-tab diagnostic");
        await ReloadReady(damaged);
        Check(await Js(damaged, "document.title") == "Aegis · Local diagnostics", "renderer reload recovery");
        tabs.SelectedIndex = 0; await ReloadReady(Current());
        Check(await Js(Current(), "document.title") == "Aegis · Local diagnostics", "remaining tab can recover even if Chromium shared its renderer");
        var owner = Current();
        var ownerPopups = (SafePopups)owner.LifeSpanHandler;
        await Cef.UIThreadTaskFactory.StartNew(() => owner.GetBrowserHost().ExecuteDevToolsMethod(0, "Runtime.evaluate",
            new Dictionary<string, object> { ["expression"] = "window.blankPopup=window.open('about:blank','blank'); window.blankOpener=!!window.blankPopup && window.blankPopup.opener===window;", ["userGesture"] = true }));
        await Wait(() => ownerPopups.PopupCount == 1, "blank popup created");
        Check(await Js(owner, "window.blankOpener") == "True", "user-initiated about:blank preserves opener");
        Button("×").PerformClick();
        await Wait(() => ownerPopups.PopupCount == 0, "owner tab closes its native popup");
        Check(true, "popup lifecycle ends with owner tab");
        Button("×").PerformClick();
        await Ready(Current()); Check(tabs.TabCount == 1, "last-tab replacement");
        form.Close();
    }

    private sealed class Completion : ICompletionCallback
    {
        public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsDisposed { get; private set; }
        public void OnComplete() => Done.TrySetResult(true);
        public void Dispose() => IsDisposed = true;
    }

    private sealed class Fixture : ISchemeHandlerFactory
    {
        public IResourceHandler Create(IBrowser browser, IFrame frame, string schemeName, IRequest request)
        {
            var uri = new Uri(request.Url);
            var script = uri.Host == "provider-tests.invalid"
                ? "location.replace('https://browser-tests.invalid/callback');"
                : uri.AbsolutePath == "/callback" ? "window.opener.postMessage('oauth-ok','https://browser-tests.invalid');window.close();" : "";
            return ResourceHandler.FromString("<!doctype html><title>CEF storage fixture</title><script>" + script + "</script><p>Local integration fixture</p>", mimeType: "text/html");
        }
    }
}
