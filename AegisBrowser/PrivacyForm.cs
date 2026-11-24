using AegisBrowser.Core;

namespace AegisBrowser;

internal sealed class PrivacyForm : Form
{
    private readonly PrivacySession session;
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly CheckBox customList = new() { Text = "Use tracking list in Custom", AutoSize = true };
    private readonly CheckBox unknownSiteRules = new() { Name = "UnknownSiteRules", Text = "Apply third-party rules for unknown sites", AutoSize = true };
    private readonly CheckBox blockThirdPartyCookies = new() { Name = "BlockThirdPartyCookies", Text = "Block third-party cookies", AutoSize = true };
    private readonly CheckBox restrictWebRtcUdp = new() { Name = "RestrictWebRtcNonProxiedUdp", Text = "Restrict non-proxied WebRTC UDP", AutoSize = true };
    private readonly CheckBox letterboxWebContent = new() { Name = "LetterboxWebContent", Text = "Letterbox the page viewport", AutoSize = true };
    private readonly CheckBox disableWebGl = new() { Name = "DisableWebGl", Text = "Restrict WebGL", AutoSize = true };
    private readonly CheckBox disableRemoteFonts = new() { Name = "DisableRemoteFonts", Text = "Block remote web fonts", AutoSize = true };
    private readonly TextBox blocks = Editor(), allows = Editor(), sites = Editor(), rules = Editor(), list = Editor(), unsupported = Editor();
    private readonly Label summary = new() { AutoSize = true };
    private readonly Label saveFeedback = new()
    {
        Dock = DockStyle.Bottom, AutoSize = true, AccessibleName = "Privacy settings messages",
        Padding = new Padding(12, 4, 12, 4), Visible = false
    };
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
        Text = "Privacy settings";
        DesktopStyle.Apply(this);
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new(900, 590);
        MinimumSize = new(570, 370);
        var pages = new TabControl { Dock = DockStyle.Fill };
        var settings = new TabPage("Settings") { BackColor = DesktopStyle.Surface };
        var rulePage = new TabPage("Site rules") { Padding = new Padding(8) };
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
        var controls = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(12)
        };
        mode.AccessibleName = "Filtering mode";
        controls.Controls.Add(new Label { Text = "Filtering mode", AutoSize = true });
        controls.Controls.AddRange([mode, customList, unknownSiteRules, blockThirdPartyCookies, restrictWebRtcUdp, letterboxWebContent,
            disableWebGl, disableRemoteFonts]);
        var policy = new Label { AutoSize = true,
            Text = "Current cookie policy: " + cookiePolicyStatus + ". Viewport letterboxing at startup: "
                + (letterboxingActive ? "On" : "Off") + ". WebGL restriction at startup: "
                + (webGlRestrictionActive ? "On" : "Off") + ". Remote web fonts restriction: "
                + (remoteFontRestrictionActive ? "On" : "Off") + ". WebRTC setting: " + webRtcPolicyStatus + ". Cookie, WebRTC, viewport, WebGL and font settings apply after restarting this profile. "
                + "Applying third-party rules for unknown sites may block content. Turning off cookie blocking restores Chromium's default, which may still block some cookies. "
                + "Blocking can affect embedded sign-in, payments and media. Restricting WebRTC UDP may break calls; TCP can still expose network addresses and this is not a firewall or proxy containment. Letterboxing adjusts the tab size for page zoom and may reduce available space. Display scaling and scrollbars can change the observed viewport; native popups are not letterboxed. It does not hide screen dimensions. WebGL restrictions can break 3D content and do not cover all OffscreenCanvas contexts in pages or workers. Disabling remote fonts may change typography and does not conceal installed local fonts. None of these options hides your IP or prevents all fingerprinting." };
        var pause = new CheckBox { Text = "Pause filtering for this session", AutoSize = true, Checked = session.Paused };
        pause.CheckedChanged += (_, _) => session.Paused = pause.Checked;
        controls.Controls.Add(pause);
        var host = SiteClassifier.Host(currentUrl);
        if (host is not null && host != "aegis-diagnostics.invalid")
            controls.Controls.Add(ActionButton("Add site exception: " + host, () => AddLine(sites, host)));
        controls.Controls.Add(policy);
        controls.ClientSizeChanged += (_, _) =>
        {
            var width = Math.Max(1, controls.ClientSize.Width - controls.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 8);
            foreach (Control control in controls.Controls)
                if (control is Label or CheckBox or Button) control.MaximumSize = new Size(width, 0);
        };
        blocks.Text = string.Join(Environment.NewLine, session.Configuration.BlockedDomains);
        allows.Text = string.Join(Environment.NewLine, session.Configuration.AllowedDomains);
        sites.Text = string.Join(Environment.NewLine, session.Configuration.SiteExceptions);
        rules.Text = session.Configuration.UserRules; list.Text = session.Configuration.FilterList;
        AddEditor(layout, "Blocked resource domains (one hostname per line)", blocks, 0, 0);
        AddEditor(layout, "Allowed resource domains (overrides blocks)", allows, 1, 0);
        AddEditor(layout, "Site exceptions (disable filtering on these sites)", sites, 0, 2);
        AddEditor(layout, "User network rules (delete lines to remove rules)", rules, 1, 2);
        rulePage.Controls.Add(layout);
        settings.Controls.Add(controls);
        var lists = new TabPage("Tracking list");
        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        listButtons.Controls.AddRange([ActionButton("Import UTF-8 list…", Import),
            ActionButton("Restore starter list", () => list.Text = PrivacyConfiguration.StarterList),
            ActionButton("Clear list", () => list.Clear()),
            new Label { Text = "Edits take effect with Save settings. No automatic downloads.", AutoSize = true }]);
        lists.Controls.Add(list); lists.Controls.Add(listButtons);
        var requests = new TabPage("Network inspector");
        var inspectorBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        inspectorBar.Controls.AddRange([thirdParty, ActionButton("Clear history and counts", session.ClearHistory),
            ActionButton("Block selected host", () => SelectedHost(blocks)), ActionButton("Allow selected host", () => SelectedHost(allows)), summary]);
        requests.Controls.Add(network); requests.Controls.Add(inspectorBar);
        requests.Controls.Add(new Label { Dock = DockStyle.Bottom, Height = 40,
            Text = "Last 500 decisions across this profile's tabs. Origins only; paths, queries, headers and bodies are not recorded. Counts are decisions, not completed downloads. Host edits require Save settings." });
        var rejected = new TabPage("Unsupported list rules"); unsupported.ReadOnly = true; rejected.Controls.Add(unsupported);
        pages.TabPages.AddRange([settings, rulePage, lists, requests, rejected]);
        var save = ActionButton("Save settings", Apply);
        DesktopStyle.Button(save, primary: true);
        var cancel = ActionButton("Close", Close);
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8)
        };
        footer.Controls.AddRange([cancel, save]);
        Controls.Add(pages);
        Controls.Add(saveFeedback);
        Controls.Add(footer);
        ClientSizeChanged += (_, _) => saveFeedback.MaximumSize = new Size(Math.Max(1, ClientSize.Width), 0);
        CancelButton = cancel;
        timer.Tick += (_, _) => RefreshInspector(); timer.Start();
        FormClosed += (_, _) => timer.Dispose();
        RefreshInspector();
    }
    private static TextBox Editor() => new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both,
        WordWrap = false, MaxLength = 8_000_000, AcceptsReturn = true };
    private static Button ActionButton(string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true };
        DesktopStyle.Button(button);
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
            saveFeedback.Text = $"Saved. Network rules apply to new requests. Restart this profile for cookie, WebRTC, viewport or fingerprint-surface settings. " +
                $"{session.RuleCount} active rules. {session.Unsupported.Count} unsupported rules skipped.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { saveFeedback.Text = "Settings were not saved. " + ex.Message; }
        saveFeedback.Visible = true;
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
