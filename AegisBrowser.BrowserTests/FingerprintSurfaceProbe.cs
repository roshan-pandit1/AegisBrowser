using AegisBrowser;
using AegisBrowser.Core;
using CefSharp;
using CefSharp.WinForms;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Real, disposable CEF experiment. Measures two browser-native settings
    // without spoofing APIs, touching production profiles or calling public hosts.
    private static int FingerprintSurfaceProbe(string profileRoot, string phase)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var root = Path.GetFullPath(profileRoot);
        Directory.CreateDirectory(root);
        var baselinePath = Path.Combine(root, "baseline-profile");
        var restrictedPath = Path.Combine(root, "restricted-profile");
        if (phase == "fingerprint-write")
        {
            new PrivacySettingsStore(baselinePath).Save(new PrivacyConfiguration());
            new PrivacySettingsStore(restrictedPath).Save(new PrivacyConfiguration
                { DisableWebGl = true, DisableRemoteFonts = true });
        }

        var completed = false;
        var failed = false;
        var initialized = false;
        try
        {
            using (var settings = FingerprintSurfacePolicy.CreateSettings(true, true)
                ?? throw new InvalidOperationException("Missing opt-in browser settings"))
            {
                Check(settings.WebGl == CefState.Disabled && settings.RemoteFonts == CefState.Disabled,
                    "CEF options restrict WebGL and remote fonts");
            }
            using (var onlyWebGl = FingerprintSurfacePolicy.CreateSettings(true, false)
                ?? throw new InvalidOperationException("Missing WebGL settings"))
                Check(onlyWebGl.WebGl == CefState.Disabled && onlyWebGl.RemoteFonts == CefState.Default,
                    "independent WebGL opt-in preserves web-font default");
            using (var onlyFonts = FingerprintSurfacePolicy.CreateSettings(false, true)
                ?? throw new InvalidOperationException("Missing remote-font settings"))
                Check(onlyFonts.WebGl == CefState.Default && onlyFonts.RemoteFonts == CefState.Disabled,
                    "independent remote-font opt-in preserves WebGL default");
            Check(FingerprintSurfacePolicy.CreateSettings(false, false) is null,
                "legacy browser uses unmodified CEF defaults");

            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(new CefSettings
            {
                RootCachePath = root,
                CachePath = Path.Combine(root, "global"),
                CommandLineArgsDisabled = true,
                LogFile = Path.Combine(root, "cef-fingerprint-surfaces.log")
            });
            if (!initialized) throw new InvalidOperationException("CEF initialization failed.");

            using var context = Cef.GetGlobalRequestContext();
            var fixture = new FingerprintSurfaceFixture();
            if (!context.RegisterSchemeHandlerFactory("https", "aegis-diagnostics.invalid", new DiagnosticsFactory())
                || !context.RegisterSchemeHandlerFactory("https", "fp-surface.invalid", fixture))
                throw new InvalidOperationException("Fingerprint fixture origins were not registered.");

            using var host = new Form { Width = 200, Height = 150, ShowInTaskbar = false };
            host.Shown += async (_, _) =>
            {
                try
                {
                    var baseline = new PrivacySettingsStore(baselinePath).Load();
                    var restricted = new PrivacySettingsStore(restrictedPath).Load();
                    Check(!baseline.DisableWebGl && !baseline.DisableRemoteFonts,
                        "baseline profile retains Chromium browser defaults");
                    Check(restricted.DisableWebGl && restricted.DisableRemoteFonts,
                        "restricted profile setting persists across processes");
                    await InspectFingerprintSurface(baselinePath, "baseline", fixture);
                    await InspectFingerprintSurface(restrictedPath, "restricted", fixture);
                    Check(fixture.BaselineFontRequests > 0,
                        "baseline web-font fixture made an actual remote-font request");
                    Check(fixture.RestrictedFontRequests == 0,
                        "CEF remote-font restriction suppressed the controlled request");
                    Console.WriteLine($"P4FP_FONT_REQUESTS: baseline={fixture.BaselineFontRequests} restricted={fixture.RestrictedFontRequests}");
                    Console.WriteLine($"PASS: {checks} fingerprint surface checks ({phase})");
                    completed = true;
                }
                catch (Exception ex) { failed = true; Console.Error.WriteLine(ex); }
                finally { host.Close(); }
            };
            Application.Run(host);
        }
        catch (Exception ex) { failed = true; Console.Error.WriteLine(ex); }
        finally
        {
            if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); }
        }
        if (!failed && completed) Console.WriteLine($"PASS: fingerprint surface CEF shutdown completed ({phase})");
        return !failed && completed ? 0 : 1;
    }

    private static async Task InspectFingerprintSurface(string profilePath, string mode, FingerprintSurfaceFixture fixture)
    {
        var form = new MainForm("fp-surface-test", profilePath);
        try
        {
            form.Show();
            var tabs = form.Controls.OfType<TabControl>().Single();
            await Wait(() => tabs.TabCount == 1, "fingerprint " + mode + " initial tab");
            var browser = tabs.SelectedTab!.Controls.OfType<ChromiumWebBrowser>().Single();
            await Ready(browser);
            await LoadReady(browser, "https://fp-surface.invalid/" + mode);
            var gl = await Js(browser, "(() => { const c = document.createElement('canvas'); const gl = c.getContext('webgl'); gl?.getExtension('WEBGL_lose_context')?.loseContext(); return !!gl; })()");
            Console.WriteLine($"P4FP_WEBGL: mode={mode} contextAvailable={gl}");
            if (mode == "restricted")
                Check(gl == "False", "browser-native WebGL control prevents a WebGL context");
            else
                Check(gl is "True" or "False",
                    "baseline WebGL availability measured without assuming a GPU");

            await Js(browser, "window.fpFont='pending';document.fonts.load('20px AegisFixtureFont','test').then(()=>window.fpFont='loaded',()=>window.fpFont='rejected');'initiated'");
            await WaitForAsync(async () => await Js(browser, "window.fpFont") != "pending",
                mode + " synthetic remote font load settles");
            Console.WriteLine($"P4FP_FONT_STATUS: mode={mode} status={await Js(browser, "window.fpFont")}");
            Check(await Js(browser, "document.querySelector('#fixture')?.textContent") == "Controlled font test",
                "ordinary HTML remains readable in " + mode + " mode");
        }
        finally
        {
            if (!form.IsDisposed)
            {
                form.Close();
                await Wait(() => form.IsDisposed, "fingerprint " + mode + " native form close");
            }
        }
    }

    private sealed class FingerprintSurfaceFixture : ISchemeHandlerFactory
    {
        private int baselineFontRequests;
        private int restrictedFontRequests;
        public int BaselineFontRequests => Volatile.Read(ref baselineFontRequests);
        public int RestrictedFontRequests => Volatile.Read(ref restrictedFontRequests);

        public IResourceHandler Create(IBrowser browser, IFrame frame, string schemeName, IRequest request)
        {
            var path = new Uri(request.Url).AbsolutePath;
            if (path == "/font-baseline.woff2")
            {
                Interlocked.Increment(ref baselineFontRequests);
                return ResourceHandler.FromString("invalid synthetic font data", mimeType: "font/woff2");
            }
            if (path == "/font-restricted.woff2")
            {
                Interlocked.Increment(ref restrictedFontRequests);
                return ResourceHandler.FromString("invalid synthetic font data", mimeType: "font/woff2");
            }
            if (path is not ("/baseline" or "/restricted"))
                return ResourceHandler.FromString("Not found", mimeType: "text/plain");
            var scenario = path[1..];
            var html = "<!doctype html><meta charset='utf-8'><title>Local fingerprint surface fixture</title>"
                + "<style>@font-face{font-family:AegisFixtureFont;src:url('/font-" + scenario
                + ".woff2') format('woff2')}#fixture{font-family:AegisFixtureFont,sans-serif}</style>"
                + "<p id='fixture'>Controlled font test</p>";
            return ResourceHandler.FromString(html, mimeType: "text/html");
        }
    }
}
