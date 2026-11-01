using AegisBrowser;
using CefSharp;
using CefSharp.WinForms;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Only a disposable research fixture: all endpoints are externally owned loopback listeners.
    private static int ExternalEgressProbe(string profileRoot)
    {
        int Port(string name) => int.TryParse(Environment.GetEnvironmentVariable(name), out var port) && port is > 0 and <= 65535
            ? port : throw new IOException("A controlled fixture port is missing or invalid: " + name);
        var proxyPort = Port("AEGIS_TEST_EXTERNAL_PROXY_PORT");
        var originPort = Port("AEGIS_TEST_EXTERNAL_ORIGIN_PORT");
        var stunPort = Port("AEGIS_TEST_EXTERNAL_STUN_PORT");
        var barrier = Environment.GetEnvironmentVariable("AEGIS_TEST_EXTERNAL_EGRESS_BARRIER");
        if (string.IsNullOrWhiteSpace(barrier) || !Directory.Exists(barrier)) throw new IOException("External egress barrier is missing.");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var root = Path.GetFullPath(profileRoot); Directory.CreateDirectory(root);
        var initialized = false; var result = 0;
        try
        {
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(new CefSettings
            {
                RootCachePath = root, CachePath = Path.Combine(root, "cache"),
                CommandLineArgsDisabled = true, LogFile = Path.Combine(root, "cef-external-egress.log")
            });
            if (!initialized) throw new IOException("CEF initialization failed.");
            using var context = Cef.GetGlobalRequestContext();
            using var host = new Form { Width = 800, Height = 600, Text = "External egress research fixture" };
            host.Shown += async (_, _) =>
            {
                BrowserView? browser = null;
                try
                {
                    var config = new Dictionary<string, object> { ["mode"] = "fixed_servers",
                        ["server"] = "http://127.0.0.1:" + proxyPort, ["bypass_list"] = "<-loopback>" };
                    var accepted = await Cef.UIThreadTaskFactory.StartNew(() => context.SetPreference("proxy", config, out _));
                    if (!accepted) throw new IOException("CEF rejected the test-only proxy preference.");
                    browser = new BrowserView("about:blank") { Dock = DockStyle.Fill };
                    browser.RequestHandler = new SafeRequests(Console.WriteLine);
                    Observe(browser); host.Controls.Add(browser); await Ready(browser);
                    var origin = "http://127.0.0.1:" + originPort;
                    await LoadReady(browser, origin + "/external-target");
                    Check(await Js(browser, "document.title") == "external-proxy", "external proxy fixture returned its controlled response");
                    var js = "(()=>{if(typeof RTCPeerConnection!=='function')return 'unsupported';" +
                        "window.__externalPeer=new RTCPeerConnection({iceServers:[{urls:'stun:127.0.0.1:" + stunPort + "'}]});" +
                        "window.__externalPeer.createDataChannel('test');window.__externalPeer.createOffer().then(o=>window.__externalPeer.setLocalDescription(o)).catch(()=>{});return 'started';})()";
                    var rtc = await Js(browser, js);
                    Console.WriteLine(rtc == "started" ? "OBSERVATION: controlled WebRTC gathering initiated" : "INCONCLUSIVE: WebRTC unavailable");
                    if (rtc == "started") { await Task.Delay(8000); await Js(browser, "window.__externalPeer.close(); 'closed'"); }
                    using (var request = new FileStream(Path.Combine(barrier, "stop-proxy"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    { request.WriteByte(1); request.Flush(true); }
                    await Wait(() => File.Exists(Path.Combine(barrier, "proxy-stopped")), "external proxy stop acknowledgement");
                    var offline = origin + "/external-offline";
                    var failed = new TaskCompletionSource<CefErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
                    EventHandler<LoadErrorEventArgs> onError = (_, e) =>
                    { if (e.Frame.IsMain && e.FailedUrl == offline) failed.TrySetResult(e.ErrorCode); };
                    browser.LoadError += onError;
                    try
                    {
                        browser.Load(offline);
                        Check(await failed.Task.WaitAsync(TimeSpan.FromSeconds(25)) != CefErrorCode.None,
                            "stopped external proxy produces a CEF load error");
                    }
                    finally { browser.LoadError -= onError; }
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
        if (result == 0) Console.WriteLine("PASS: external egress CEF fixture and native shutdown completed");
        return result;
    }
}
