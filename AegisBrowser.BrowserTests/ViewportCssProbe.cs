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
                    if (phase == "viewport-write") await ExerciseStalledZoomQuery(enabledPath);
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
            // Chromium persists zoom by origin. The prior stress phase can
            // leave it changed, so establish a known starting level on restart.
            browser.GetBrowserHost().SetZoomLevel(0);
            await WaitForAsync(async () => Math.Abs(await browser.GetBrowserHost().GetZoomLevelAsync().WaitAsync(TimeSpan.FromSeconds(5))) < 0.01,
                "viewport initial zoom reset");
            await ReportViewport(form, tabs, browser, enabled, "initial");
            await CheckViewportFixture(browser);
            await MeasureScrollbarViewports(browser);

            await ResizeViewportAndWait(browser, () => form.Size = new Size(1023, 713));
            await ReportViewport(form, tabs, browser, enabled, "resize_1023x713");
            await ResizeViewportAndWait(browser, () => form.Size = new Size(1089, 751));
            await ReportViewport(form, tabs, browser, enabled, "resize_1089x751");
            await ResizeViewportAndWait(browser, () => form.Size = new Size(1099, 761));
            await ReportViewport(form, tabs, browser, enabled, "resize_1099x761");
            if (!enabled)
            {
                var original = int.Parse(await Js(browser, "window.innerWidth"));
                browser.GetBrowserHost().SetZoomLevel(1.0);
                await WaitForAsync(async () => Math.Abs(await browser.GetBrowserHost().GetZoomLevelAsync() - 1.0) < 0.01 &&
                    int.Parse(await Js(browser, "window.innerWidth")) != original, "unletterboxed zoom change");
                await ReportViewport(form, tabs, browser, false, "disabled_zoom_1");
                browser.GetBrowserHost().SetZoomLevel(0);
                await WaitForAsync(async () => Math.Abs(await browser.GetBrowserHost().GetZoomLevelAsync()) < 0.01 &&
                    int.Parse(await Js(browser, "window.innerWidth")) == original,
                    "unletterboxed zoom reset");
                return;
            }
            Button("+").PerformClick();
            await Wait(() => tabs.TabCount == 2, "viewport second tab");
            await Ready(Current());
            await LoadReady(Current(), Origin + "viewport-second");
            await ReportViewport(form, tabs, Current(), enabled, "new_tab");
            tabs.SelectedIndex = 0;
            await Ready(Current());
            await ReportViewport(form, tabs, Current(), enabled, "tab_switch");

            // The renderer measurement, not the WinForms bounds, is the privacy check.
            var widthBeforeZoom = int.Parse(await Js(browser, "window.innerWidth"));
            var zoomTimer = System.Diagnostics.Stopwatch.StartNew();
            browser.GetBrowserHost().SetZoomLevel(1.0);
            await WaitForAsync(async () => Math.Abs(await browser.GetBrowserHost().GetZoomLevelAsync() - 1.0) < 0.01 &&
                int.Parse(await Js(browser, "window.innerWidth")) != widthBeforeZoom,
                "viewport zoom change");
            await ReportViewport(form, tabs, browser, enabled, "zoom_1");
            Console.WriteLine("P4FP08_ZOOM_SETTLED_MS: " + zoomTimer.ElapsedMilliseconds);
            await CheckViewportFixture(browser);
            await ResizeViewportAndWait(browser, () => form.Size = new Size(1089, 751));
            await ReportViewport(form, tabs, browser, enabled, "zoom_1_resize");
            await LoadReady(browser, Origin + "viewport-zoomed-navigation");
            await ReportViewport(form, tabs, browser, enabled, "zoom_1_navigation");
            tabs.SelectedIndex = 1;
            await Ready(Current());
            tabs.SelectedIndex = 0;
            await ReportViewport(form, tabs, browser, enabled, "zoomed_tab_switch");
            var fractionalZoom = Math.Log(1.1) / Math.Log(1.2);
            browser.GetBrowserHost().SetZoomLevel(fractionalZoom);
            await WaitForAsync(async () => Math.Abs(await browser.GetBrowserHost().GetZoomLevelAsync() - fractionalZoom) < 0.01,
                "viewport fractional zoom change");
            await ReportViewport(form, tabs, browser, enabled, "zoom_110_percent");
            var widthAtFractionalZoom = int.Parse(await Js(browser, "window.innerWidth"));
            browser.GetBrowserHost().SetZoomLevel(-1.0);
            await WaitForAsync(async () => Math.Abs(await browser.GetBrowserHost().GetZoomLevelAsync() + 1.0) < 0.01 &&
                int.Parse(await Js(browser, "window.innerWidth")) != widthAtFractionalZoom,
                "viewport negative zoom change");
            await ReportViewport(form, tabs, browser, enabled, "zoom_minus_1");
            browser.GetBrowserHost().SetZoomLevel(-2.0);
            await WaitForAsync(async () => Math.Abs(await browser.GetBrowserHost().GetZoomLevelAsync().WaitAsync(TimeSpan.FromSeconds(5)) + 2.0) < 0.01,
                "viewport zoom minus two");
            await ReportViewport(form, tabs, browser, enabled, "zoom_minus_2");
            browser.GetBrowserHost().SetZoomLevel(2.0);
            await WaitForAsync(async () => Math.Abs(await browser.GetBrowserHost().GetZoomLevelAsync().WaitAsync(TimeSpan.FromSeconds(5)) - 2.0) < 0.01,
                "viewport zoom plus two");
            await ReportViewport(form, tabs, browser, enabled, "zoom_plus_2");
            var widthAtNegativeZoom = int.Parse(await Js(browser, "window.innerWidth"));
            browser.GetBrowserHost().SetZoomLevel(0);
            await WaitForAsync(async () => Math.Abs(await browser.GetBrowserHost().GetZoomLevelAsync()) < 0.01 &&
                int.Parse(await Js(browser, "window.innerWidth")) != widthAtNegativeZoom,
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
            // Exercise a real native tab close immediately after a zoom change.
            tabs.SelectedIndex = 1;
            var closingTab = Current();
            closingTab.GetBrowserHost().SetZoomLevel(1);
            Button("×").PerformClick();
            await Wait(() => tabs.TabCount == 1, "zooming tab native close");
            await Ready(Current());
            await ReportViewport(form, tabs, Current(), enabled, "after_tab_close");
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

    private static async Task MeasureScrollbarViewports(ChromiumWebBrowser browser)
    {
        foreach (var (name, x, y) in new[]
        {
            ("none", "hidden", "hidden"), ("vertical", "hidden", "scroll"),
            ("horizontal", "scroll", "hidden"), ("both", "scroll", "scroll")
        })
        {
            var result = await Js(browser, $$"""
                (()=>{const root=document.documentElement;
                root.style.overflowX='{{x}}';root.style.overflowY='{{y}}';
                return JSON.stringify({innerWidth,innerHeight,clientWidth:root.clientWidth,
                  clientHeight:root.clientHeight,visualWidth:visualViewport?.width??null,
                  visualHeight:visualViewport?.height??null,overflowX:getComputedStyle(root).overflowX,
                  overflowY:getComputedStyle(root).overflowY});})()
                """);
            using var metrics = JsonDocument.Parse(result);
            Check(metrics.RootElement.GetProperty("overflowX").GetString() == x &&
                metrics.RootElement.GetProperty("overflowY").GetString() == y,
                "controlled scrollbar style " + name);
            Console.WriteLine("P4FP08_SCROLLBAR: " + name + " " + result);
        }
        await Js(browser, "document.documentElement.style.overflowX='';document.documentElement.style.overflowY='';'restored'");
    }

    private static async Task ExerciseStalledZoomQuery(string profilePath)
    {
        var never = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        ChromiumWebBrowser? stalledBrowser = null;
        var stalledRequests = 0;
        var otherRequests = 0;
        Task<double> Query(ChromiumWebBrowser browser)
        {
            stalledBrowser ??= browser;
            if (browser == stalledBrowser) { stalledRequests++; return never.Task; }
            otherRequests++;
            return browser.GetBrowserHost().GetZoomLevelAsync();
        }
        var form = new MainForm("viewport-stalled-query", profilePath, Query);
        try
        {
            form.Show();
            var tabs = form.Controls.OfType<TabControl>().Single();
            var toolbar = form.Controls.OfType<TableLayoutPanel>().Single();
            Button Button(string label) => toolbar.Controls.OfType<Button>().Single(b => b.Text == label);
            ChromiumWebBrowser Current() => tabs.SelectedTab!.Controls.OfType<ChromiumWebBrowser>().Single();
            await Wait(() => tabs.TabCount == 1 && stalledRequests == 1, "first zoom query started");
            var first = Current();
            await Ready(first);
            await Wait(() => form.Controls.OfType<StatusStrip>().Single().Items.OfType<ToolStripStatusLabel>()
                .Any(label => label.Text?.Contains("Page zoom query timed out", StringComparison.Ordinal) == true), "stalled zoom timeout notice");
            Check(stalledRequests == 1, "stalled query is not repeatedly queued");
            await LoadReady(first, Origin + "viewport-stalled-navigation");
            Check(stalledRequests == 1, "navigation does not multiply a stalled query");
            Button("+").PerformClick();
            await Wait(() => tabs.TabCount == 2, "open tab while zoom query stalled");
            var second = Current();
            await Ready(second);
            await LoadReady(second, Origin + "viewport-unstalled");
            await Wait(() => otherRequests > 0, "independent tab zoom query");
            await ReportViewport(form, tabs, second, true, "other_tab_while_stalled");
            tabs.SelectedIndex = 0;
            Button("×").PerformClick();
            await Wait(() => tabs.TabCount == 1, "stalled tab closes without completing query");
            never.TrySetResult(5);
            await Ready(Current());
            await ReportViewport(form, tabs, Current(), true, "stale_completion_after_tab_close");
            Check(stalledRequests == 1, "stale completion did not restart closed tab queries");
        }
        finally
        {
            if (!form.IsDisposed) { form.Close(); await Wait(() => form.IsDisposed, "stalled-query form native close"); }
        }

        var pending = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = false;
        var closing = new MainForm("viewport-close-pending", profilePath, _ => { requested = true; return pending.Task; });
        closing.Show();
        await Wait(() => requested, "shutdown test query started");
        closing.Close();
        await Wait(() => closing.IsDisposed, "window closes with pending zoom query");
        pending.TrySetResult(5);
        Check(closing.IsDisposed, "late completion does not reopen a closed window");
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
        if (enabled)
        {
            await WaitForAsync(async () =>
            {
                var area = tabs.SelectedTab!.DisplayRectangle;
                var level = await browser.GetBrowserHost().GetZoomLevelAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var target = ViewportLetterboxing.Calculate(Math.Max(0, area.Width), Math.Max(0, area.Height), level);
                return browser.Bounds == new Rectangle(area.X + target.X, area.Y + target.Y, target.Width, target.Height);
            }, "zoom-aware layout " + scenario);
        }
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
        var zoom = await browser.GetBrowserHost().GetZoomLevelAsync();
        var factor = Math.Pow(1.2, zoom);
        if (enabled)
        {
            var expected = ViewportLetterboxing.Calculate(Math.Max(0, available.Width), Math.Max(0, available.Height), zoom);
            Check(bounds == new Rectangle(available.X + expected.X, available.Y + expected.Y, expected.Width, expected.Height),
                "letterboxed control matches centered geometry (" + scenario + ")");
            // The fixture is independently checked in CSS pixels when the
            // observed renderer scale matches the zoom factor. Other DPI
            // configurations remain measured, not assumed equivalent.
            if (Math.Abs(metrics.RootElement.GetProperty("dpr").GetDouble() - factor) < 0.02)
            {
                static int Distance(int value, int step) => Math.Min(value % step, step - value % step);
                if (available.Width / factor >= ViewportLetterboxing.WidthStep)
                    Check(Distance(width, ViewportLetterboxing.WidthStep) <= 1, "CSS width near zoom bucket (" + scenario + ")");
                if (available.Height / factor >= ViewportLetterboxing.HeightStep)
                    Check(Distance(height, ViewportLetterboxing.HeightStep) <= 1, "CSS height near zoom bucket (" + scenario + ")");
                if (scenario == "zoom_1" && available.Width / factor >= ViewportLetterboxing.WidthStep &&
                    available.Height / factor >= ViewportLetterboxing.HeightStep)
                    Console.WriteLine("P4FP07_CSS_BUCKET: VERIFIED zoom_1 (inner viewport, observed DPR)");
                if (scenario == "zoom_minus_1" && available.Height / factor >= ViewportLetterboxing.HeightStep)
                {
                    var targetHeight = (int)Math.Floor(available.Height / factor / ViewportLetterboxing.HeightStep) * ViewportLetterboxing.HeightStep;
                    Check(height == targetHeight, "negative zoom CSS height reaches target bucket");
                    Console.WriteLine("P4FP08_ROUNDING: VERIFIED zoom_minus_1 css_height=" + height);
                }
            }
            else Console.WriteLine("P4FP07_CSS_BUCKET: INCONCLUSIVE (DPR differs from zoom factor; " + scenario + ")");
        }
        else Check(bounds == available, "unletterboxed control fills available area (" + scenario + ")");
        Console.WriteLine("P4LB_CSS_VIEWPORT: " + JsonSerializer.Serialize(new
        {
            scenario, enabled, dpi = form.DeviceDpi, windowState = form.WindowState.ToString(), zoom,
            bounds = new { bounds.X, bounds.Y, bounds.Width, bounds.Height },
            available = new { available.X, available.Y, available.Width, available.Height },
            css = metrics.RootElement.Clone(),
            cssBucketed = width % ViewportLetterboxing.WidthStep == 0 && height % ViewportLetterboxing.HeightStep == 0
        }));
    }
}
