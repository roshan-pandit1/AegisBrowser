using AegisBrowser;
using AegisBrowser.Core;
using CefSharp;
using CefSharp.WinForms;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal static partial class Program
{
    private static int checks;
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var root = args.Length == 2 ? Path.GetFullPath(args[1]) : Path.Combine(Path.GetTempPath(), "aegis-filtering-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var initialized = false; var exit = 1;
        try
        {
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(new CefSettings { RootCachePath = root, CachePath = Path.Combine(root, "cache"),
                CommandLineArgsDisabled = true, LogFile = Path.Combine(root, "cef.log") });
            if (!initialized) throw new Exception("CEF initialization failed");
            using var context = Cef.GetGlobalRequestContext();
            if (!context.RegisterSchemeHandlerFactory("https", "aegis-diagnostics.invalid", new DiagnosticsFactory())) throw new Exception("Diagnostics registration failed");
            using var server = new FixtureServer();
            using var host = new Form { Width = 1000, Height = 700 };
            host.Shown += async (_, _) =>
            {
                try { if (args.Length == 2) await RestartProfile(host, root, args[0] == "write");
                    else { await Run(host, server); await SettingsReview(host, server); }
                    Console.WriteLine($"PASS: {checks} Chromium filtering checks"); exit = 0; }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                finally { host.Close(); }
            };
            Application.Run(host);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
        finally { if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); } Console.WriteLine("CEF test profile: " + root); }
        return exit;
    }
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception("FAIL: " + message);
        checks++; Console.WriteLine("PASS: " + message);
    }
    private static async Task Run(Form host, FixtureServer server)
    {
        var config = new PrivacyConfiguration { Mode = PrivacyMode.Custom, UserRules = "/blocked.png" };
        var session = new PrivacySession(config);
        using var web = new ChromiumWebBrowser("about:blank") { LifeSpanHandler = new SafePopups(Console.WriteLine), Dock = DockStyle.Fill };
        web.RequestHandler = new SafeRequests(Console.WriteLine, session.Evaluate,
            (request, decision) => Console.WriteLine($"Decision: mode={session.Configuration.Mode} top={request.TopUrl} url={request.Url} type={request.Type} main={request.MainFrame} third={decision.ThirdParty} blocked={decision.Blocked} rule={decision.Rule}"));
        // Subscribe before handle creation so CEF installs its load event adapter.
        web.FrameLoadEnd += (_, _) => { };
        web.LoadError += (_, e) => Console.WriteLine("Resource error: " + e.ErrorCode);
        host.Controls.Add(web);
        await Wait(() => Task.FromResult(web.IsBrowserInitialized && web.CanExecuteJavascriptInMainFrame), "CEF startup");
        async Task Page()
        {
            server.Clear(); session.ClearHistory();
            var marker = Guid.NewGuid().ToString("N");
            web.Load(server.Origin + "/page?" + marker);
            await Wait(async () =>
            {
                if (!web.CanExecuteJavascriptInMainFrame) return false;
                try { var response = await web.EvaluateScriptAsync("document.title").WaitAsync(TimeSpan.FromSeconds(3)); return response.Success && (string?)response.Result == marker; }
                catch (Exception) { return false; }
            }, "fixture completion");
        }
        await Page();
        Check(server.Hits("/first.png") > 0, "allowed first-party resource reaches server");
        Check(server.Hits("/third.png") > 0, "Custom permits unlisted third party");
        Check(server.Hits("/redirect") > 0, "redirect source requested");
        Check(server.Hits("/blocked.png") == 0, "direct, redirected and iframe blocked resources never reach server");
        Check(session.Snapshot().Requests.Any(r => r.Result == "Blocked" && r.Rule == "/blocked.png"), "inspector reports matching rule");
        session.Apply(config with { Mode = PrivacyMode.Standard, UserRules = "", FilterList = "||localhost^$third-party,image" });
        await Page();
        Check(server.Hits("/third.png") == 0 && server.Hits("/first.png") > 0, "Standard list blocks matching third-party resources");
        session.Apply(config with { Mode = PrivacyMode.Strict, UserRules = "" });
        await Page();
        Check(server.Hits("/third.png") == 0, "Strict blocks third-party request at network boundary");
        Check(server.Hits("/first.png") > 0, "Strict preserves first-party resource");
        session.Apply(config with { Mode = PrivacyMode.Strict, SiteExceptions = ["127.0.0.1"] });
        await Page();
        Check(server.Hits("/third.png") > 0 && server.Hits("/blocked.png") > 0, "site exception restores resources");
        session.Apply(config with { Mode = PrivacyMode.Strict, AllowedDomains = ["localhost"] });
        await Page();
        Check(server.Hits("/third.png") > 0, "explicit resource allow restores third-party content");
        session.Apply(config with { Mode = PrivacyMode.Strict }); session.Paused = true;
        await Page();
        Check(server.Hits("/third.png") > 0 && server.Hits("/blocked.png") > 0, "temporary pause restores blocked content");
        session.Paused = false;
        await Page();
        Check(server.Hits("/third.png") == 0 && server.Hits("/blocked.png") == 0, "resume reapplies blocks");
        session.Apply(config with { Mode = PrivacyMode.Strict, BlockedDomains = ["localhost"] });
        web.Load(server.ThirdOrigin + "/landing");
        await Wait(() => Task.FromResult(server.Hits("/landing") > 0), "main-frame destination");
        Check(server.Hits("/landing") > 0, "main-frame navigation is not content-blocked");
        // Check native settings controls can be constructed/opened on the Windows UI thread.
        using var settings = new PrivacyForm(session, server.Origin);
        settings.Show(host); await Task.Delay(200);
        Check(settings.Visible, "native privacy window opens"); settings.Close();
        await CloseWeb(web);
    }
    private static async Task Wait(Func<Task<bool>> predicate, string name)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await predicate()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(name); await Task.Delay(100); }
    }
}

