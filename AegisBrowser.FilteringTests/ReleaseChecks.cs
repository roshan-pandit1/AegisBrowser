using AegisBrowser;
using AegisBrowser.Core;
using CefSharp;
using CefSharp.WinForms;

internal static partial class Program
{
    private static async Task NavigationDiagnosticReview(Form host, FixtureServer server)
    {
        var trace = new NavigationDiagnostics { Enabled = true };
        var privacy = new PrivacySession(new() { Mode = PrivacyMode.Strict });
        using var web = new BrowserView("about:blank") { LifeSpanHandler = new SafePopups(Console.WriteLine), Dock = DockStyle.Fill };
        web.RequestHandler = new SafeRequests(Console.WriteLine, privacy.Evaluate, diagnostics: trace);
        var attachment = new BrowserDiagnosticAttachment(web, trace);
        host.Controls.Add(web);
        try
        {
            await Ready(web);
            web.Load(server.Origin + "/security-redirect");
            await Wait(async () => await Js(web, "document.title") == "Mock provider result", "provider result rendered");
            await Wait(() => Task.FromResult(trace.Snapshot().Any(e => e.Kind == DiagnosticKind.MainDocumentLoaded && e.Status == 200)), "main document diagnostic");
            Check((await Js(web, "document.body.textContent")).Contains("SECURITY"), "provider rejection rendered unchanged with filtering enabled");
            Check(trace.Snapshot().Any(e => e.Kind == DiagnosticKind.Redirect && e.Status == 302), "redirect status observed");
            Check(!trace.Export().Contains("syntheticParticipantSecret"), "native redirect diagnostic redacts participant");
            attachment.Dispose(); attachment.Dispose();
            trace.Clear();
            await Js(web, "console.error('syntheticParticipantSecret'); true");
            Check(!trace.Snapshot().Any(e => e.Kind == DiagnosticKind.ConsoleError), "disposed attachment does not collect console errors");
            attachment = new BrowserDiagnosticAttachment(web, trace);
            await Js(web, "console.error('syntheticParticipantSecret'); true");
            await Wait(() => Task.FromResult(trace.Snapshot().Count(e => e.Kind == DiagnosticKind.ConsoleError) == 1), "single reattached console observation");
            Check(!trace.Export().Contains("syntheticParticipantSecret"), "console payload not exported");
            trace.Enabled = false;
            await Js(web, "console.error('syntheticParticipantSecret'); true");
            Check(trace.Snapshot().Length == 0, "disabled native diagnostics collect nothing");
        }
        finally { attachment.Dispose(); await CloseWeb(web); }
    }

