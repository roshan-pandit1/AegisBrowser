using AegisBrowser;
using CefSharp;
using CefSharp.WinForms;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Test process only. It never reads a production profile or configures a proxy.
    private static int AppContainerCefProbe(string profileRoot, bool waitForObserver = false)
    {
        var root = Path.GetFullPath(profileRoot);
        void Mark(string name) => File.WriteAllText(Path.Combine(root, name), "observed");
        var initialized = false;
        var result = 0;
        try
        {
            Mark("started");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(new CefSettings
            {
                RootCachePath = root,
                CachePath = Path.Combine(root, "cache"),
                CommandLineArgsDisabled = true,
                LogFile = Path.Combine(root, "cef-appcontainer.log")
            });
            if (!initialized) throw new IOException("CEF initialization returned false.");
            Mark("cef-initialized");
            using var context = Cef.GetGlobalRequestContext();
            if (!context.RegisterSchemeHandlerFactory("https", "aegis-diagnostics.invalid", new DiagnosticsFactory()))
                throw new IOException("Synthetic diagnostics handler registration failed.");
            using var host = new Form { Width = 800, Height = 600, Text = "AppContainer CEF research" };
            host.Shown += async (_, _) =>
            {
                BrowserView? browser = null;
                try
                {
                    browser = new BrowserView("about:blank") { Dock = DockStyle.Fill };
                    Observe(browser); host.Controls.Add(browser);
                    await Ready(browser);
                    Mark("browser-ready");
                    await LoadReady(browser, AegisBrowser.Core.BrowserPolicy.DiagnosticsUrl);
                    if (!(await Js(browser, "document.title")).Contains("diagnostics", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The controlled diagnostic page did not load.");
                    Mark("navigated");
                    if (waitForObserver)
                    {
                        Mark("observer-ready");
                        var deadline = DateTime.UtcNow.AddSeconds(60);
                        while (!File.Exists(Path.Combine(root, "cross-check")) && !File.Exists(Path.Combine(root, "observer-release")))
                        {
                            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Independent cross-profile check was not requested.");
                            await Task.Delay(100);
                        }
                        if (File.Exists(Path.Combine(root, "cross-check")))
                        {
                            var ownRole = Directory.GetParent(root)?.Name;
                            if (ownRole is not ("A" or "B")) throw new IOException("Unexpected synthetic profile role.");
                            var pairRoot = Directory.GetParent(Directory.GetParent(root)!.FullName)!.FullName;
                            if (File.ReadAllText(Path.Combine(root, "isolation-control")) != ownRole)
                                throw new IOException("Own synthetic profile control is unreadable or mismatched.");
                            Mark("own-control-read");
                            try
                            {
                                var other = Path.Combine(pairRoot, ownRole == "A" ? "B" : "A", "profile", "isolation-control");
                                _ = File.ReadAllText(other);
                                Mark("cross-access-allowed");
                            }
                            catch (UnauthorizedAccessException) { Mark("cross-access-denied"); }
                            catch (IOException) { Mark("cross-access-inconclusive"); }
                        }
                        while (!File.Exists(Path.Combine(root, "observer-release")))
                        {
                            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Independent observer did not release the synthetic fixture.");
                            await Task.Delay(100);
                        }
                    }
                }
                catch (Exception ex)
                {
                    result = 1;
                    try { File.WriteAllText(Path.Combine(root, "failure.txt"), ex.GetType().Name + ": " + ex.Message); } catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException) { }
                }
                finally
                {
                    if (browser is not null && browser.IsBrowserInitialized)
                    {
                        try { await CloseWeb(browser); }
                        catch (Exception ex) { result = 1; try { File.WriteAllText(Path.Combine(root, "failure.txt"), ex.GetType().Name + ": " + ex.Message); } catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException) { } }
                    }
                    host.Close();
                }
            };
            Application.Run(host);
        }
        catch (Exception ex)
        {
            result = 1;
            try { File.WriteAllText(Path.Combine(root, "failure.txt"), ex.GetType().Name + ": " + ex.Message); } catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException) { }
        }
        finally
        {
            if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); }
        }
        if (result == 0) Mark("shutdown");
        return result;
    }
}