internal sealed class FixtureServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<string, int> hits = new();
    public string Origin { get; }
    public string ThirdOrigin { get; }
    public FixtureServer()
    {
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Origin = "http://127.0.0.1:" + port; ThirdOrigin = "http://localhost:" + port;
        _ = Accept();
    }
    public int Hits(string path) => hits.GetValueOrDefault(path);
    public void Clear() => hits.Clear();
    private async Task Accept()
    {
        try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); _ = Serve(client); } }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }
    private async Task Serve(TcpClient client)
    {
        using (client)
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var first = await reader.ReadLineAsync(stop.Token);
            if (first is null) return;
            for (var i = 0; i < 100; i++) if (string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) break;
            var url = new Uri(Origin + first.Split(' ')[1]); var path = url.AbsolutePath;
            hits.AddOrUpdate(path, 1, (_, n) => n + 1);
            var header = "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\n";
            string body;
            if (path == "/redirect") { header = "HTTP/1.1 302 Found\r\nLocation: /blocked.png\r\n"; body = ""; }
            else if (path == "/page") body = $$"""
                <!doctype html><iframe src="/frame"></iframe><script>
                function img(url) { return new Promise(resolve => { const i = new Image(); i.onload = i.onerror = resolve; i.src = url; document.body.append(i); }); }
                Promise.all([img('/first.png'),img('/blocked.png'),img('/redirect'),img('{{ThirdOrigin}}/third.png')])
                  .then(() => { window.addEventListener('load', () => document.title = '{{url.Query[1..]}}'); if (document.readyState === 'complete') document.title = '{{url.Query[1..]}}'; });
                </script>
                """;
            else if (path == "/frame") body = "<img src='/blocked.png'>";
            else body = "fixture"; // Image decode failure is fine: onerror still establishes completion.
            var data = Encoding.UTF8.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes(header + "Cache-Control: no-store\r\nConnection: close\r\nContent-Length: " + data.Length + "\r\n\r\n");
            await stream.WriteAsync(headers, stop.Token); await stream.WriteAsync(data, stop.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); }
}