    private static IEnumerable<Control> All(Control parent)
    {
        foreach (Control child in parent.Controls) { yield return child; foreach (var nested in All(child)) yield return nested; }
    }
    private static T Named<T>(Control form, string name) where T : Control => All(form).OfType<T>().Single(c => c.Name == name);
    private static async Task<string> Js(ChromiumWebBrowser web, string code)
    {
        var result = await web.EvaluateScriptAsync(code).WaitAsync(TimeSpan.FromSeconds(5));
        return result.Success ? result.Result?.ToString() ?? "" : "";
    }
    private static async Task Ready(ChromiumWebBrowser web) => await Wait(async () =>
    {
        if (!web.IsBrowserInitialized || !web.CanExecuteJavascriptInMainFrame || web.IsLoading) return false;
        try { return await Js(web, "document.readyState") == "complete"; } catch { return false; }
    }, "rendered document ready");
    private static async Task CloseWeb(ChromiumWebBrowser web)
    {
        var life = (SafePopups)web.LifeSpanHandler;
        life.ClosePopups(); web.GetBrowserHost().CloseBrowser(true);
        await life.OwnerCloseReady.WaitAsync(TimeSpan.FromSeconds(10));
        await ((BrowserView)web).ReleaseNativeHostAsync();
        web.Dispose();
    }
    private static async Task RestartProfile(Form host, string root, bool write)
    {
        using var form = new MainForm("restart", root); form.Show(host);
        var tabs = All(form).OfType<TabControl>().Single();
        await Wait(() => Task.FromResult(tabs.TabCount == 1), "startup tab");
        await Ready(tabs.SelectedTab!.Controls.OfType<ChromiumWebBrowser>().Single());
        All(form).OfType<Button>().Single(c => c.Text == "Privacy").PerformClick();
        var settings = form.OwnedForms.OfType<PrivacyForm>().Single();
        var mode = Named<ComboBox>(settings, "PrivacyMode"); var rules = Named<TextBox>(settings, "UserRules");
        var pause = All(settings).OfType<CheckBox>().Single(c => c.Text.StartsWith("Pause filtering"));
        if (write)
        {
            mode.SelectedItem = "Strict"; rules.Text = "||persisted.test^";
            Named<TextBox>(settings, "SiteExceptions").Text = "example.com";
            settings.ApplyEdits(); pause.Checked = true;
            Check(File.Exists(Path.Combine(root, "privacy.json")), "browser settings saved to profile");
        }
        else
        {
            Check((string?)mode.SelectedItem == "Strict" && rules.Text == "||persisted.test^"
                && Named<TextBox>(settings, "SiteExceptions").Text == "example.com", "settings survive actual browser process restart");
            Check(!pause.Checked && Named<DataGridView>(settings, "Requests").Rows.Count <= 1, "restart clears pause and previous request history");
        }
        settings.Close();
        All(form).OfType<Button>().Single(c => c.Text == "Search").PerformClick();
        var search = form.OwnedForms.OfType<SearchSettingsForm>().Single();
        if (write)
        {
            Named<ComboBox>(search, "SearchEngine").SelectedItem = SearchEngines.Get("brave");
            search.SaveSelection();
            Check(form.SearchEngineId == "brave" && File.Exists(Path.Combine(root, "search.json")), "search selection saved through settings UI");
        }
        else Check(form.SearchEngineId == "brave" && ((SearchEngine)Named<ComboBox>(search, "SearchEngine").SelectedItem!).Id == "brave", "search selection survives browser process restart");
        search.Close(); form.Close();
        await Wait(() => Task.FromResult(form.IsDisposed), "asynchronous window shutdown");
        Check(form.IsDisposed, "main window normal shutdown");
    }
    private static async Task SettingsReview(Form host, FixtureServer server)
    {
        var session = new PrivacySession();
        using (var settings = new PrivacyForm(session, server.Origin))
        {
            settings.Show(host); var file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, "||tracker.test^\nexample.com##.ad"); settings.ImportFile(file);
                Named<ComboBox>(settings, "PrivacyMode").SelectedItem = "Standard"; settings.ApplyEdits();
                Check(session.RuleCount == 1 && session.Unsupported.Count == 1, "list import and unsupported syntax report");
                Named<ComboBox>(settings, "PrivacyMode").SelectedItem = "Custom";
                Named<TextBox>(settings, "UserRules").Text = "||custom.test^"; settings.ApplyEdits();
                var request = new NetworkRequest("https://custom.test/a", "https://example.com", "script");
                Check(session.Evaluate(request).Blocked, "custom rule applied from settings controls");
                Named<TextBox>(settings, "UserRules").Clear(); settings.ApplyEdits();
                Check(!session.Evaluate(request).Blocked, "custom rule removed from settings controls");
                Named<TextBox>(settings, "BlockedDomains").Text = "https://invalid/path";
                var rejected = false; try { settings.ApplyEdits(); } catch (ArgumentException) { rejected = true; }
                Check(rejected && !session.Evaluate(request).Blocked, "invalid edit preserves active settings");
                All(settings).OfType<TabControl>().Single().SelectedIndex = 2;
                session.Evaluate(request); session.Evaluate(request with { Url = "https://other.test/b" }); settings.RefreshInspector();
                var grid = Named<DataGridView>(settings, "Requests"); grid.CurrentCell = grid.Rows[1].Cells[0];
                var number = ((RequestObservation)grid.CurrentRow!.DataBoundItem).Number;
                session.Evaluate(request); settings.RefreshInspector();
                Check(((RequestObservation)grid.CurrentRow!.DataBoundItem).Number == number, "inspector preserves selected request across refresh");
            }
            finally { File.Delete(file); settings.Close(); }
        }
        using var web = new BrowserView(server.Origin + "/landing") { LifeSpanHandler = new SafePopups(Console.WriteLine), Dock = DockStyle.Fill };
        web.RequestHandler = new SafeRequests(Console.WriteLine, session.Evaluate);
        var downloadDenied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        web.DownloadHandler = new DenyDownloads(_ => downloadDenied.TrySetResult(true));
        web.FrameLoadEnd += (_, _) => { }; host.Controls.Add(web); await Ready(web);
        await Js(web, $"window.corsResult='pending'; fetch('{server.ThirdOrigin}/landing').then(()=>window.corsResult='allowed').catch(()=>window.corsResult='denied');");
        await Wait(async () => await Js(web, "window.corsResult") != "pending", "cross-origin fetch outcome");
        Check(await Js(web, "window.corsResult") == "denied", "Chromium denies cross-origin response without CORS permission");
        await Js(web, "window.diagnosticsFetch='pending'; fetch('https://aegis-diagnostics.invalid/').then(()=>window.diagnosticsFetch='allowed').catch(()=>window.diagnosticsFetch='denied');");
        await Wait(async () => await Js(web, "window.diagnosticsFetch") != "pending", "diagnostics cross-origin fetch outcome");
        Check(await Js(web, "window.diagnosticsFetch") == "denied", "web content cannot fetch internal diagnostics");
        await Js(web, "const f=document.createElement('iframe'); f.src='https://aegis-diagnostics.invalid/'; document.body.append(f); window.diagFrame=f; 'ok'");
        await Task.Delay(200);
        Check(await Js(web, "(()=>{try{return !!window.diagFrame.contentDocument?.querySelector('#result')}catch{return false}})()") == "False", "external frame cannot read diagnostics report");
        await Js(web, $"const a=document.createElement('a'); a.href='{server.Origin}/download'; document.body.append(a); a.click(); 'requested'");
        Check(await downloadDenied.Task.WaitAsync(TimeSpan.FromSeconds(10)), "production download policy refuses local attachment");
        await CloseWeb(web);
        host.Controls.Remove(web);
    }
}
