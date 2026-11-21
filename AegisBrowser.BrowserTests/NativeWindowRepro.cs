using System.Diagnostics;
using AegisBrowser;
using CefSharp;
using CefSharp.WinForms;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // A smaller candidate reproduction of the observed last-tab / rapid-close sequence.
    // It is diagnostic, not evidence that the intermittent hwnd_util failure is fixed.
    private static int NativeWindowRepro(string profileRoot)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var root = Path.GetFullPath(profileRoot); Directory.CreateDirectory(root);
        var initialized = false; var result = 0;
        var run = Guid.NewGuid().ToString("N");
        var elapsed = Stopwatch.StartNew();
        var traceGate = new object();
        var sequence = 0;
        var dropped = 0;
        const int maximumEvents = 2000;
        var trackedOwners = new System.Collections.Concurrent.ConcurrentDictionary<string, SafePopups>();
        // This trace is bounded and records observations, not an authoritative
        // CEF browser count. Owner task continuations can be scheduled later.
        void Event(string kind, string detail = "none")
        {
            lock (traceGate)
            {
                if (sequence >= maximumEvents) { dropped++; return; }
                Console.WriteLine($"WINDOW-LIFECYCLE run={run} sequence={++sequence} elapsedMs={elapsed.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId} kind={kind} detail={detail}");
            }
        }
        void Snapshot(string reason)
        {
            foreach (var pair in trackedOwners.OrderBy(p => p.Key, StringComparer.Ordinal))
                Event("OWNER_SNAPSHOT", $"reason={reason},owner={pair.Key},ready={pair.Value.OwnerCloseReady.IsCompleted},closed={pair.Value.OwnerClosed.IsCompleted}");
            Event("TRACE_SNAPSHOT", $"reason={reason},trackedOwners={trackedOwners.Count},dropped={dropped}");
        }
        EventHandler exitHandler = (_, _) => Event("PROCESS_EXIT_OBSERVED");
        AppDomain.CurrentDomain.ProcessExit += exitHandler;
        try
        {
            Event("CEF_INITIALIZE_REQUESTED");
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(new CefSettings
            {
                RootCachePath = root, CachePath = Path.Combine(root, "cache"),
                CommandLineArgsDisabled = true, LogFile = Path.Combine(root, "cef-window-repro.log")
            });
            Event("CEF_INITIALIZE_RETURNED", $"initialized={initialized}");
            if (!initialized) throw new IOException("CEF initialization failed.");
            using var context = Cef.GetGlobalRequestContext();
            context.RegisterSchemeHandlerFactory("https", "aegis-diagnostics.invalid", new DiagnosticsFactory());
            using var host = new Form { Width = 250, Height = 150, Text = "Native window research" };
            host.Shown += async (_, _) =>
            {
                try
                {
                    for (var iteration = 0; iteration < 5; iteration++)
                    {
                        var timer = Stopwatch.StartNew();
                        using var form = new MainForm("synthetic-window-repro");
                        var number = iteration + 1;
                        var nextBrowser = 0;
                        var trackedControls = new HashSet<Control>();
                        Event("WINDOW_CONSTRUCTED", $"iteration={number}");
                        form.FormClosing += (_, e) => Event("FORM_CLOSING_OBSERVED", $"iteration={number},cancel={e.Cancel}");
                        form.FormClosed += (_, _) => Event("FORM_CLOSED_OBSERVED", $"iteration={number}");
                        form.Disposed += (_, _) => Event("FORM_DISPOSED_OBSERVED", $"iteration={number}");
                        void Trace(string name, Control control, string state, IntPtr handle) =>
                            Event("CONTROL_OBSERVED", $"iteration={number},iterationMs={timer.ElapsedMilliseconds},control={name},state={state},hwnd=0x{handle.ToInt64():x},handleCreated={control.IsHandleCreated}");
                        void Track(Control control, string name)
                        {
                            if (!trackedControls.Add(control)) return;
                            var handle = control.IsHandleCreated ? control.Handle : IntPtr.Zero;
                            Trace(name, control, "tracked", handle);
                            control.HandleCreated += (_, _) => { handle = control.Handle; Trace(name, control, "created", handle); };
                            control.HandleDestroyed += (_, _) => Trace(name, control, "destroyed", handle);
                            control.Disposed += (_, _) => Event("CONTROL_DISPOSED_OBSERVED", $"iteration={number},control={name}");
                            if (control is ChromiumWebBrowser browser && browser.LifeSpanHandler is SafePopups owner)
                            {
                                var ownerId = $"{number}-{++nextBrowser}";
                                trackedOwners.TryAdd(ownerId, owner);
                                Event("BROWSER_CONTROL_TRACKED", $"owner={ownerId},initialized={browser.IsBrowserInitialized}");
                                browser.IsBrowserInitializedChanged += (_, _) => Event("BROWSER_INITIALIZATION_CHANGED", $"owner={ownerId},initialized={browser.IsBrowserInitialized}");
                                _ = owner.OwnerCloseReady.ContinueWith(_ => Event("OWNER_CLOSE_READY_OBSERVED", $"owner={ownerId}"), TaskScheduler.Default);
                                _ = owner.OwnerClosed.ContinueWith(_ => Event("OWNER_CLOSED_OBSERVED", $"owner={ownerId}"), TaskScheduler.Default);
                            }
                            control.ControlAdded += (_, e) => { if (e.Control is Control child) Track(child, child.GetType().Name); };
                            foreach (Control child in control.Controls) Track(child, child.GetType().Name);
                        }
                        Track(form, "MainForm");
                        form.Show();
                        var tabs = form.Controls.OfType<TabControl>().Single();
                        tabs.SelectedIndexChanged += (_, _) => Trace("TabControl", tabs, "selection-" + tabs.SelectedIndex, tabs.IsHandleCreated ? tabs.Handle : IntPtr.Zero);
                        tabs.ControlRemoved += (_, _) => Trace("TabControl", tabs, "child-removed", tabs.IsHandleCreated ? tabs.Handle : IntPtr.Zero);
                        var toolbar = form.Controls.OfType<TableLayoutPanel>().Single();
                        Button Button(string text) => toolbar.Controls.OfType<Button>().Single(b => b.Text == text);
                        ChromiumWebBrowser Current() => tabs.SelectedTab!.Controls.OfType<ChromiumWebBrowser>().Single();
                        await Wait(() => tabs.TabCount == 1, "window repro initial tab");
                        await Ready(Current());
                        Event("INITIAL_BROWSER_READY", $"iteration={number}");
                        var previous = Current();
                        Event("LAST_TAB_CLOSE_REQUESTED", $"iteration={number}");
                        Button("×").PerformClick();
                        await Wait(() => tabs.TabCount == 1 && Current() != previous, "window repro last-tab replacement");
                        await Ready(Current());
                        Trace("MainForm", form, "replacement-ready", form.IsHandleCreated ? form.Handle : IntPtr.Zero);
                        for (var tab = 0; tab < 4; tab++) Button("+").PerformClick();
                        Trace("MainForm", form, "rapid-tabs-created", form.IsHandleCreated ? form.Handle : IntPtr.Zero);
                        var owners = tabs.TabPages.Cast<TabPage>()
                            .SelectMany(page => page.Controls.OfType<ChromiumWebBrowser>())
                            .Select(browser => (SafePopups)browser.LifeSpanHandler).ToArray();
                        var removedBeforeNativeClose = false;
                        tabs.ControlRemoved += (_, _) =>
                        {
                            if (owners.Any(owner => !owner.OwnerClosed.IsCompleted)) removedBeforeNativeClose = true;
                        };
                        Event("WINDOW_CLOSE_REQUESTED", $"iteration={number},owners={owners.Length}");
                        form.Close(); form.Close();
                        await Wait(() => form.IsDisposed, "window repro normal asynchronous shutdown");
                        Event("WINDOW_DISPOSAL_OBSERVED", $"iteration={number}");
                        Check(!removedBeforeNativeClose, "window retains tab parents until all native browser owners close");
                        Console.WriteLine($"PASS: native window candidate reproduction cycle {number} closed normally");
                    }
                }
                catch (Exception ex) { result = 1; Event("TEST_EXCEPTION", ex.GetType().Name); Snapshot("test-failure"); Console.Error.WriteLine(ex); }
                finally { Event("HOST_CLOSE_REQUESTED"); host.Close(); }
            };
            Event("MESSAGE_LOOP_ENTERING");
            Application.Run(host);
            Event("MESSAGE_LOOP_RETURNED");
        }
        catch (Exception ex) { result = 1; Event("OUTER_EXCEPTION", ex.GetType().Name); Snapshot("outer-failure"); Console.Error.WriteLine(ex); }
        finally
        {
            if (initialized)
            {
                Snapshot("before-cef-wait");
                Event("CEF_WAIT_ENTERING", "browserCount=not-exposed");
                Cef.WaitForBrowsersToClose(10000);
                Event("CEF_WAIT_RETURNED", "browserCount=not-exposed");
                Event("CEF_SHUTDOWN_ENTERING");
                Cef.Shutdown();
                Event("CEF_SHUTDOWN_RETURNED");
            }
        }
        if (result == 0) Console.WriteLine("PASS: native window candidate reproduction completed and CEF shut down");
        Event("TEST_MAIN_RETURN", $"result={result},dropped={dropped}");
        return result;
    }
}
