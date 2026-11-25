using AegisBrowser;
using AegisBrowser.Core;
using CefSharp;
using CefSharp.WinForms;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    private static int HistoryUiProbe(string root)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Directory.CreateDirectory(root);
        var initialized = false;
        var outcome = 0;
        try
        {
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(new CefSettings { RootCachePath = root, CachePath = Path.Combine(root, "global"),
                CommandLineArgsDisabled = true, LogFile = Path.Combine(root, "cef-test.log"), LogSeverity = LogSeverity.Warning });
            if (!initialized) throw new InvalidOperationException("CEF initialization failed.");
            using var global = Cef.GetGlobalRequestContext();
            global.RegisterSchemeHandlerFactory("https", "aegis-diagnostics.invalid", new DiagnosticsFactory());
            using var host = new Form();
            host.Shown += async (_, _) =>
            {
                try
                {
                    await HistoryUi(root);
                    using (var reopened = new ProfileLease(Path.Combine(root, "history-profile"))) Check(true, "history fixture lease released");
                    Console.WriteLine($"PASS: {checks} native history checks");
                }
                catch (Exception ex) { outcome = 1; Console.Error.WriteLine(ex); }
                finally { host.Close(); }
            };
            Application.Run(host);
        }
        finally { if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); } }
        if (outcome == 0) Console.WriteLine("PASS: CEF shutdown completed (history)");
        return outcome;
    }

    private static async Task HistoryUi(string root)
    {
        var profile = Path.Combine(root, "history-profile");
        Directory.CreateDirectory(profile);
        using var lease = new ProfileLease(profile);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancel = new CancellationTokenSource();
        var slowRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pausedRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var server = ServeHistoryFixture(listener, cancel.Token, slowRequest, pausedRequest, releasePaused.Task, abortRequest,
            () => Interlocked.Increment(ref requests));
        var origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        using var form = new MainForm("history-fixture", profile);
        try
        {
            form.Show();
            var exerciseKeyboard = Environment.GetEnvironmentVariable("AEGIS_HISTORY_LIFECYCLE_ONLY") != "1";
            var tabs = form.Controls.OfType<TabControl>().Single();
            await Wait(() => tabs.TabCount == 1, "history fixture tab");
            var web = tabs.SelectedTab!.Controls.OfType<ChromiumWebBrowser>().Single();
            await Ready(web);
            Check(form.HistoryForTests.Snapshot().Count == 0, "diagnostics excluded from session history");
            await LoadReady(web, origin + "/page");
            await Wait(() => form.HistoryForTests.Snapshot().Count == 1, "first history visit");
            Check(form.HistoryForTests.Snapshot().Single().Url.EndsWith("/page", StringComparison.Ordinal), "successful page recorded in session memory");
            await Wait(() => form.HistoryForTests.Snapshot().Single().Title == "Fixture title", "history title attribution");
            Check(true, "history title follows the committed document");
            var redirected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<FrameLoadEndEventArgs> redirectEnd = (_, e) =>
            { if (e.Frame.IsMain && e.Url == origin + "/page" && e.HttpStatusCode == 200) redirected.TrySetResult(); };
            web.FrameLoadEnd += redirectEnd;
            try
            {
                web.Load(origin + "/redirect");
                await redirected.Task.WaitAsync(TimeSpan.FromSeconds(25));
                await Ready(web);
            }
            finally { web.FrameLoadEnd -= redirectEnd; }
            await Wait(() => form.HistoryForTests.Snapshot().Count == 2, "redirected history visit");
            Check(form.HistoryForTests.Snapshot().First().Url.EndsWith("/page", StringComparison.Ordinal), "redirect records only its final address");
            var requestsBeforeOpening = Volatile.Read(ref requests);
            form.Activate();
            var address = (TextBox)form.Controls.Find("BrowserAddress", true).Single();
            address.Focus();
            if (!exerciseKeyboard) { }
            else if (HistoryForegroundWindow() == form.Handle && address.Focused)
            {
                SendKeys.SendWait("^h");
                await Wait(() => form.OwnedForms.OfType<HistoryForm>().Any(), "toolbar Ctrl+H");
                Check(true, "Ctrl+H opens history from toolbar focus");
                form.OwnedForms.OfType<HistoryForm>().Single().Close();
                form.Activate(); web.Focus();
                if (HistoryForegroundWindow() == form.Handle && web.ContainsFocus)
                {
                    SendKeys.SendWait("^l");
                    await Wait(() => address.Focused, "existing Ctrl+L shortcut");
                    Check(true, "Ctrl+L still focuses the address field");
                    SendKeys.SendWait("^t");
                    await Wait(() => tabs.TabCount == 2, "existing Ctrl+T shortcut");
                    var created = tabs.SelectedTab!.Controls.OfType<ChromiumWebBrowser>().Single();
                    await Ready(created);
                    Check(true, "Ctrl+T still opens a tab");
                    tabs.SelectedIndex = 0; form.Activate(); web.Focus();
                    SendKeys.SendWait("^{TAB}");
                    await Wait(() => tabs.SelectedIndex == 1, "existing Ctrl+Tab shortcut");
                    Check(true, "Ctrl+Tab still switches tabs");
                    created.Focus(); SendKeys.SendWait("^w");
                    await Wait(() => tabs.TabCount == 1, "existing Ctrl+W shortcut");
                    Check(true, "Ctrl+W still closes the current tab");
                    form.Activate(); web.Focus();
                    SendKeys.SendWait("^h");
                    await Wait(() => form.OwnedForms.OfType<HistoryForm>().Any(), "web content Ctrl+H");
                    Check(true, "Ctrl+H opens history from Chromium focus");
                    Check(await Js(web, "window.historyKeyConsumed === true") == "False", "browser shortcut precedes page key handler");
                }
                else Console.WriteLine("NOT_RUN: Chromium focus unavailable for history shortcut");
            }
            else Console.WriteLine("NOT_RUN: foreground toolbar focus unavailable for history shortcut");
            if (!form.OwnedForms.OfType<HistoryForm>().Any()) BrowserMenuItem(form, "OpenHistory").PerformClick();
            var window = form.OwnedForms.OfType<HistoryForm>().Single();
            await Cef.UIThreadTaskFactory.StartNew(() => { });
            Check(Volatile.Read(ref requests) == requestsBeforeOpening, "opening history makes no loopback fixture request");
            var list = (ListView)window.Controls.Find("HistoryList", true).Single();
            var search = (TextBox)window.Controls.Find("HistorySearch", true).Single();
            var pause = (Button)window.Controls.Find("HistoryPause", true).Single();
            await Wait(() => list.Items.Count == 2, "history window initial entries");
            Check(list.Items.Count == 2, "history manager displays session visits");
            window.Activate(); list.Focus();
            if (exerciseKeyboard && HistoryForegroundWindow() == window.Handle && list.Focused)
            {
                SendKeys.SendWait("^f");
                await Wait(() => search.Focused, "history Ctrl+F");
                Check(true, "Ctrl+F focuses history search");
            }
            else if (exerciseKeyboard) Console.WriteLine("NOT_RUN: history list focus unavailable for Ctrl+F");
            if (Environment.GetEnvironmentVariable("AEGIS_HISTORY_SCREENSHOT") == "1")
            {
                using var snapshot = new Bitmap(window.Width, window.Height);
                window.DrawToBitmap(snapshot, new Rectangle(Point.Empty, window.Size));
                snapshot.Save(Path.Combine(root, "history-window.png"));
            }
            search.Text = "no-match";
            Check(list.Items.Count == 0, "history search filters the view");
            search.Text = "";
            pause.PerformClick();
            Check(form.HistoryForTests.IsPaused, "history pause control changes recording state");
            await LoadReady(web, origin + "/other");
            Check(form.HistoryForTests.Snapshot().Count == 2, "paused navigation does not create a visit");
            var pausedLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<FrameLoadEndEventArgs> pausedEnd = (_, e) =>
            { if (e.Frame.IsMain && e.Url.EndsWith("/slow-pause", StringComparison.Ordinal) && e.HttpStatusCode == 200) pausedLoad.TrySetResult(); };
            web.FrameLoadEnd += pausedEnd;
            try
            {
                web.Load(origin + "/slow-pause");
                await pausedRequest.Task.WaitAsync(TimeSpan.FromSeconds(25));
                pause.PerformClick();
                releasePaused.TrySetResult();
                await pausedLoad.Task.WaitAsync(TimeSpan.FromSeconds(25));
            }
            finally { web.FrameLoadEnd -= pausedEnd; }
            Check(form.HistoryForTests.Snapshot().Count == 2, "resuming during a paused navigation does not backfill");
            await LoadReady(web, origin + "/error");
            Check(form.HistoryForTests.Snapshot().Count == 2, "HTTP error page is excluded from history");
            await LoadReady(web, origin + "/other");
            await Wait(() => form.HistoryForTests.Snapshot().Count == 3 && list.Items.Count == 3, "resumed visit");
            Check(true, "history resumes without backfilling paused pages");
            await ReloadReady(web);
            Check(form.HistoryForTests.Snapshot().Count == 3, "reload does not add a history visit");
            var backDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<FrameLoadEndEventArgs> backEnd = (_, e) => { if (e.Frame.IsMain && e.HttpStatusCode == 404) backDone.TrySetResult(); };
            web.FrameLoadEnd += backEnd;
            try { web.Back(); await backDone.Task.WaitAsync(TimeSpan.FromSeconds(25)); }
            finally { web.FrameLoadEnd -= backEnd; }
            Check(form.HistoryForTests.Snapshot().Count == 3, "back to an HTTP error does not add a visit");
            var forwardDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<FrameLoadEndEventArgs> forwardEnd = (_, e) => { if (e.Frame.IsMain && e.HttpStatusCode == 200) forwardDone.TrySetResult(); };
            web.FrameLoadEnd += forwardEnd;
            try { web.Forward(); await forwardDone.Task.WaitAsync(TimeSpan.FromSeconds(25)); }
            finally { web.FrameLoadEnd -= forwardEnd; }
            await Wait(() => form.HistoryForTests.Snapshot().Count == 4 && list.Items.Count == 4, "forward history visit");
            Check(true, "forward creates another visit even after reload");
            list.Items[0].Selected = true;
            ((Button)window.Controls.Find("HistoryDelete", true).Single()).PerformClick();
            await Wait(() => form.HistoryForTests.Snapshot().Count == 3 && list.Items.Count == 3, "history deletion and refreshed view");
            Check(form.HistoryForTests.Snapshot().Count == 3, "delete selected removes the visit");
            window.Activate(); list.Focus(); list.Items[0].Selected = true;
            if (exerciseKeyboard && HistoryForegroundWindow() == window.Handle && list.Focused)
            {
                SendKeys.SendWait("{DELETE}");
                await Wait(() => form.HistoryForTests.Snapshot().Count == 2 && list.Items.Count == 2, "history Delete key");
                Check(true, "Delete key removes the selected history entry");
            }
            else if (exerciseKeyboard) Console.WriteLine("NOT_RUN: history list focus unavailable for Delete");
            list.Items[0].Selected = true;
            Check(list.SelectedItems.Count == 1, "history row selected for opening");
            var beforeNewTab = form.HistoryForTests.Snapshot().Count;
            ((Button)window.Controls.Find("HistoryOpenNew", true).Single()).PerformClick();
            await Wait(() => tabs.TabCount == 2, "open history in new tab");
            await Ready(tabs.SelectedTab!.Controls.OfType<ChromiumWebBrowser>().Single());
            await Wait(() => form.HistoryForTests.Snapshot().Count == beforeNewTab + 1, "new-tab history visit");
            Check(tabs.TabCount == 2, "history opens and records a visit in a new tab");
            var background = tabs.SelectedTab!.Controls.OfType<ChromiumWebBrowser>().Single();
            tabs.SelectedIndex = 0;
            var beforeBackground = form.HistoryForTests.Snapshot().Count;
            await LoadReady(background, origin + "/background");
            await Wait(() => form.HistoryForTests.Snapshot().Count == beforeBackground + 1, "background history visit");
            Check(tabs.SelectedIndex == 0, "background navigation records without switching tabs");
            var beforeIframe = form.HistoryForTests.Snapshot().Count;
            var childLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<FrameLoadEndEventArgs> childEnd = (_, e) =>
            { if (!e.Frame.IsMain && e.Url.EndsWith("/iframe-child", StringComparison.Ordinal)) childLoaded.TrySetResult(); };
            web.FrameLoadEnd += childEnd;
            try
            {
                await LoadReady(web, origin + "/iframe-parent");
                await childLoaded.Task.WaitAsync(TimeSpan.FromSeconds(25));
            }
            finally { web.FrameLoadEnd -= childEnd; }
            await Wait(() => form.HistoryForTests.Snapshot().Count == beforeIframe + 1, "main-frame visit with subframe");
            Check(!form.HistoryForTests.Snapshot().Any(entry => entry.Url.EndsWith("/iframe-child", StringComparison.Ordinal)), "subframe navigation is excluded");
            var beforeEnter = form.HistoryForTests.Snapshot().Count;
            await Wait(() => list.Items.Count == beforeEnter, "history view after background visit");
            foreach (ListViewItem row in list.Items) row.Selected = false;
            list.Items[0].Selected = true;
            window.Activate(); list.Focus();
            if (exerciseKeyboard && HistoryForegroundWindow() == window.Handle && list.Focused)
            {
                SendKeys.SendWait("{ENTER}");
                await Wait(() => form.HistoryForTests.Snapshot().Count == beforeEnter + 1, "history Enter opens current tab");
                Check(true, "Enter opens the selected history entry in the current tab");
            }
            else if (exerciseKeyboard) Console.WriteLine("NOT_RUN: history list focus unavailable for Enter");
            var popups = (SafePopups)web.LifeSpanHandler;
            await Cef.UIThreadTaskFactory.StartNew(() => web.GetBrowserHost().ExecuteDevToolsMethod(0, "Runtime.evaluate",
                new Dictionary<string, object> { ["expression"] = "window.open('" + origin + "/popup','history-fixture')", ["userGesture"] = true }));
            await Wait(() => popups.PopupCount == 1, "history popup created");
            await Wait(() => form.HistoryForTests.Snapshot().Any(entry => entry.Url.EndsWith("/popup", StringComparison.Ordinal)), "popup visit recorded");
            Check(true, "user-created popup contributes a history visit");
            var beforeSameDocument = form.HistoryForTests.Snapshot().Count;
            var sameLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<FrameLoadEndEventArgs> sameEnd = (_, e) => { if (e.Frame.IsMain && e.HttpStatusCode == 200) sameLoaded.TrySetResult(); };
            web.FrameLoadEnd += sameEnd;
            try { web.Load(origin + "/same"); await sameLoaded.Task.WaitAsync(TimeSpan.FromSeconds(25)); }
            finally { web.FrameLoadEnd -= sameEnd; }
            await Wait(() => form.HistoryForTests.Snapshot().Count == beforeSameDocument + 1, "same-document source visit");
            Check(!form.HistoryForTests.Snapshot().Any(entry => entry.Url.Contains("/changed", StringComparison.Ordinal)),
                "same-document address changes are not separately recorded");
            var beforeFailure = form.HistoryForTests.Snapshot().Count;
            var failureSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var failureSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<LoadErrorEventArgs> failureHandler = (_, e) => { if (e.Frame.IsMain && e.ErrorCode != CefErrorCode.Aborted) failureSeen.TrySetResult(); };
            EventHandler<LoadingStateChangedEventArgs> settledHandler = (_, e) => { if (!e.IsLoading) failureSettled.TrySetResult(); };
            web.LoadError += failureHandler; web.LoadingStateChanged += settledHandler;
            try
            {
                web.Load(origin + "/reset");
                await failureSeen.Task.WaitAsync(TimeSpan.FromSeconds(25));
                await failureSettled.Task.WaitAsync(TimeSpan.FromSeconds(25));
            }
            finally { web.LoadError -= failureHandler; web.LoadingStateChanged -= settledHandler; }
            Check(form.HistoryForTests.Snapshot().Count == beforeFailure, "failed navigation is not recorded");
            var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<LoadErrorEventArgs> abortHandler = (_, e) => { if (e.Frame.IsMain && e.ErrorCode == CefErrorCode.Aborted) aborted.TrySetResult(); };
            web.LoadError += abortHandler;
            try
            {
                web.Load(origin + "/slow-abort");
                await abortRequest.Task.WaitAsync(TimeSpan.FromSeconds(25));
                web.Stop();
                await aborted.Task.WaitAsync(TimeSpan.FromSeconds(25));
            }
            finally { web.LoadError -= abortHandler; }
            Check(form.HistoryForTests.Snapshot().Count == beforeFailure, "aborted navigation is not recorded");
            window.Activate();
            if (exerciseKeyboard && HistoryForegroundWindow() == window.Handle)
            {
                SendKeys.SendWait("{ESC}");
                await Wait(() => window.IsDisposed, "history Escape key");
                Check(true, "Escape closes the history window");
                BrowserMenuItem(form, "OpenHistory").PerformClick();
                window = form.OwnedForms.OfType<HistoryForm>().Single();
            }
            else if (exerciseKeyboard) Console.WriteLine("NOT_RUN: history window foreground unavailable for Escape");
            web.Load(origin + "/slow");
            await slowRequest.Task.WaitAsync(TimeSpan.FromSeconds(25));
            form.Close();
            await Wait(() => form.IsDisposed, "history fixture shutdown");
            Check(window.IsDisposed, "history window closes with browser during an in-flight navigation");
        }
        finally
        {
            if (!form.IsDisposed)
            {
                form.Close();
                await Wait(() => form.IsDisposed, "history fixture cleanup");
            }
            cancel.Cancel(); listener.Stop();
            try { await server; } catch (OperationCanceledException) { }
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr HistoryForegroundWindow();
}
