using AegisBrowser;
using AegisBrowser.Core;
using CefSharp;
using CefSharp.WinForms;
using System.Net;
using System.Net.Sockets;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    private static int HistoryPrivacyProbe(string root, string phase, string marker, string? other = null, string? readyName = null, string? releaseName = null)
    {
        if (marker.Length != 32 || !marker.All(Uri.IsHexDigit)) return 2;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Directory.CreateDirectory(root);
        using var lease = new ProfileLease(root);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancel = new CancellationTokenSource();
        var server = ServeHistoryFixture(listener, cancel.Token);
        var initialized = false;
        var result = 0;
        try
        {
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(new CefSettings { RootCachePath = root, CachePath = Path.Combine(root, "Cache"),
                CommandLineArgsDisabled = true, LogFile = Path.Combine(root, "chromium.log"), LogSeverity = LogSeverity.Warning });
            if (!initialized) throw new InvalidOperationException("CEF initialization failed.");
            using var global = Cef.GetGlobalRequestContext();
            global.RegisterSchemeHandlerFactory("https", "aegis-diagnostics.invalid", new DiagnosticsFactory());
            using var form = new MainForm("history-privacy-fixture", root);
            form.Shown += async (_, _) =>
            {
                try
                {
                    var tabs = form.Controls.OfType<TabControl>().Single();
                    await Wait(() => tabs.TabCount == 1 && tabs.SelectedTab!.Controls.OfType<ChromiumWebBrowser>().Single().IsBrowserInitialized, "privacy fixture ready");
                    Check(form.HistoryForTests.Snapshot().Count == 0, "fresh process history begins empty");
                    if (phase is "history-privacy-write" or "history-privacy-pair")
                    {
                        var browser = tabs.SelectedTab!.Controls.OfType<ChromiumWebBrowser>().Single();
                        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        EventHandler<FrameLoadEndEventArgs> handler = (_, e) =>
                        { if (e.Frame.IsMain && e.HttpStatusCode == 200) loaded.TrySetResult(); };
                        browser.FrameLoadEnd += handler;
                        try
                        {
                            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                            browser.Load("http://127.0.0.1:" + port + "/canary-" + marker + "?search=Query" + marker);
                            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(25));
                        }
                        finally { browser.FrameLoadEnd -= handler; }
                        await Wait(() => form.HistoryForTests.Snapshot().Any(e => e.Title == "HistoryTitle" + marker), "fixture title observed");
                        var entry = form.HistoryForTests.Snapshot().Single();
                        Check(!entry.Url.Contains('?') && entry.Url.Contains(marker), "query removed from in-memory visit");
                        Check(entry.Title == "HistoryTitle" + marker, "controlled title appears in memory");
                        if (phase == "history-privacy-pair")
                        {
                            if (other is null || other.Length != 32 || !other.All(Uri.IsHexDigit) || readyName is null || releaseName is null)
                                throw new InvalidOperationException("Invalid pair fixture inputs.");
                            using var readySignal = EventWaitHandle.OpenExisting(readyName);
                            using var releaseSignal = EventWaitHandle.OpenExisting(releaseName);
                            readySignal.Set();
                            if (!await Task.Run(() => releaseSignal.WaitOne(TimeSpan.FromSeconds(30))))
                                throw new TimeoutException("Profile-pair synchronization timed out.");
                            Check(form.HistoryForTests.Snapshot().All(e => !e.Url.Contains(other) && !e.Title.Contains(other)), "simultaneous profile history remains isolated");
                        }
                    }
                }
                catch (Exception ex) { result = 1; Console.Error.WriteLine("History privacy fixture failed: " + ex.GetType().Name); }
                finally { form.Close(); }
            };
            Application.Run(form);
        }
        finally
        {
            if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); }
            cancel.Cancel(); listener.Stop();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
        if (result == 0) Console.WriteLine("PASS: history privacy fixture shutdown " + phase);
        return result;
    }
}
