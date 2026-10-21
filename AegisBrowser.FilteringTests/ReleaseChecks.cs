using AegisBrowser;
using AegisBrowser.Core;
using CefSharp;
using CefSharp.WinForms;

internal static partial class Program
{
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
        settings.Close(); form.Close(); Check(form.IsDisposed, "main window normal shutdown");
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
        using var web = new ChromiumWebBrowser(server.Origin + "/landing") { LifeSpanHandler = new CefSharp.Handler.LifeSpanHandler(), Dock = DockStyle.Fill };
        web.RequestHandler = new SafeRequests(Console.WriteLine, session.Evaluate);
        web.FrameLoadEnd += (_, _) => { }; host.Controls.Add(web); await Ready(web);
        await Js(web, $"window.corsResult='pending'; fetch('{server.ThirdOrigin}/landing').then(()=>window.corsResult='allowed').catch(()=>window.corsResult='denied');");
        await Wait(async () => await Js(web, "window.corsResult") != "pending", "cross-origin fetch outcome");
        Check(await Js(web, "window.corsResult") == "denied", "Chromium denies cross-origin response without CORS permission");
        host.Controls.Remove(web);
    }
}
