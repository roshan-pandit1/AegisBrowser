using AegisBrowser;
using AegisBrowser.Core;
using CefSharp;
using CefSharp.WinForms;
using System.Text.Json;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // A separate process uses disposable profile directories and the real
    // MainForm layout. No browser API values or display settings are changed.
    private static int ViewportCssProbe(string profileRoot, string phase)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var root = Path.GetFullPath(profileRoot);
        Directory.CreateDirectory(root);
        var enabledPath = Path.Combine(root, "enabled");
        var disabledPath = Path.Combine(root, "disabled");
        if (phase == "viewport-write")
        {
            new PrivacySettingsStore(enabledPath).Save(new PrivacyConfiguration { LetterboxWebContent = true });
            new PrivacySettingsStore(disabledPath).Save(new PrivacyConfiguration { LetterboxWebContent = false });
        }
        var initialized = false;
        var completed = false;
        var failed = false;
        try
        {
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(new CefSettings
            {
                RootCachePath = root, CachePath = Path.Combine(root, "global"),
                CommandLineArgsDisabled = true, LogFile = Path.Combine(root, "cef-viewport.log")
            });
            if (!initialized) throw new InvalidOperationException("CEF initialization failed.");
            using var context = Cef.GetGlobalRequestContext();
            if (!context.RegisterSchemeHandlerFactory("https", "aegis-diagnostics.invalid", new DiagnosticsFactory()) ||
                !context.RegisterSchemeHandlerFactory("https", "browser-tests.invalid", new Fixture()))
                throw new InvalidOperationException("Viewport fixture registration failed.");
            using var host = new Form { Width = 200, Height = 150, ShowInTaskbar = false };
            host.Shown += async (_, _) =>
            {
                try
                {
                    Check(new PrivacySettingsStore(enabledPath).Load().LetterboxWebContent,
                        "enabled viewport preference persisted");
                    Check(!new PrivacySettingsStore(disabledPath).Load().LetterboxWebContent,
                        "disabled viewport preference persisted");
                    if (phase == "viewport-write") await MeasureMainForm(disabledPath, false);
                    await MeasureMainForm(enabledPath, true);
                    completed = true;
                    Console.WriteLine($"PASS: {checks} CSS viewport checks ({phase})");
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
        if (!failed && completed) Console.WriteLine($"PASS: viewport CEF shutdown completed ({phase})");
        return !failed && completed ? 0 : 1;
    }

    private static async Task MeasureMainForm(string profilePath, bool enabled)
    {
        var form = new MainForm("viewport-test", profilePath);
        try
        {
            form.Show();
            var tabs = form.Controls.OfType<TabControl>().Single();
            var toolbar = form.Controls.OfType<TableLayoutPanel>().Single();
            Button Button(string label) => toolbar.Controls.OfType<Button>().Single(b => b.Text == label);
            ChromiumWebBrowser Current() => tabs.SelectedTab!.Controls.OfType<ChromiumWebBrowser>().Single();
            await Wait(() => tabs.TabCount == 1, "viewport initial tab");
            var browser = Current();
            await Ready(browser);
            await LoadReady(browser, Origin + "viewport");
            await ReportViewport(form, tabs, browser, enabled, "initial");
            await CheckViewportFixture(browser);

            await ResizeViewportAndWait(browser, () => form.Size = new Size(1023, 713));
            await ReportViewport(form, tabs, browser, enabled, "resize_1023x713");
            await ResizeViewportAndWait(browser, () => form.Size = new Size(1089, 751));
            await ReportViewport(form, tabs, browser, enabled, "resize_1089x751");
            await ResizeViewportAndWait(browser, () => form.Size = new Size(1099, 761));
            await ReportViewport(form, tabs, browser, enabled, "resize_1099x761");
            if (!enabled) return;
            Button("+").PerformClick();
            await Wait(() => tabs.TabCount == 2, "viewport second tab");
            await Ready(Current());
            await LoadReady(Current(), Origin + "viewport-second");
            await ReportViewport(form, tabs, Current(), enabled, "new_tab");
            tabs.SelectedIndex = 0;
            await Ready(Current());
            await ReportViewport(form, tabs, Current(), enabled, "tab_switch");

            // Zoom is an intentionally unchanged Chromium behavior. Measure it,
            // rather than claiming the original CSS buckets survive zoom.
            var widthBeforeZoom = int.Parse(await Js(browser, "window.innerWidth"));
            browser.GetBrowserHost().SetZoomLevel(1.0);
            await WaitForAsync(async () => Math.Abs(await browser.GetBrowserHost().GetZoomLevelAsync() - 1.0) < 0.01 &&
                int.Parse(await Js(browser, "window.innerWidth")) != widthBeforeZoom,
                "viewport zoom change");
            await ReportViewport(form, tabs, browser, enabled, "zoom_1");
            var widthAtZoom = int.Parse(await Js(browser, "window.innerWidth"));
            browser.GetBrowserHost().SetZoomLevel(0);
            await WaitForAsync(async () => Math.Abs(await browser.GetBrowserHost().GetZoomLevelAsync()) < 0.01 &&
                int.Parse(await Js(browser, "window.innerWidth")) != widthAtZoom,
                "viewport zoom reset");
            await ReportViewport(form, tabs, browser, enabled, "zoom_reset");
            await ResizeViewportAndWait(browser, () => form.WindowState = FormWindowState.Maximized);
            await ReportViewport(form, tabs, browser, enabled, "maximized");
            await ResizeViewportAndWait(browser, () => form.WindowState = FormWindowState.Normal);
            await ReportViewport(form, tabs, browser, enabled, "restored");
            form.WindowState = FormWindowState.Minimized;
            form.WindowState = FormWindowState.Normal;
            await ReportViewport(form, tabs, browser, enabled, "minimize_restore");
            await LoadReady(browser, Origin + "viewport-navigation");
            await ReportViewport(form, tabs, browser, enabled, "navigation");
        }
        finally
        {
            if (!form.IsDisposed)
            {
                form.Close();
                await Wait(() => form.IsDisposed, "viewport form native close");
            }
        }
    }

    private static string ViewportFixturePage() => """
        <!doctype html><meta name="viewport" content="width=device-width, initial-scale=1">
        <style>body{margin:0;min-height:1800px}#fixed{position:fixed;right:0;top:0}
        #layout::after{content:'desktop'}@media(max-width:900px){#layout::after{content:'mobile'}}</style>
        <div id="layout">Controlled viewport fixture</div><div id="fixed">fixed</div>
        <form id="form"><input id="entry" value="initial"><button type="button">Test</button></form>
        <dialog id="dialog">Local dialog</dialog><video id="media" controls></video>
        <iframe id="child" title="local fixture" srcdoc="<!doctype html><p>Local embedded content</p>"></iframe>
        """;

    private static async Task CheckViewportFixture(ChromiumWebBrowser browser)
    {
        var result = await Js(browser, """
            (()=>{const entry=document.querySelector('#entry'), dialog=document.querySelector('#dialog');
              entry.value='test';dialog.showModal();const opened=dialog.open;dialog.close();
              const fixed=getComputedStyle(document.querySelector('#fixed')).position==='fixed';
              const media=document.querySelector('#media').controls;
              const frame=!!document.querySelector('#child').contentDocument;
              window.scrollTo(0,400);return JSON.stringify({form:entry.value==='test',dialog:opened&&!dialog.open,
                fixed,media,frame,responsive:matchMedia('(max-width:900px)').matches,scrollable:document.documentElement.scrollHeight>innerHeight});})()
            """);
        using var data = JsonDocument.Parse(result);
        foreach (var property in new[] { "form", "dialog", "fixed", "media", "frame", "scrollable" })
            Check(data.RootElement.GetProperty(property).GetBoolean(), "controlled viewport fixture " + property);
        Console.WriteLine("P4LB_COMPATIBILITY: responsive_mobile=" + data.RootElement.GetProperty("responsive").GetBoolean());
        await Js(browser, "window.scrollTo(0,0);'reset'");
    }

    private static async Task ResizeViewportAndWait(ChromiumWebBrowser browser, Action resize)
    {
        await Js(browser, "window.__p4lbResize=0;window.addEventListener('resize',()=>window.__p4lbResize++);'ready'");
        var before = browser.ClientSize;
        resize();
        if (browser.ClientSize != before)
            await WaitForAsync(async () => int.Parse(await Js(browser, "window.__p4lbResize")) > 0,
                "renderer viewport resize event");
    }

    private static async Task ReportViewport(MainForm form, TabControl tabs, ChromiumWebBrowser browser, bool enabled, string scenario)
    {
        const string script = "JSON.stringify({innerWidth:window.innerWidth,innerHeight:window.innerHeight,clientWidth:document.documentElement.clientWidth,clientHeight:document.documentElement.clientHeight,dpr:window.devicePixelRatio,outerWidth:window.outerWidth,outerHeight:window.outerHeight,screenWidth:screen.width,screenHeight:screen.height,availWidth:screen.availWidth,availHeight:screen.availHeight,visualWidth:window.visualViewport?.width??null,visualHeight:window.visualViewport?.height??null})";
        string? previous = null;
        var stable = 0;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string value;
        do
        {
            value = await Js(browser, script);
            stable = value == previous ? stable + 1 : 0;
            previous = value;
            if (stable >= 2) break;
            await Task.Delay(100);
        } while (DateTime.UtcNow < deadline);
        if (stable < 2) throw new TimeoutException("Viewport dimensions did not stabilize: " + scenario);
        using var metrics = JsonDocument.Parse(value);
        var width = metrics.RootElement.GetProperty("innerWidth").GetInt32();
        var height = metrics.RootElement.GetProperty("innerHeight").GetInt32();
        var responsive = await Js(browser, "matchMedia('(max-width:900px)').matches === (getComputedStyle(document.querySelector('#layout'),'::after').content.includes('mobile'))");
        Check(responsive == "True", "controlled responsive layout matches viewport (" + scenario + ")");
        Check(width > 0 && height > 0 && browser.Width > 0 && browser.Height > 0,
            "active viewport remains nonzero (" + scenario + ")");
        var bounds = browser.Bounds;
        var available = tabs.SelectedTab!.DisplayRectangle;
        if (enabled)
        {
            var expected = ViewportLetterboxing.Calculate(Math.Max(0, available.Width), Math.Max(0, available.Height));
            Check(bounds == new Rectangle(available.X + expected.X, available.Y + expected.Y, expected.Width, expected.Height),
                "letterboxed control matches centered geometry (" + scenario + ")");
        }
        else Check(bounds == available, "unletterboxed control fills available area (" + scenario + ")");
        Console.WriteLine("P4LB_CSS_VIEWPORT: " + JsonSerializer.Serialize(new
        {
            scenario, enabled, dpi = form.DeviceDpi, windowState = form.WindowState.ToString(), zoom = await browser.GetBrowserHost().GetZoomLevelAsync(),
            bounds = new { bounds.X, bounds.Y, bounds.Width, bounds.Height },
            available = new { available.X, available.Y, available.Width, available.Height },
            css = metrics.RootElement.Clone(),
            cssBucketed = width % ViewportLetterboxing.WidthStep == 0 && height % ViewportLetterboxing.HeightStep == 0
        }));
    }
}
