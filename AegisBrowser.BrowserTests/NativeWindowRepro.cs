using System.Diagnostics;
using System.Collections.Concurrent;
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
        var rapidEvents = new ConcurrentQueue<PopupLifecycleEvent>();
        try
        {
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(new CefSettings
            {
                RootCachePath = root, CachePath = Path.Combine(root, "cache"),
                CommandLineArgsDisabled = true, LogFile = Path.Combine(root, "cef-window-repro.log")
            });
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
                        void Trace(string name, Control control, string state, IntPtr handle) =>
                            Console.WriteLine($"WINDOW-TRACE iteration={number} elapsedMs={timer.ElapsedMilliseconds} thread={Environment.CurrentManagedThreadId} control={name} state={state} hwnd=0x{handle.ToInt64():x} handleCreated={control.IsHandleCreated}");
                        void Track(Control control, string name)
                        {
                            var handle = control.IsHandleCreated ? control.Handle : IntPtr.Zero;
                            control.HandleCreated += (_, _) => { handle = control.Handle; Trace(name, control, "created", handle); };
                            control.HandleDestroyed += (_, _) => Trace(name, control, "destroyed", handle);
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
                        var previous = Current();
                        Button("×").PerformClick();
                        await Wait(() => tabs.TabCount == 1 && Current() != previous, "window repro last-tab replacement");
                        await Ready(Current());
                        Trace("MainForm", form, "replacement-ready", form.IsHandleCreated ? form.Handle : IntPtr.Zero);
                        // Exercise a real owner popup while the window itself closes.
                        // One cycle keeps this native smoke test bounded on CI.
                        SafePopups? activePopupOwner = null;
                        ChromiumWebBrowser? rapidPopupBrowser = iteration == 1 ? Current() : null;
                        SafePopups? rapidPopupOwner = rapidPopupBrowser is null ? null :
                            rapidPopupBrowser.LifeSpanHandler as SafePopups ?? throw new InvalidOperationException("Missing rapid-popup lifetime handler.");
                        // Exactly one synthetic popup request uses this owner. The
                        // observer reports event kinds only, never URLs or page data.
                        rapidPopupOwner?.SetLifecycleObserverForTest(rapidEvents.Enqueue);
                        if (iteration == 0)
                        {
                            var popupBrowser = Current();
                            activePopupOwner = (SafePopups)popupBrowser.LifeSpanHandler;
                            await Cef.UIThreadTaskFactory.StartNew(() => popupBrowser.GetBrowserHost().ExecuteDevToolsMethod(0, "Runtime.evaluate",
                                new Dictionary<string, object> { ["expression"] = "window.open('about:blank','shutdown-popup')", ["userGesture"] = true }));
                            await Wait(() => activePopupOwner.PopupCount == 1, "window repro active popup created");
                        }
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
                        if (rapidPopupBrowser is not null)
                        {
                            // Submit a real popup request just before closing. Dispatching
                            // DevTools does not prove OnBeforePopup or OnAfterCreated ran.
                            await Cef.UIThreadTaskFactory.StartNew(() => rapidPopupBrowser.GetBrowserHost().ExecuteDevToolsMethod(0, "Runtime.evaluate",
                                new Dictionary<string, object> { ["expression"] = "window.open('about:blank','rapid-shutdown-popup')", ["userGesture"] = true }));
                        }
                        form.Close(); form.Close();
                        await Wait(() => form.IsDisposed, "window repro normal asynchronous shutdown");
                        Check(!removedBeforeNativeClose, "window retains tab parents until all native browser owners close");
                        if (activePopupOwner is not null)
                        {
                            Check(activePopupOwner.PopupCount == 0 && activePopupOwner.OwnerClosed.IsCompletedSuccessfully,
                                "window close completes native owner and active popup closure");
                        }
                        if (rapidPopupOwner is not null)
                        {
                            Check(rapidPopupOwner.OwnerClosed.IsCompletedSuccessfully && rapidPopupOwner.PopupCount == 0,
                                "window closes tracked owner after a rapid popup request");
                        }
                        Console.WriteLine($"PASS: native window candidate reproduction cycle {number} closed normally");
                    }
                }
                catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
                finally { host.Close(); }
            };
            Application.Run(host);
        }
        catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
        finally
        {
            if (initialized)
            {
                Cef.WaitForBrowsersToClose(10000);
                // Snapshot after the bounded browser-close wait. Missing creation
                // may mean abort or a later callback; this binding does not expose
                // a terminal abort signal, so absence is never called safe.
                var observed = rapidEvents.ToArray();
                var approved = Array.IndexOf(observed, PopupLifecycleEvent.PopupApproved);
                var ready = Array.IndexOf(observed, PopupLifecycleEvent.OwnerReady);
                var created = Array.IndexOf(observed, PopupLifecycleEvent.PopupCreated);
                Console.WriteLine("POPUP-LIFECYCLE observations=" + string.Join(",", observed));
                if (approved >= 0 && ready > approved && created > ready)
                {
                    result = 1;
                    Console.Error.WriteLine("UNSAFE_OBSERVED: owner readiness preceded creation of the approved synthetic popup.");
                }
                else if (approved >= 0 && created > approved && ready > created)
                    Console.WriteLine("OBSERVED: popup creation preceded owner readiness in this run; other orderings remain unproven.");
                else
                    Console.WriteLine("INCONCLUSIVE: approved-popup-in-flight ordering was not established in the bounded observation.");
                Cef.Shutdown();
            }
        }
        if (result == 0) Console.WriteLine("PASS: native window candidate reproduction completed and CEF shut down");
        return result;
    }
}
