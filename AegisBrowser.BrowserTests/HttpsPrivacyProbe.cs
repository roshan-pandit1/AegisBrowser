using AegisBrowser;
using AegisBrowser.Core;
using CefSharp;
using CefSharp.WinForms;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    private static int HttpsPrivacyPreflight(string unused)
    {
        try
        {
            using var certificates = new PrivacyCertificates();
            using var server = new PrivacyHttpsServer(certificates.Server);
            var valid = ClientHandshake(server.Port, "localhost", certificates.Root).GetAwaiter().GetResult();
            var wrongHost = ClientHandshake(server.Port, "wrong.invalid", certificates.Root).GetAwaiter().GetResult();
            var untrusted = ClientHandshake(server.Port, "localhost", null).GetAwaiter().GetResult();
            Check(valid, "HTTPS fixture accepts its explicitly trusted test root and matching hostname");
            Check(!wrongHost, "HTTPS fixture rejects a mismatched hostname");
            Check(!untrusted, "HTTPS fixture rejects its untrusted root");
            if (!string.Equals(Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"), "github-hosted", StringComparison.OrdinalIgnoreCase))
            {
                var rejected = false;
                try { DisposableRunnerTrust.RequireHostedRunner(); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "test CA installation is rejected outside a hosted CI runner");
            }
            Console.WriteLine("PASS: HTTPS fixture preflight completed without changing the trust store");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task<bool> ClientHandshake(int port, string host, X509Certificate2? root)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var stream = new SslStream(client.GetStream());
            var options = new SslClientAuthenticationOptions { TargetHost = host };
            if (root is not null)
            {
                var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
                policy.CustomTrustStore.Add(root);
                options.CertificateChainPolicy = policy;
            }
            await stream.AuthenticateAsClientAsync(options, timeout.Token);
            var request = Encoding.ASCII.GetBytes("GET /tls-preflight HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(request, timeout.Token);
            using var reader = new StreamReader(stream, Encoding.ASCII);
            return (await reader.ReadLineAsync(timeout.Token)) == "HTTP/1.1 200 OK";
        }
        catch (AuthenticationException) { return false; }
        catch (IOException) { return false; }
    }

    private static async Task WaitForAsync(Func<Task<bool>> predicate, string name)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!await predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException(name);
            await Task.Delay(100);
        }
    }

    private static int HttpsPrivacyProbe(string profileRoot, string trustDirectory, string phase)
    {
        PrivacyCertificates? certificates = null;
        DisposableRunnerTrust? trust = null;
        PrivacyHttpsServer? server = null;
        var initialized = false;
        var result = 1;
        var root = Path.GetFullPath(profileRoot);
        try
        {
            DisposableRunnerTrust.RequireHostedRunner();
            var runnerTemp = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP")!);
            if (!root.StartsWith(Path.TrimEndingDirectorySeparator(runnerTemp) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The disposable profile must be under RUNNER_TEMP.");
            Directory.CreateDirectory(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Linked test profiles are not supported.");
            var portPath = Path.Combine(root, "p403-port.txt");
            var port = phase == "https-privacy-read" ? int.Parse(File.ReadAllText(portPath)) : 0;
            certificates = new PrivacyCertificates();
            Console.WriteLine("P403_STAGE: certificates_created");
            trust = new DisposableRunnerTrust(certificates.Root, trustDirectory, certificates.Id);
            Console.WriteLine("P403_STAGE: trust_ready");
            server = new PrivacyHttpsServer(certificates.Server, port);
            if (phase == "https-privacy-write")
            {
                using var file = new FileStream(portPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var bytes = Encoding.ASCII.GetBytes(server.Port.ToString());
                file.Write(bytes); file.Flush(true);
            }
            Check(ClientHandshake(server.Port, "localhost", certificates.Root).GetAwaiter().GetResult(),
                "test certificate chain and localhost hostname validate before CEF startup");
            Check(!ClientHandshake(server.Port, "wrong.invalid", certificates.Root).GetAwaiter().GetResult(),
                "test certificate rejects a wrong hostname");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(new CefSettings
            {
                RootCachePath = root, CachePath = Path.Combine(root, "cache"),
                CommandLineArgsDisabled = true, LogFile = Path.Combine(root, "cef-https-privacy.log")
            });
            if (!initialized) throw new InvalidOperationException("CEF initialization failed.");
            Console.WriteLine("P403_STAGE: cef_initialized");
            using var context = Cef.GetGlobalRequestContext();
            using var host = new Form { Width = 800, Height = 600 };
            host.Shown += async (_, _) =>
            {
                BrowserView? browser = null;
                try
                {
                    if (phase == "https-privacy-read")
                    {
                        var settings = new PrivacySettingsStore(root).Load();
                        Check(settings.BlockThirdPartyCookies == true, "saved cookie policy survived process restart");
                        Check(ThirdPartyCookiePolicy.ApplyAtStartup(settings.BlockThirdPartyCookies).Succeeded,
                            "production cookie policy applied before first browser creation");
                    }
                    var firstUrl = phase == "https-privacy-read" ? server.TopA + "early-top" : server.Third + "seed";
                    browser = new BrowserView(firstUrl) { Dock = DockStyle.Fill, RequestHandler = new SafeRequests(Console.WriteLine) };
                    Observe(browser); host.Controls.Add(browser);
                    await Ready(browser);
                    if (phase == "https-privacy-read")
                    {
                        await WaitForAsync(async () => await Js(browser, "window.p403early") == "True", "earliest cross-site iframe");
                        var early = server.Take("/early-child");
                        Check(early.Length == 1 && !early[0].Normal && !early[0].HttpOnly,
                            "earliest cross-site HTTPS request after startup omitted the persistent cookies");
                        await LoadReady(browser, server.Third + "inspect");
                        var first = server.Take("/inspect");
                        Check(first.Length == 1 && first[0].Normal && first[0].HttpOnly,
                            "first-party normal and HttpOnly HTTPS cookies survived restart");
                        await HttpsScenario(browser, server, false, "restart-enabled");
                        await HttpsChips(browser, server, "restart-enabled", true);
                        await HttpsStorage(browser, server, "restart-enabled", true);
                    }
                    else
                    {
                        var baseline = await Cef.UIThreadTaskFactory.StartNew(() =>
                        {
                            var ok = context.SetPreference(ThirdPartyCookiePolicy.Preference, 0, out _);
                            return ok && context.GetPreference(ThirdPartyCookiePolicy.Preference) is 0;
                        });
                        Check(baseline, "baseline cookie preference selected");
                        await HttpsScenario(browser, server, true, "baseline");
                        await HttpsChips(browser, server, "baseline", false);
                        await HttpsStorage(browser, server, "baseline", false);
                        var applied = await Cef.UIThreadTaskFactory.StartNew(() => ThirdPartyCookiePolicy.ApplyOnUiThread(context, true));
                        Check(applied.Succeeded, "production cookie preference applied and read back");
                        await HttpsScenario(browser, server, false, "enabled");
                        await HttpsChips(browser, server, "enabled", false);
                        await HttpsStorage(browser, server, "enabled", false);
                        await HttpsStorageAccessNoGesture(browser, server);
                        await RejectFixtureCertificates(host, certificates);
                        new PrivacySettingsStore(root).Save(new PrivacyConfiguration { BlockThirdPartyCookies = true });
                        using var manager = context.GetCookieManager(null);
                        using var flush = new Completion();
                        Check(manager.FlushStore(flush), "HTTPS test cookies flushed for restart");
                        await flush.Done.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    await CloseWeb(browser); host.Controls.Remove(browser); browser = null;
                    Console.WriteLine("PASS: real HTTPS privacy probe completed");
                    result = 0;
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                finally
                {
                    if (browser is { IsDisposed: false, IsBrowserInitialized: true })
                    {
                        try { await CloseWeb(browser); }
                        catch (Exception ex) { Console.Error.WriteLine("HTTPS browser close incomplete: " + ex.Message); }
                    }
                    host.Close();
                }
            };
            Application.Run(host);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
        finally
        {
            try
            {
                if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); }
                if (result == 0) Console.WriteLine("PASS: real HTTPS CEF shutdown completed");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            try { server?.Dispose(); } catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            try { trust?.Dispose(); } catch (Exception ex) { Console.Error.WriteLine(ex); result = 1; }
            certificates?.Dispose();
        }
        return result;
    }

    private static async Task HttpsScenario(BrowserView browser, PrivacyHttpsServer server, bool expectThirdParty, string label)
    {
        Check(SiteClassifier.Default.IsThirdParty(server.Third, server.TopA) == true, "HTTPS fixture uses distinct sites");
        await LoadReady(browser, server.Third + "seed");
        Check(await Js(browser, "isSecureContext && location.protocol==='https:' && document.cookie.includes('p403normal=1')"
            + " && document.cookie.includes('p403lax=1') && document.cookie.includes('p403strict=1')"
            + " && !document.cookie.includes('p403http=1')") == "True",
            "HTTPS first-party Secure/SameSite cookies are present and HttpOnly is hidden from JavaScript");
        server.Take("/embedded");
        await LoadReady(browser, server.TopA);
        await Js(browser, "window.p403done=false;addEventListener('message',e=>{if(e.origin==='"
            + server.Third.TrimEnd('/') + "'&&e.data==='p403:done')window.p403done=true;});"
            + "const f=document.createElement('iframe');f.src='" + server.Third + "embedded';document.body.appendChild(f);'started'");
        await WaitForAsync(async () => await Js(browser, "window.p403done") == "True", "HTTPS cross-site iframe");
        var embedded = server.Take("/embedded");
        Check(embedded.Length == 1, "HTTPS server received the cross-site request");
        await LoadReady(browser, server.Third + "inspect");
        var stored = await Js(browser, "document.cookie.includes('p403embedded=1')") == "True";
        Console.WriteLine($"P403_HTTPS_COOKIE: scenario={label} requests={embedded.Length} normal={embedded[0].Normal} httponly={embedded[0].HttpOnly} lax={embedded[0].Lax} strict={embedded[0].Strict} response_cookie_stored={stored}");
        Check(embedded[0].Normal == expectThirdParty && embedded[0].HttpOnly == expectThirdParty
            && !embedded[0].Lax && !embedded[0].Strict && stored == expectThirdParty,
            "HTTPS server observed expected third-party send and save behavior");
    }

    private static async Task RejectFixtureCertificates(Form host, PrivacyCertificates certificates)
    {
        using var wrong = new PrivacyHttpsServer(certificates.WrongHost);
        using var untrusted = new PrivacyHttpsServer(certificates.Untrusted);
        using var web = new BrowserView("about:blank") { Dock = DockStyle.Fill, RequestHandler = new SafeRequests(Console.WriteLine) };
        Observe(web); host.Controls.Add(web); await Ready(web);
        async Task Reject(string url, CefErrorCode expected)
        {
            var failed = new TaskCompletionSource<CefErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<LoadErrorEventArgs> handler = (_, e) => { if (e.Frame.IsMain && e.FailedUrl == url) failed.TrySetResult(e.ErrorCode); };
            web.LoadError += handler;
            try { web.Load(url); Check(await failed.Task.WaitAsync(TimeSpan.FromSeconds(20)) == expected, "production CEF handler rejects " + expected); }
            finally { web.LoadError -= handler; }
        }
        try
        {
            await Reject(wrong.TopA, CefErrorCode.CertCommonNameInvalid);
            await Reject(untrusted.Third, CefErrorCode.CertAuthorityInvalid);
        }
        finally { await CloseWeb(web); host.Controls.Remove(web); }
    }
}
