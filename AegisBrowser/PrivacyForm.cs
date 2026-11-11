using AegisBrowser.Core;

namespace AegisBrowser;

internal sealed class PrivacyForm : Form
{
    private readonly PrivacySession session;
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly CheckBox customList = new() { Text = "Use tracking list in Custom", AutoSize = true };
    private readonly CheckBox unknownSiteRules = new() { Name = "UnknownSiteRules", Text = "Apply third-party rules when site is unknown (may block content)", AutoSize = true };
    private readonly CheckBox blockThirdPartyCookies = new() { Name = "BlockThirdPartyCookies", Text = "Block third-party cookies (restart profile to apply)", AutoSize = true };
    private readonly CheckBox restrictWebRtcUdp = new() { Name = "RestrictWebRtcNonProxiedUdp", Text = "Restrict non-proxied WebRTC UDP (restart; calls may fail)", AutoSize = true };
    private readonly CheckBox letterboxWebContent = new() { Name = "LetterboxWebContent", Text = "Letterbox page viewport (restart profile; may reduce available space)", AutoSize = true };
    private readonly CheckBox disableWebGl = new() { Name = "DisableWebGl", Text = "Restrict WebGL (restart; 3D graphics and some sites may fail)", AutoSize = true };
    private readonly CheckBox disableRemoteFonts = new() { Name = "DisableRemoteFonts", Text = "Block remote web fonts (restart; typography may change)", AutoSize = true };
    private readonly TextBox blocks = Editor(), allows = Editor(), sites = Editor(), rules = Editor(), list = Editor(), unsupported = Editor();
    private readonly Label summary = new() { AutoSize = true };
    private readonly DataGridView network = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly CheckBox thirdParty = new() { Text = "Third-party only", AutoSize = true };
    private readonly Action<PrivacyConfiguration> apply;

