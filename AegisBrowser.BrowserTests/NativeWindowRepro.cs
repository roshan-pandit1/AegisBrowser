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
                            control.ControlAdded += (_, e) => Track(e.Control, e.Control.GetType().Name);
                            foreach (Control child in control.Controls) Track(child, child.GetType().Name);
                        }
                        Track(form, "MainForm");
                        form.Show();
                        var tabs = form.Controls.OfType<TabControl>().Single();
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
                        for (var tab = 0; tab < 4; tab++) Button("+").PerformClick();
                        Trace("MainForm", form, "rapid-tabs-created", form.IsHandleCreated ? form.Handle : IntPtr.Zero);
                        form.Close(); form.Close();
                        await Wait(() => form.IsDisposed, "window repro normal asynchronous shutdown");
                        Console.WriteLine($"PASS: native window candidate reproduction cycle {number} closed normally");
                    }
                }
                catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
                finally { host.Close(); }
            };
            Application.Run(host);
        }
        catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
        finally { if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); } }
        if (result == 0) Console.WriteLine("PASS: native window candidate reproduction completed and CEF shut down");
        return result;
    }
}
