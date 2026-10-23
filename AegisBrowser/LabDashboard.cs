using AegisBrowser.Core;
using AegisBrowser.Lab;
using CefSharp;

namespace AegisBrowser;

internal sealed class LabDashboard : Form
{
    internal BrowserView Browser { get; }
    private readonly LabServer server;
    private readonly LabProfile profile;
    private readonly PrivacySession privacy;
    private readonly ComboBox scenario = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly TextBox rules = new() { Width = 250, Height = 55, Multiline = true, AcceptsReturn = true, PlaceholderText = "Custom network rules", Text = "||localhost^" };
    private readonly Label summary = new() { Dock = DockStyle.Top, AutoSize = true };
    private readonly DataGridView events = new() { Dock = DockStyle.Bottom, Height = 200, ReadOnly = true, AutoGenerateColumns = true, AllowUserToAddRows = false };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 500 };
    private bool closing, closed;
    internal LabDashboard(LabServer server, LabProfile profile)
    {
        this.server = server; this.profile = profile;
        privacy = new(LabProfiles.Configuration(profile));
        Text = "Aegis authorized testing lab — " + profile; Width = 1200; Height = 850; AutoScaleMode = AutoScaleMode.Dpi;
        var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        scenario.Items.AddRange(["Navigation", "Redirect", "Allow", "Challenge", "Deny", "Unavailable", "Disconnect", "TLS rejection", "DNS failure"]);
        scenario.SelectedItem = profile == LabProfile.FailureTesting ? "Unavailable" : "Navigation";
        tools.Controls.Add(new Label { Text = "Scenario", AutoSize = true }); tools.Controls.Add(scenario);
        rules.Enabled = profile == LabProfile.CustomRules; tools.Controls.Add(rules);
        var run = new Button { Text = "Run local scenario", AutoSize = true }; tools.Controls.Add(run);
        var export = new Button { Text = "Export sanitized JSON", AutoSize = true }; tools.Controls.Add(export);
        Browser = new BrowserView("about:blank") { Dock = DockStyle.Fill };
        Browser.RequestHandler = new LabRequests(server, privacy);
        Browser.PermissionHandler = new OriginPermissions(_ => { });
        Browser.DownloadHandler = new DenyDownloads(_ => { });
        Browser.LifeSpanHandler = new SafePopups(_ => { }, () => BeginInvoke((Action)(async () => {
            await Browser.ReleaseNativeHostAsync(); closed = true; Close();
        })), blockAllPopups: true);
        Browser.LoadError += (_, e) => { if (e.ErrorCode != CefErrorCode.Aborted) server.Report.Add(LabEventKind.NetworkFailure, e.FailedUrl, 0, e.ErrorCode.ToString()); };
        Browser.FrameLoadEnd += (_, e) => { if (e.Frame.IsMain) server.Report.Add(LabEventKind.Execution, e.Url, e.HttpStatusCode, "Finished"); };
        run.Click += (_, _) => {
            try { RunScenario(scenario.SelectedItem!.ToString()!); }
            catch (ArgumentException) { MessageBox.Show(this, "Invalid custom rules; active settings were preserved."); }
        };
        export.Click += (_, _) => {
            using var dialog = new SaveFileDialog { Filter = "JSON report|*.json", FileName = "aegis-lab-report.json" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { File.WriteAllText(dialog.FileName, server.Report.Export(profile)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(this, "Could not save report."); }
        };
        Controls.Add(Browser); Controls.Add(events); Controls.Add(summary); Controls.Add(tools);
        refresh.Tick += (_, _) => {
            var rows = server.Report.Snapshot();
            summary.Text = $"Profile: {profile} | Filtering: {privacy.Configuration.Mode} | Allowed: {rows.Count(e => e.Outcome == "Allowed")} | Blocked: {rows.Count(e => e.Outcome == "Blocked")} | Network failures: {rows.Count(e => e.Kind is LabEventKind.NetworkFailure or LabEventKind.TlsFailure)} | Mock: {server.Security.Decision}";
            events.DataSource = rows.Reverse().ToArray();
        };
        refresh.Start();
        FormClosing += (_, e) => {
            if (closed) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            if (Browser.IsBrowserInitialized) Browser.GetBrowserHost().CloseBrowser(true);
        };
        Browser.IsBrowserInitializedChanged += (_, _) => { if (closing && Browser.IsBrowserInitialized) Browser.GetBrowserHost().CloseBrowser(true); };
        FormClosed += (_, _) => { refresh.Stop(); refresh.Dispose(); };
    }
    internal void RunScenario(string name)
    {
        if (closing) return;
        if (profile == LabProfile.CustomRules) privacy.Apply(LabProfiles.Configuration(profile, rules.Text));
        server.Report.Add(LabEventKind.Execution, server.Origin, 0, "Started");
        var path = name switch { "Redirect" => "redirect", "Disconnect" => "disconnect", "Navigation" => "", _ => "scenario" };
        server.Security.Decision = name switch { "Allow" => SecurityDecision.Allow, "Challenge" => SecurityDecision.Challenge, "Unavailable" => SecurityDecision.Unavailable, _ => SecurityDecision.Deny };
        server.AllowDnsFailure = name == "DNS failure";
        Browser.Load(name == "TLS rejection" ? server.TlsOrigin : name == "DNS failure" ? LabServer.DnsFailureUrl : server.Origin + path);
    }
}
