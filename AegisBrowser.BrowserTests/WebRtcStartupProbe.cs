using AegisBrowser;
using AegisBrowser.Core;
using CefSharp;
using CefSharp.WinForms;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // A separate disposable process/profile per case. This measures one loopback
    // STUN listener, not TCP, IPv6, DNS, public traffic, or containment.
    private static int WebRtcStartupProbe(string profileRoot, bool restrict)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var root = Path.GetFullPath(profileRoot);
        Directory.CreateDirectory(root);
        var store = new PrivacySettingsStore(root);
        store.Save(new PrivacyConfiguration { RestrictWebRtcNonProxiedUdp = restrict ? true : null });
        var requested = store.Load().RestrictWebRtcNonProxiedUdp;
        var initialized = false;
        var result = 0;
        try
        {
            Check(requested == (restrict ? (bool?)true : null), "disposable WebRTC profile setting round trip");
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(new CefSettings
            {
                RootCachePath = root, CachePath = Path.Combine(root, "cache"),
                CommandLineArgsDisabled = true, LogFile = Path.Combine(root, "cef-webrtc-probe.log")
            });
            if (!initialized) throw new IOException("CEF initialization failed.");
            var policy = WebRtcPrivacyPolicy.ApplyAtStartup(requested);
            Check(policy.Succeeded, "WebRTC startup policy evaluated before browser creation");
            Console.WriteLine("WEBRTC_STARTUP_POLICY: " + policy.Status);
            using var origin = new LocalHttpFixture("webrtc");
            using var host = new Form { Width = 800, Height = 600, Text = "Local WebRTC observation" };
            host.Shown += async (_, _) =>
            {
                BrowserView? browser = null;
                try
                {
                    browser = new BrowserView("about:blank") { Dock = DockStyle.Fill };
                    Observe(browser);
                    host.Controls.Add(browser);
                    await Ready(browser);
                    await LoadReady(browser, "http://127.0.0.1:" + origin.Port + "/webrtc");
                    var observation = await ObserveWebRtcUdp(browser);
                    Console.WriteLine("WEBRTC_STARTUP_STUN: " + observation.ToString().ToUpperInvariant());
                    if (restrict && observation == WebRtcUdpObservation.Stun)
                        throw new InvalidOperationException("The restricted startup probe sent a STUN packet to the local listener.");
                    Console.WriteLine("INCONCLUSIVE: a single loopback listener is not a network-containment test.");
                }
                catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
                finally
                {
                    if (browser is not null && browser.IsBrowserInitialized)
                        try { await CloseWeb(browser); }
                        catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
                    host.Close();
                }
            };
            Application.Run(host);
        }
        catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
        finally { if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); } }
        if (result == 0) Console.WriteLine("PASS: WebRTC startup observation shutdown completed");
        return result;
    }
}