    public PrivacyForm(PrivacySession session, string? currentUrl, Action<PrivacyConfiguration>? apply = null,
        string cookiePolicyStatus = "Existing Chromium setting (no Aegis override)", bool letterboxingActive = false,
        bool webGlRestrictionActive = false, bool remoteFontRestrictionActive = false,
        string webRtcPolicyStatus = "Existing Chromium setting (no Aegis override)")
    {
        this.session = session;
        mode.Name = "PrivacyMode"; blocks.Name = "BlockedDomains"; allows.Name = "AllowedDomains";
        sites.Name = "SiteExceptions"; rules.Name = "UserRules"; list.Name = "TrackingList"; network.Name = "Requests";
        this.apply = apply ?? (configuration => session.Apply(configuration));
        Text = "Aegis Privacy — profile settings and network inspector";
        Width = 1100; Height = 760; MinimumSize = new(800, 550);
        var pages = new TabControl { Dock = DockStyle.Fill };
        var settings = new TabPage("Settings and rules");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 50)); layout.ColumnStyles.Add(new(SizeType.Percent, 50));
        for (var i = 0; i < 4; i++) layout.RowStyles.Add(new(SizeType.Percent, 25));
        mode.Items.AddRange(Enum.GetNames<PrivacyMode>()); mode.SelectedItem = session.Configuration.Mode.ToString();
        customList.Checked = session.Configuration.UseListsInCustom;
        unknownSiteRules.Checked = session.Configuration.ApplyThirdPartyRulesWhenSiteUnknown;
        blockThirdPartyCookies.Checked = session.Configuration.BlockThirdPartyCookies == true;
        restrictWebRtcUdp.Checked = session.Configuration.RestrictWebRtcNonProxiedUdp == true;
        letterboxWebContent.Checked = session.Configuration.LetterboxWebContent;
        disableWebGl.Checked = session.Configuration.DisableWebGl;
        disableRemoteFonts.Checked = session.Configuration.DisableRemoteFonts;
        var controls = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        controls.Controls.AddRange([mode, customList, unknownSiteRules, blockThirdPartyCookies, restrictWebRtcUdp, letterboxWebContent,
            disableWebGl, disableRemoteFonts, Button("Apply settings", Apply)]);
        controls.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(1000, 0),
            Text = "Current cookie policy: " + cookiePolicyStatus + ". Viewport letterboxing at startup: "
                + (letterboxingActive ? "On" : "Off") + ". WebGL restriction at startup: "
                + (webGlRestrictionActive ? "On" : "Off") + ". Remote web fonts restriction: "
                + (remoteFontRestrictionActive ? "On" : "Off") + ". WebRTC setting: " + webRtcPolicyStatus + ". Changes apply after restarting this profile. "
                + "Turning this off restores Chromium's default, which may still block some cookies. "
                + "Blocking can affect embedded sign-in, payments and media. Restricting WebRTC UDP may break calls; TCP can still expose network addresses and this is not a firewall or proxy containment. Letterboxing sizes the browser control; page zoom and display scaling can change the CSS viewport. WebGL restrictions can break 3D content and may not apply to workers. Disabling remote fonts does not conceal installed local fonts. None of these options hides your IP or prevents all fingerprinting." });
        var pause = new CheckBox { Text = "Pause filtering until resumed or browser exit", AutoSize = true, Checked = session.Paused };
        pause.CheckedChanged += (_, _) => session.Paused = pause.Checked;
        controls.Controls.Add(pause);
        var host = SiteClassifier.Host(currentUrl);
        if (host is not null && host != "aegis-diagnostics.invalid")
            controls.Controls.Add(Button("Add site exception: " + host, () => AddLine(sites, host)));
        blocks.Text = string.Join(Environment.NewLine, session.Configuration.BlockedDomains);
        allows.Text = string.Join(Environment.NewLine, session.Configuration.AllowedDomains);
        sites.Text = string.Join(Environment.NewLine, session.Configuration.SiteExceptions);
        rules.Text = session.Configuration.UserRules; list.Text = session.Configuration.FilterList;
        AddEditor(layout, "Blocked resource domains (one hostname per line)", blocks, 0, 0);
        AddEditor(layout, "Allowed resource domains (overrides blocks)", allows, 1, 0);
        AddEditor(layout, "Site exceptions (disable filtering on these sites)", sites, 0, 2);
        AddEditor(layout, "User network rules (delete lines to remove rules)", rules, 1, 2);
        settings.Controls.Add(layout); settings.Controls.Add(controls);
        var lists = new TabPage("Tracking list");
        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        listButtons.Controls.AddRange([Button("Import UTF-8 list…", Import),
            Button("Restore starter list", () => list.Text = PrivacyConfiguration.StarterList),
            Button("Clear list", () => list.Clear()),
            new Label { Text = "Edits take effect with Apply settings. No automatic downloads.", AutoSize = true }]);
        lists.Controls.Add(list); lists.Controls.Add(listButtons);
        var requests = new TabPage("Network inspector");
        var inspectorBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        inspectorBar.Controls.AddRange([thirdParty, Button("Clear history and counts", session.ClearHistory),
            Button("Block selected host", () => SelectedHost(blocks)), Button("Allow selected host", () => SelectedHost(allows)), summary]);
        requests.Controls.Add(network); requests.Controls.Add(inspectorBar);
        requests.Controls.Add(new Label { Dock = DockStyle.Bottom, Height = 40,
            Text = "Last 500 decisions across this profile's tabs. Origins only; paths, queries, headers and bodies are not recorded. Counts are decisions, not completed downloads. Host edits require Apply settings." });
        var rejected = new TabPage("Unsupported list rules"); unsupported.ReadOnly = true; rejected.Controls.Add(unsupported);
        pages.TabPages.AddRange([settings, lists, requests, rejected]); Controls.Add(pages);
        timer.Tick += (_, _) => RefreshInspector(); timer.Start();
        FormClosed += (_, _) => timer.Dispose();
        RefreshInspector();
    }
    private static TextBox Editor() => new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both,
        WordWrap = false, MaxLength = 8_000_000, AcceptsReturn = true };
    private static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) => action(); return button;
    }
    private static void AddEditor(TableLayoutPanel layout, string label, TextBox editor, int col, int row)
    {
        var group = new GroupBox { Text = label, Dock = DockStyle.Fill }; group.Controls.Add(editor);
        layout.Controls.Add(group, col, row); layout.SetRowSpan(group, 2);
    }
    private static void AddLine(TextBox box, string line)
    {
        if (!box.Lines.Contains(line)) box.AppendText((box.Text.Length == 0 ? "" : Environment.NewLine) + line);
    }
    private void SelectedHost(TextBox target)
    {
        if (network.CurrentRow?.DataBoundItem is RequestObservation row && SiteClassifier.Host(row.Destination) is { } host) AddLine(target, host);
    }
    internal void ImportFile(string path)
    {
        if (new FileInfo(path).Length > 8_000_000) throw new IOException("List exceeds 8 MB.");
        list.Text = File.ReadAllText(path, new System.Text.UTF8Encoding(false, true));
    }
    private void Import()
    {
        using var dialog = new OpenFileDialog { Filter = "Text filter lists|*.txt|All files|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            ImportFile(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        { MessageBox.Show(this, ex.Message, "Could not import list"); }
    }
    internal void ApplyEdits()
    {
            string[] Domains(TextBox box) => box.Lines.Where(s => !string.IsNullOrWhiteSpace(s)).Select(NetworkFilterList.ValidateDomain).Distinct().ToArray();
            var previousCookies = session.Configuration.BlockThirdPartyCookies;
            var previousWebRtc = session.Configuration.RestrictWebRtcNonProxiedUdp;
            var config = new PrivacyConfiguration { Mode = Enum.Parse<PrivacyMode>((string)mode.SelectedItem!),
                UseListsInCustom = customList.Checked, ApplyThirdPartyRulesWhenSiteUnknown = unknownSiteRules.Checked,
                BlockThirdPartyCookies = blockThirdPartyCookies.Checked ? true : previousCookies == true ? false : previousCookies,
                RestrictWebRtcNonProxiedUdp = restrictWebRtcUdp.Checked ? true : previousWebRtc == true ? false : previousWebRtc,
                LetterboxWebContent = letterboxWebContent.Checked,
                DisableWebGl = disableWebGl.Checked, DisableRemoteFonts = disableRemoteFonts.Checked,
                FilterList = list.Text, UserRules = rules.Text,
                BlockedDomains = Domains(blocks), AllowedDomains = Domains(allows), SiteExceptions = Domains(sites) };
            apply(config);
    }
    private void Apply()
    {
        try
        {
            ApplyEdits();
            RefreshInspector();
            MessageBox.Show(this, $"Network rules apply to new requests. Restart this profile for cookie, WebRTC, viewport or fingerprint-surface settings.\n{session.RuleCount} active rules; {session.Unsupported.Count} unsupported rules skipped.", "Privacy settings");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { MessageBox.Show(this, ex.Message, "Settings were not applied"); }
    }
    internal void RefreshInspector()
    {
        var snapshot = session.Snapshot();
        summary.Text = $"Allowed: {snapshot.Allowed}   Blocked: {snapshot.Blocked}";
        var selected = (network.CurrentRow?.DataBoundItem as RequestObservation)?.Number;
        network.DataSource = snapshot.Requests.Reverse().Where(r => !thirdParty.Checked || r.Party == "Third-party").ToArray();
        network.ClearSelection(); network.CurrentCell = null;
        if (selected.HasValue)
            foreach (DataGridViewRow row in network.Rows)
                if (row.DataBoundItem is RequestObservation item && item.Number == selected)
                { network.CurrentCell = row.Cells[0]; row.Selected = true; break; }
        var report = string.Join(Environment.NewLine, session.Unsupported);
        if (unsupported.Text != report) unsupported.Text = report;
    }
}
