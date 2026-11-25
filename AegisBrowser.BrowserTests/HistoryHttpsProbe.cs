using AegisBrowser;
using AegisBrowser.Core;
using CefSharp;
using CefSharp.WinForms;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Uses the existing uniquely generated test CA and its independently
    // verified cleanup. This is permitted only on a disposable hosted runner.
    private static int HistoryHttpsProbe(string profileRoot, string trustDirectory)
    {
        PrivacyCertificates? certificates = null;
        DisposableRunnerTrust? trust = null;
        PrivacyHttpsServer? trusted = null, untrusted = null;
        ProfileLease? lease = null;
        var initialized = false;
        var result = 1;
        try
        {
            DisposableRunnerTrust.RequireHostedRunner();
            var runnerTemp = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP")!);
            var root = Path.GetFullPath(profileRoot);
            if (!root.StartsWith(Path.TrimEndingDirectorySeparator(runnerTemp) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The history fixture profile must be under RUNNER_TEMP.");
            Directory.CreateDirectory(root);
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked test profiles are not supported.");
            lease = new ProfileLease(root);
            certificates = new PrivacyCertificates();
            trust = new DisposableRunnerTrust(certificates.Root, trustDirectory, certificates.Id);
            trusted = new PrivacyHttpsServer(certificates.Server);
            untrusted = new PrivacyHttpsServer(certificates.Untrusted);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(new CefSettings { RootCachePath = root, CachePath = Path.Combine(root, "Cache"),
                CommandLineArgsDisabled = true, LogFile = Path.Combine(root, "chromium.log"), LogSeverity = LogSeverity.Warning });
            if (!initialized) throw new InvalidOperationException("CEF initialization failed.");
            using var context = Cef.GetGlobalRequestContext();
            context.RegisterSchemeHandlerFactory("https", "aegis-diagnostics.invalid", new DiagnosticsFactory());
            using var form = new MainForm("history-https-fixture", root);
            form.Shown += async (_, _) =>
            {
                try
                {
                    var tabs = form.Controls.OfType<TabControl>().Single();
                    await Wait(() => tabs.TabCount == 1, "HTTPS history fixture tab");
                    var browser = tabs.SelectedTab!.Controls.OfType<ChromiumWebBrowser>().Single();
                    await Ready(browser);
                    await LoadReady(browser, trusted!.Third + "history-fixture");
                    await Wait(() => form.HistoryForTests.Snapshot().Count == 1, "trusted HTTPS history visit");
                    Check(form.HistoryForTests.Snapshot().Single().Url.StartsWith("https://", StringComparison.Ordinal),
                        "trusted HTTPS visit is recorded with certificate validation enabled");
                    var failed = new TaskCompletionSource<CefErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
                    EventHandler<LoadErrorEventArgs> error = (_, e) => { if (e.Frame.IsMain) failed.TrySetResult(e.ErrorCode); };
                    browser.LoadError += error;
                    try
                    {
                        browser.Load(untrusted!.Third + "history-untrusted");
                        var code = await failed.Task.WaitAsync(TimeSpan.FromSeconds(25));
                        Check(code == CefErrorCode.CertAuthorityInvalid, "untrusted HTTPS certificate is rejected");
                    }
                    finally { browser.LoadError -= error; }
                    Check(form.HistoryForTests.Snapshot().Count == 1, "rejected HTTPS navigation is not recorded");
                    result = 0;
                }
                catch (Exception ex) { result = 1; Console.Error.WriteLine("History HTTPS fixture failed: " + ex.GetType().Name); }
                finally { form.Close(); }
            };
            Application.Run(form);
        }
        catch (Exception ex) { result = 1; Console.Error.WriteLine("History HTTPS setup failed: " + ex.GetType().Name); }
        finally
        {
            try { if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); } }
            catch (Exception ex) { result = 1; Console.Error.WriteLine("History HTTPS shutdown failed: " + ex.GetType().Name); }
            try { untrusted?.Dispose(); trusted?.Dispose(); }
            catch (Exception ex) { result = 1; Console.Error.WriteLine("History HTTPS server cleanup failed: " + ex.GetType().Name); }
            try { trust?.Dispose(); }
            catch (Exception ex) { result = 1; Console.Error.WriteLine("History HTTPS trust cleanup failed: " + ex.GetType().Name); }
            certificates?.Dispose();
            lease?.Dispose();
        }
        if (result == 0) Console.WriteLine("PASS: trusted and rejected HTTPS history fixture completed, shut down and removed its test root");
        return result;
    }
}
