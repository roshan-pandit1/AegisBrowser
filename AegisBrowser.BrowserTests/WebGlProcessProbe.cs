using AegisBrowser;
using CefSharp;
using CefSharp.WinForms;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Research-only: a separate disposable process per mode, with an actual
    // Chromium switch requested before Cef.Initialize and no API overrides.
    private static int WebGlProcessProbe(string profileRoot, string phase)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var root = Path.GetFullPath(profileRoot);
        Directory.CreateDirectory(root);
        var restricted = phase != "webgl-process-baseline";
        var mode = phase == "webgl-process-3d" ? "3d" : restricted ? "restricted" : "baseline";
        var candidateSwitch = phase == "webgl-process-3d" ? "disable-3d-apis" : "disable-webgl";
        var initialized = false;
        var completed = false;
        var failed = false;
        try
        {
            var settings = new CefSettings
            {
                RootCachePath = root,
                CachePath = Path.Combine(root, "global"),
                CommandLineArgsDisabled = true,
                LogFile = Path.Combine(root, "cef-webgl-process.log")
            };
            if (restricted) settings.CefCommandLineArgs.Add(candidateSwitch, "");
            Console.WriteLine("P4FP04_SWITCH_REQUESTED: " + (restricted ? candidateSwitch : "none"));
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(settings);
            if (!initialized) throw new InvalidOperationException("CEF initialization failed.");
            using var context = Cef.GetGlobalRequestContext();
            var fixture = new FingerprintSurfaceFixture();
            if (!context.RegisterSchemeHandlerFactory("https", "fp-surface.invalid", fixture)
                || !context.RegisterSchemeHandlerFactory("https", "fp-other.invalid", fixture))
                throw new InvalidOperationException("Controlled origins could not be registered.");
            using var host = new Form { Width = 800, Height = 600, ShowInTaskbar = false };
            host.Shown += async (_, _) =>
            {
                BrowserView? browser = null;
                var safeToClose = true;
                try
                {
                    browser = new BrowserView("about:blank") { Dock = DockStyle.Fill };
                    Observe(browser);
                    host.Controls.Add(browser);
                    await Ready(browser);
                    await LoadReady(browser, "https://fp-surface.invalid/baseline");
                    var results = await MeasureFingerprintContexts(browser, "process-" + mode);
                    var functionalWhenRestricted = new List<string>();
                    var incomplete = false;
                    foreach (var contextName in new[] { "main", "same", "cross", "worker" })
                    {
                        foreach (var surface in new[] { "webgl", "webgl2", "offscreenWebgl", "offscreenWebgl2" })
                        {
                            var state = results[contextName].TryGetValue(surface, out var observed) ? observed : "INCONCLUSIVE";
                            Console.WriteLine($"P4FP04_PROCESS_GL: mode={mode} context={contextName} surface={surface} state={state}");
                            if (restricted && state == "FEATURE_FUNCTIONAL") functionalWhenRestricted.Add(contextName + "/" + surface);
                            if (state is "UNSUPPORTED" or "INCONCLUSIVE") incomplete = true;
                        }
                    }
                    if (restricted)
                        Console.WriteLine("P4FP04_SWITCH_RESULT: " + (functionalWhenRestricted.Count != 0 ? "INEFFECTIVE" : incomplete ? "INCONCLUSIVE" : "NO_CONTEXT_OBSERVED"));
                    Console.WriteLine("P4FP04_BASELINE_NOTE: Missing baseline support or inconclusive results do not prove restriction.");
                    completed = true;
                }
                catch (Exception ex) { failed = true; Console.Error.WriteLine(ex); }
                finally
                {
                    if (browser is { IsBrowserInitialized: true, IsDisposed: false })
                    {
                        try { await CloseWeb(browser); host.Controls.Remove(browser); }
                        catch (Exception ex) { safeToClose = false; failed = true; Console.Error.WriteLine("CEF browser close incomplete: " + ex.Message); }
                    }
                    else if (browser is { IsDisposed: false }) { safeToClose = false; failed = true; }
                    if (safeToClose) host.Close();
                    else Console.Error.WriteLine("P4FP04_CEF_SHUTDOWN: INCOMPLETE; live owner retained for bounded supervisor timeout");
                }
            };
            Application.Run(host);
        }
        catch (Exception ex) { failed = true; Console.Error.WriteLine(ex); }
        finally { if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); } }
        if (!failed && completed) Console.WriteLine("PASS: WebGL process experiment completed and CEF shut down");
        return !failed && completed ? 0 : 1;
    }
}
