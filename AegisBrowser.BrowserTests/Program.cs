using AegisBrowser;
using CefSharp;
using CefSharp.WinForms;

internal static class Program
{
    private const string Origin = "https://browser-tests.invalid/";
    private static int checks;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("write" or "read")) return 2;
        ApplicationConfiguration.Initialize();
        var root = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(root);
        var initialized = false;
        try
        {
            initialized = Cef.Initialize(new CefSettings
            {
                RootCachePath = root, CachePath = Path.Combine(root, "global"),
                CommandLineArgsDisabled = true, LogFile = Path.Combine(root, "cef-test.log")
            });
            if (!initialized) throw new Exception("CEF initialization failed");
            using var global = Cef.GetGlobalRequestContext();
            global.RegisterSchemeHandlerFactory("https", "aegis-diagnostics.invalid", new DiagnosticsFactory());
            using var host = new Form { Width = 1000, Height = 700, Text = "Aegis CEF integration tests" };
            host.Shown += async (_, _) =>
            {
                try
                {
                    await Storage(host, root, args[0]);
                    if (args[0] == "write") await TabsAndRenderer();
                    Console.WriteLine($"PASS: {checks} browser integration checks ({args[0]})");
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                finally { host.Close(); }
            };
            Application.Run(host);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (initialized) Cef.Shutdown(); }
        return Environment.ExitCode;
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
        await Wait(() => browser.IsBrowserInitialized && browser.CanExecuteJavascriptInMainFrame && !browser.IsLoading, "browser ready");
        // A completed JS round trip also proves the live renderer is responsive.
        await Js(browser, "document.readyState");
    }

    private static async Task Storage(Form host, string root, string phase)
    {
        using var a = new RequestContext(new RequestContextSettings { CachePath = Path.Combine(root, "profile-a") });
        using var b = new RequestContext(new RequestContextSettings { CachePath = Path.Combine(root, "profile-b") });
        foreach (var context in new[] { a, b })
        {
            context.RegisterSchemeHandlerFactory("https", "browser-tests.invalid", new Fixture());
            context.RegisterSchemeHandlerFactory("https", "provider-tests.invalid", new Fixture());
        }
        using var first = new ChromiumWebBrowser(Origin, requestContext: a) { Dock = DockStyle.Left, Width = 450 };
        using var second = new ChromiumWebBrowser(Origin, requestContext: b) { Dock = DockStyle.Fill };
        first.RequestHandler = new SafeRequests(Console.WriteLine);
        first.PermissionHandler = new OriginPermissions(Console.WriteLine);
        var popupHandler = new SafePopups(Console.WriteLine);
        first.LifeSpanHandler = popupHandler;
        host.Controls.Add(second); host.Controls.Add(first);
        await Ready(first); await Ready(second);
        if (phase == "write")
        {
            Check(await Js(first, "document.cookie + localStorage.getItem('marker')") == "null", "fresh profile A");
            Check(await Js(second, "document.cookie + localStorage.getItem('marker')") == "null", "fresh profile B");
            await Js(first, "document.cookie='marker=A; Max-Age=86400; Secure; SameSite=Lax; Path=/'; localStorage.setItem('marker','A'); 'ok'");
            Check(await Js(second, "document.cookie + localStorage.getItem('marker')") == "null", "actual CEF cookie and localStorage isolation");
            await Js(second, "document.cookie='marker=B; Max-Age=86400; Secure; SameSite=Lax; Path=/'; localStorage.setItem('marker','B'); 'ok'");
            using var same = new ChromiumWebBrowser(Origin, requestContext: a) { Dock = DockStyle.Bottom, Height = 150 };
            host.Controls.Add(same); await Ready(same);
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
        await Wait(() => status.Text.Contains("Renderer stopped"), "renderer termination diagnostic");
        Check(true, "renderer crash reports recoverable per-tab diagnostic");
        damaged.Reload(); await Ready(damaged);
        Check(await Js(damaged, "document.title") == "Aegis · Local diagnostics", "renderer reload recovery");
        tabs.SelectedIndex = 0; Current().Reload(); await Ready(Current());
        Check(await Js(Current(), "document.title") == "Aegis · Local diagnostics", "remaining tab can recover even if Chromium shared its renderer");
        Button("×").PerformClick(); Button("×").PerformClick();
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
