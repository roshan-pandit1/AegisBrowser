using AegisBrowser;
using AegisBrowser.Core;
using CefSharp;
using CefSharp.WinForms;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    private static int HistoryUiProbe(string root, bool bounds = false)
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
                    if (bounds) await HistoryBounds(root); else await HistoryUi(root);
                    using (var reopened = new ProfileLease(Path.Combine(root, "history-profile"))) Check(true, "history fixture lease released");
                    Console.WriteLine(bounds ? $"PASS: {checks} native history bounds checks" : $"PASS: {checks} native history checks");
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

    private static async Task HistoryBounds(string root)
    {
        var profile = Path.Combine(root, "history-profile");
        Directory.CreateDirectory(profile);
        using var lease = new ProfileLease(profile);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancel = new CancellationTokenSource();
        var server = ServeHistoryFixture(listener, cancel.Token);
        var origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        using var form = new MainForm("history-bounds-fixture", profile);
        try
        {
            form.Show();
            var tabs = form.Controls.OfType<TabControl>().Single();
            await Wait(() => tabs.TabCount == 1, "history bounds tab");
            var web = tabs.SelectedTab!.Controls.OfType<ChromiumWebBrowser>().Single();
            await Ready(web);
            var history = form.HistoryForTests;
            Check(history.Snapshot().Count == 0, "history bounds fixture starts empty");
            for (var i = 0; i <= SessionHistoryService.DefaultEntryLimit; i++)
            {
                var target = origin + "/bound/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler changed = (_, _) =>
                {
                    if (history.Snapshot().FirstOrDefault()?.Url == target) recorded.TrySetResult();
                };
                history.Changed += changed;
                try
                {
                    await LoadReady(web, target);
                    await recorded.Task.WaitAsync(TimeSpan.FromSeconds(25));
                }
                finally { history.Changed -= changed; }
                if (i == SessionHistoryService.DefaultEntryLimit - 1)
                {
                    var atLimit = history.Snapshot();
                    Check(atLimit.Count == SessionHistoryService.DefaultEntryLimit &&
                        atLimit.First().Url.EndsWith("/bound/999", StringComparison.Ordinal) &&
                        atLimit.Last().Url.EndsWith("/bound/0", StringComparison.Ordinal), "real browser retains exactly 1000 visits at the limit");
                }
            }
            var afterEviction = history.Snapshot();
            Check(afterEviction.Count == SessionHistoryService.DefaultEntryLimit &&
                afterEviction.First().Url.EndsWith("/bound/1000", StringComparison.Ordinal) &&
                afterEviction.Last().Url.EndsWith("/bound/1", StringComparison.Ordinal), "real browser evicts the oldest visit after 1001 navigations");
            form.Close();
            await Wait(() => form.IsDisposed, "history bounds shutdown");
        }
        finally
        {
            if (!form.IsDisposed)
            {
                form.Close();
                await Wait(() => form.IsDisposed, "history bounds cleanup");
            }
            cancel.Cancel(); listener.Stop();
            try { await server; } catch (OperationCanceledException) { }
        }
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
            await Js(web, "document.title = ''");
            await Wait(() => form.HistoryForTests.Snapshot().Single().Title == "127.0.0.1", "empty history title fallback");
            Check(true, "an empty document title restores the hostname");
            await Js(web, "document.title = 'Fixture title'");
            await Wait(() => form.HistoryForTests.Snapshot().Single().Title == "Fixture title", "restored history title");
            Check(true, "a later nonempty title updates the same visit");
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
            var cefControlL = 0; var handledControlL = 0; var focusedWhenHandled = false;
            var selectProbeAttempted = false; var selectProbeFocused = false;
            var addressEntered = 0; var addressLeft = 0;
            ((BrowserKeys)web.KeyboardHandler).ShortcutObservedForTests = keys =>
            {
                if (keys == (Keys.Control | Keys.L)) Interlocked.Increment(ref cefControlL);
            };
            form.ControlLHandledForTests = focused =>
            {
                handledControlL++; focusedWhenHandled = focused;
                if (!focused)
                {
                    // A diagnostic comparison only: failure of the original focus
                    // request still fails the test even if Select succeeds here.
                    selectProbeAttempted = true;
                    address.Select();
                    selectProbeFocused = address.Focused;
                }
            };
            address.Enter += (_, _) => addressEntered++;
            address.Leave += (_, _) => addressLeft++;
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
                    await FocusHistoryContent(form, web);
                    var enteredBeforeShortcut = addressEntered; var leftBeforeShortcut = addressLeft;
                    SendKeys.SendWait("^l");
                    try
                    {
                        await Wait(() => address.Focused, "existing Ctrl+L shortcut");
                        if (handledControlL > 0 && !focusedWhenHandled)
                            throw new InvalidOperationException("The original Ctrl+L focus request failed; the Select comparison is diagnostic only.");
                    }
                    catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
                    {
                        Console.Error.WriteLine($"HISTORY_SHORTCUT_DIAGNOSTIC: cef_control_l={Volatile.Read(ref cefControlL)} handled_control_l={handledControlL} focused_when_handled={(focusedWhenHandled ? 1 : 0)} select_probe_attempted={(selectProbeAttempted ? 1 : 0)} select_probe_focused={(selectProbeFocused ? 1 : 0)} address_entered_after={addressEntered - enteredBeforeShortcut} address_left_after={addressLeft - leftBeforeShortcut} address_can_focus={(address.CanFocus ? 1 : 0)} foreground={(HistoryForegroundWindow() == form.Handle ? 1 : 0)} browser_focused={(web.ContainsFocus ? 1 : 0)}");
                        throw;
                    }
                    Check(true, "Ctrl+L still focuses the address field");
                    SendKeys.SendWait("^t");
                    await Wait(() => tabs.TabCount == 2, "existing Ctrl+T shortcut");
                    var created = tabs.SelectedTab!.Controls.OfType<ChromiumWebBrowser>().Single();
                    await Ready(created);
                    Check(true, "Ctrl+T still opens a tab");
                    tabs.SelectedIndex = 0; form.Activate(); web.Focus();
                    await FocusHistoryContent(form, web);
                    SendKeys.SendWait("^{TAB}");
                    await Wait(() => tabs.SelectedIndex == 1, "existing Ctrl+Tab shortcut");
                    Check(true, "Ctrl+Tab still switches tabs");
                    await FocusHistoryContent(form, created); SendKeys.SendWait("^w");
                    await Wait(() => tabs.TabCount == 1, "existing Ctrl+W shortcut");
                    Check(true, "Ctrl+W still closes the current tab");
                    await FocusHistoryContent(form, web);
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
            BrowserMenuItem(form, "OpenHistory").PerformClick();
            Check(form.OwnedForms.OfType<HistoryForm>().Count() == 1 && form.OwnedForms.OfType<HistoryForm>().Single() == window,
                "reopening history reuses the existing window");
            await Cef.UIThreadTaskFactory.StartNew(() => { });
            Check(Volatile.Read(ref requests) == requestsBeforeOpening, "opening history makes no loopback fixture request");
            var list = (ListView)window.Controls.Find("HistoryList", true).Single();
            var search = (TextBox)window.Controls.Find("HistorySearch", true).Single();
            var pause = (Button)window.Controls.Find("HistoryPause", true).Single();
            await Wait(() => list.Items.Count == 2, "history window initial entries");
            Check(list.Items.Count == 2, "history manager displays session visits");
            search.Text = "Fixture";
            list.Items[0].Selected = true;
            var retainedSelection = (Guid)list.Items[0].Tag!;
            await Js(web, "document.title = 'Fixture updated'");
            await Wait(() => form.HistoryForTests.Snapshot().First().Title == "Fixture updated" &&
                list.Items.Cast<ListViewItem>().Any(item => item.Tag is Guid id && id == retainedSelection && item.SubItems[1].Text == "Fixture updated"), "history view title refresh");
            Check(search.Text == "Fixture" && list.SelectedItems.Count == 1 && (Guid)list.SelectedItems[0].Tag! == retainedSelection,
                "history updates preserve search text and selection");
            await Js(web, "document.title = 'Fixture title'");
            await Wait(() => form.HistoryForTests.Snapshot().First().Title == "Fixture title", "history title restored after view refresh");
            search.Text = "";
            await CoordinateHistoryAccessibility(window, pause, form);
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
            await LoadReady(web, origin + "/server-error");
            Check(form.HistoryForTests.Snapshot().Count == 2, "HTTP server error page is excluded from history");
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
            await Ready(web);
            var sameDocumentCount = form.HistoryForTests.Snapshot().Count;
            async Task SameDocumentNavigation(Func<Task> action, string expectedPath, string expectedFragment)
            {
                var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var addressEvents = 0; var expectedPathEvents = 0; var sourcePathEvents = 0; var fragmentEvents = 0;
                EventHandler<AddressChangedEventArgs> changed = (_, e) =>
                {
                    Interlocked.Increment(ref addressEvents);
                    if (!Uri.TryCreate(e.Address, UriKind.Absolute, out var addressValue)) return;
                    if (addressValue.AbsolutePath == expectedPath) Interlocked.Increment(ref expectedPathEvents);
                    if (addressValue.AbsolutePath == "/same") Interlocked.Increment(ref sourcePathEvents);
                    if (addressValue.Fragment.Length != 0) Interlocked.Increment(ref fragmentEvents);
                    if (addressValue.AbsolutePath == expectedPath && addressValue.Fragment == expectedFragment) observed.TrySetResult();
                };
                web.AddressChanged += changed;
                try { await action(); await observed.Task.WaitAsync(TimeSpan.FromSeconds(25)); }
                catch (TimeoutException)
                {
                    Console.Error.WriteLine($"HISTORY_SAME_DOCUMENT_DIAGNOSTIC: address_events={addressEvents} expected_path={expectedPathEvents} source_path={sourcePathEvents} fragment={fragmentEvents} can_back={web.CanGoBack} can_forward={web.CanGoForward}");
                    throw;
                }
                finally { web.AddressChanged -= changed; }
                await Cef.UIThreadTaskFactory.StartNew(() => { });
            }
            await SameDocumentNavigation(async () => { await Js(web, "location.hash = 'history-fixture'"); }, "/changed", "#history-fixture");
            Check(form.HistoryForTests.Snapshot().Count == sameDocumentCount, "fragment navigation does not add a visit");
            var browserHistoryBeforePush = int.Parse(await Js(web, "history.length"), System.Globalization.CultureInfo.InvariantCulture);
            // Chromium's back/forward intervention skips entries created without user activation.
            // Give both fixture operations a user gesture so the browser controls visit them.
            Task PushWithGesture(string path) => Cef.UIThreadTaskFactory.StartNew(() =>
                web.GetBrowserHost().ExecuteDevToolsMethod(0, "Runtime.evaluate", new Dictionary<string, object>
                {
                    ["expression"] = "window.fixtureGestureObserved = navigator.userActivation.isActive; history.pushState({fixture:1}, '', '" + path + "')",
                    ["userGesture"] = true
                }));
            await SameDocumentNavigation(() => PushWithGesture("/same-step-a"), "/same-step-a", "");
            Check(await Js(web, "window.fixtureGestureObserved === true") == "True", "same-document fixture observes user activation");
            await SameDocumentNavigation(() => PushWithGesture("/same-step-b"), "/same-step-b", "");
            Check(int.Parse(await Js(web, "history.length"), System.Globalization.CultureInfo.InvariantCulture) == browserHistoryBeforePush + 2 &&
                form.HistoryForTests.Snapshot().Count == sameDocumentCount, "post-load pushState creates browser entries without recording visits");
            var nativeEntries = (await web.GetBrowserHost().GetNavigationEntriesAsync(false)).ToList();
            var currentEntry = nativeEntries.FindIndex(entry => entry.IsCurrent);
            var previousIsSameDocument = currentEntry > 0 && Uri.TryCreate(nativeEntries[currentEntry - 1].Url, UriKind.Absolute, out var previousEntry) &&
                previousEntry.AbsolutePath == "/same-step-a" && previousEntry.Fragment.Length == 0;
            Console.WriteLine($"HISTORY_NATIVE_ENTRY_COUNTS: total={nativeEntries.Count} current_index={currentEntry} expected_predecessor={(previousIsSameDocument ? 1 : 0)}");
            Check(previousIsSameDocument, "Chromium navigation list confirms the same-document predecessor");
            await SameDocumentNavigation(() => { web.Back(); return Task.CompletedTask; }, "/same-step-a", "");
            Check(form.HistoryForTests.Snapshot().Count == sameDocumentCount, "same-document back does not add a visit");
            await SameDocumentNavigation(() => { web.Forward(); return Task.CompletedTask; }, "/same-step-b", "");
            Check(form.HistoryForTests.Snapshot().Count == sameDocumentCount, "same-document forward does not add a visit");
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

            await Wait(() => list.Items.Count == form.HistoryForTests.Snapshot().Count && list.Items.Count >= 2, "history rows before multi-select");
            foreach (ListViewItem row in list.Items) row.Selected = false;
            var beforeMultipleDelete = form.HistoryForTests.Snapshot().Count;
            list.Items[0].Selected = true; list.Items[1].Selected = true;
            Check(list.SelectedItems.Count == 2, "multiple history rows can be selected");
            ((Button)window.Controls.Find("HistoryDelete", true).Single()).PerformClick();
            await Wait(() => form.HistoryForTests.Snapshot().Count == beforeMultipleDelete - 2 && list.Items.Count == beforeMultipleDelete - 2, "multiple history deletion");
            Check(true, "delete selected removes multiple visits");

            foreach (ListViewItem row in list.Items) row.Selected = false;
            list.Items[0].Selected = true;
            var selectedId = (Guid)list.Items[0].Tag!;
            var selectedUrl = form.HistoryForTests.Snapshot().Single(entry => entry.Id == selectedId).Url;
            var beforeOpenCurrent = form.HistoryForTests.Snapshot().Count;
            var tabCountBeforeOpen = tabs.TabCount;
            ((Button)window.Controls.Find("HistoryOpen", true).Single()).PerformClick();
            await Wait(() => form.HistoryForTests.Snapshot().Count == beforeOpenCurrent + 1, "open history in current tab");
            Check(tabs.TabCount == tabCountBeforeOpen && form.HistoryForTests.Snapshot().First().Url == selectedUrl,
                "history opens the selected visit in the current tab");

            form.Activate(); address.Focus();
            if (address.Focused)
            {
                const string editing = "unfinished input";
                address.Text = editing;
                var beforeEditingNavigation = form.HistoryForTests.Snapshot().Count;
                Check(form.SubmitAddress(selectedUrl), "navigation while editing is accepted");
                await Wait(() => form.HistoryForTests.Snapshot().Count == beforeEditingNavigation + 1, "navigation while address is edited");
                Check(address.Focused && address.Text == editing, "navigation does not overwrite an actively edited address");
            }
            else Console.WriteLine("NOT_RUN: address focus unavailable for editing-preservation observation");

            var beforeClear = form.HistoryForTests.Snapshot().Count;
            Check(AnswerHistoryClear(window, false), "clear confirmation explains caches and cookies are unaffected");
            Check(form.HistoryForTests.Snapshot().Count == beforeClear, "declining clear preserves session visits");
            Check(AnswerHistoryClear(window, true), "clear confirmation remains visible when accepted");
            await Wait(() => form.HistoryForTests.Snapshot().Count == 0 && list.Items.Count == 0, "clear all history");
            Check(true, "confirming clear removes all session visits");

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

    private static async Task FocusHistoryContent(MainForm form, ChromiumWebBrowser browser)
    {
        form.Activate(); browser.Focus();
        if (await Js(browser, "document.hasFocus()") != "True")
        {
            var documentFocus = browser.EvaluateScriptAsPromiseAsync(
                "return new Promise(resolve => { if (document.hasFocus()) resolve(true); else window.addEventListener('focus', () => resolve(true), { once: true }); });",
                TimeSpan.FromSeconds(25));
            await Cef.UIThreadTaskFactory.StartNew(() => browser.GetBrowserHost().SetFocus(true));
            var response = await documentFocus.WaitAsync(TimeSpan.FromSeconds(25));
            if (!response.Success || response.Result is not true)
                throw new InvalidOperationException("The fixture did not observe focused Chromium content.");
        }
        if (HistoryForegroundWindow() != form.Handle || !browser.ContainsFocus)
            throw new InvalidOperationException("The fixture did not establish focused Chromium content before sending a shortcut.");
    }

    // A separate UI Automation process inspects this live window in each
    // recording state. The fixture passes only a window handle and process ID.
    private static async Task CoordinateHistoryAccessibility(HistoryForm window, Button pause, MainForm form)
    {
        var helper = Environment.GetEnvironmentVariable("AEGIS_HISTORY_UIA_HELPER");
        var evidence = Environment.GetEnvironmentVariable("AEGIS_HISTORY_UIA_EVIDENCE");
        if (string.IsNullOrEmpty(helper) && string.IsNullOrEmpty(evidence)) return;
        if (string.IsNullOrEmpty(helper) || string.IsNullOrEmpty(evidence) || !File.Exists(helper))
            throw new InvalidOperationException("Accessibility fixture inputs are incomplete.");
        async Task Observe(string state)
        {
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", helper,
                "-WindowHandle", window.Handle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-OwnerProcessId", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-State", state, "-EvidenceDirectory", evidence }) start.ArgumentList.Add(argument);
            using var observer = new System.Diagnostics.Process { StartInfo = start };
            if (!observer.Start()) throw new InvalidOperationException("Could not start the accessibility observer.");
            var output = observer.StandardOutput.ReadToEndAsync();
            var errors = observer.StandardError.ReadToEndAsync();
            await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            var observed = await output;
            var diagnostic = await errors;
            if (observer.ExitCode != 0)
                throw new InvalidOperationException("Accessibility observer failed: " +
                    System.Text.RegularExpressions.Regex.Replace(diagnostic, @"https?://\S+", "[redacted]"));
            Console.Write(observed);
        }
        try
        {
            if (form.HistoryForTests.IsPaused || window.IsDisposed) throw new InvalidOperationException("Accessibility fixture was not active.");
            await Observe("active");
            pause.PerformClick();
            await Wait(() => form.HistoryForTests.IsPaused && pause.Text == "Resume recording", "paused accessibility state");
            await Observe("paused");
        }
        finally
        {
            if (!window.IsDisposed && form.HistoryForTests.IsPaused) pause.PerformClick();
        }
        await Wait(() => !form.HistoryForTests.IsPaused && pause.Text == "Pause recording", "restored accessibility state");
        Check(true, "accessibility fixture restored active history recording");
    }

    // A thread-local CBT hook observes the real MessageBox and answers it; no
    // global keyboard input, sleeps, or production dialog replacement is used.
    private static bool AnswerHistoryClear(HistoryForm window, bool accept)
    {
        const int cbtActivate = 5, whCbt = 5, wmCommand = 0x111, wmClose = 0x10, owner = 4;
        var observed = false;
        var explained = false;
        Exception? callbackError = null;
        IntPtr hook = IntPtr.Zero;
        HistoryCbtHook callback = (code, wParam, lParam) =>
        {
            var isTarget = false;
            try
            {
                if (code == cbtActivate && HistoryGetWindow(wParam, owner) == window.Handle &&
                    HistoryWindowText(wParam) == "Clear session history")
                {
                    isTarget = true;
                    observed = true;
                    var child = IntPtr.Zero;
                    while ((child = HistoryFindWindowEx(wParam, child, "Static", null)) != IntPtr.Zero)
                        explained |= HistoryWindowText(child).Contains("Chromium caches and cookies are not affected.", StringComparison.Ordinal);
                    if (!HistoryPostMessage(wParam, wmCommand, (IntPtr)(accept ? 6 : 7), IntPtr.Zero))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            catch (Exception ex)
            {
                callbackError = ex;
                if (isTarget) HistoryPostMessage(wParam, wmClose, IntPtr.Zero, IntPtr.Zero);
            }
            return HistoryCallNextHookEx(hook, code, wParam, lParam);
        };
        using var deadline = new System.Windows.Forms.Timer { Interval = 25000 };
        deadline.Tick += (_, _) =>
        {
            deadline.Stop();
            var dialog = HistoryFindWindow(null, "Clear session history");
            if (dialog != IntPtr.Zero && HistoryGetWindow(dialog, owner) == window.Handle)
                HistoryPostMessage(dialog, wmClose, IntPtr.Zero, IntPtr.Zero);
        };
        try
        {
            hook = HistorySetWindowsHookEx(whCbt, callback, IntPtr.Zero, HistoryGetCurrentThreadId());
            if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            deadline.Start();
            ((Button)window.Controls.Find("HistoryClear", true).Single()).PerformClick();
        }
        finally
        {
            deadline.Stop();
            if (hook != IntPtr.Zero && !HistoryUnhookWindowsHookEx(hook))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            GC.KeepAlive(callback);
        }
        if (callbackError is not null) throw new InvalidOperationException("Could not observe the history confirmation.", callbackError);
        if (!observed) throw new TimeoutException("History clear confirmation was not observed before the fixture deadline.");
        return explained;
    }

    private static string HistoryWindowText(IntPtr window)
    {
        var text = new StringBuilder(HistoryGetWindowTextLength(window) + 1);
        HistoryGetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    private delegate IntPtr HistoryCbtHook(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint HistoryGetCurrentThreadId();
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr HistorySetWindowsHookEx(int idHook, HistoryCbtHook callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", EntryPoint = "CallNextHookEx")]
    private static extern IntPtr HistoryCallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "UnhookWindowsHookEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HistoryUnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern IntPtr HistoryGetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
    private static extern IntPtr HistoryFindWindow(string? className, string? title);
    [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)]
    private static extern IntPtr HistoryFindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)]
    private static extern int HistoryGetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int HistoryGetWindowText(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HistoryPostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
