using AegisBrowser.Core;

namespace AegisBrowser;

/// <summary>Edits saved proxy intent. Runtime networking is deliberately unavailable.</summary>
internal sealed class ProxySettingsForm : Form
{
    private readonly IProxyConfigurationEditor editor;
    private readonly ProfileDefinition profile;
    private readonly ProxyConfiguration? original;
    private readonly Func<string, bool> confirm;
    private readonly IProxySavedDiagnostics? diagnostics;
    private readonly ComboBox protocol = new() { Name = "ProxyProtocol", AccessibleName = "Saved proxy mode", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox host = new() { Name = "ProxyHost", AccessibleName = "Proxy hostname or IP address", Dock = DockStyle.Fill };
    private readonly NumericUpDown port = new() { Name = "ProxyPort", AccessibleName = "Proxy port", Minimum = 1, Maximum = 65535, Value = 8080, Dock = DockStyle.Fill };
    private readonly TextBox label = new() { Name = "ProxyLabel", AccessibleName = "Proxy label", Dock = DockStyle.Fill, MaxLength = 120 };
    private readonly CheckBox authentication = new() { Name = "ProxyAuthentication", AccessibleName = "Proxy requires authentication", Text = "Requires username and password", AutoSize = true };
    private readonly TextBox username = new() { Name = "ProxyUsername", AccessibleName = "New proxy username", Dock = DockStyle.Fill, MaxLength = 512 };
    private readonly TextBox password = new() { Name = "ProxyPassword", AccessibleName = "New proxy password", Dock = DockStyle.Fill, MaxLength = 4096, UseSystemPasswordChar = true };
    private readonly Label credentialHint = new() { Name = "ProxyCredentialHint", AccessibleName = "Credential status", AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label feedback = new() { Name = "ProxyFeedback", AccessibleName = "Proxy settings messages", AutoSize = true, Dock = DockStyle.Fill };
    private readonly Button save = new() { Name = "SaveProxyDraft", Text = "Save", AccessibleName = "Save proxy draft", AutoSize = true };
    private readonly Button cancel = new() { Name = "CancelProxyDraft", Text = "Cancel", AccessibleName = "Cancel proxy editing", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly Button clearCredentials = new() { Name = "ClearProxyCredentials", Text = "Clear credentials on save", AccessibleName = "Clear proxy credentials on save", AutoSize = true };
    private readonly TextBox diagnosticEndpoint = new() { Name = "ProxyDiagnosticEndpoint", AccessibleName = "Trusted diagnostic endpoint URL", Dock = DockStyle.Fill, MaxLength = 2048, PlaceholderText = "https://your-trusted-endpoint/ip" };
    private readonly Button test = new() { Name = "TestSavedProxy", Text = "Test saved draft", AccessibleName = "Test saved proxy draft using .NET transport", AutoSize = true };
    private readonly Button cancelTest = new() { Name = "CancelProxyTest", Text = "Cancel test", AccessibleName = "Cancel proxy transport test", AutoSize = true, Enabled = false };
    private readonly Label testResult = new() { Name = "ProxyTestResult", AccessibleName = "Proxy transport test result", AutoSize = true, Dock = DockStyle.Fill };
    private CancellationTokenSource? testCancellation;
    private bool saving, testing;
    public string? SavedMessage { get; private set; }

    public ProxySettingsForm(IProxyConfigurationEditor editor, ProfileDefinition profile, ProxyConfiguration? original,
        Func<string, bool>? confirm = null, IProxySavedDiagnostics? diagnostics = null)
    {
        this.editor = editor; this.profile = profile; this.original = original;
        this.diagnostics = diagnostics;
        this.confirm = confirm ?? (message => MessageBox.Show(this, message, "Proxy settings", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes);
        Text = "Proxy settings — saved draft"; AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
        Width = 760; Height = 850; MinimumSize = new Size(550, 570);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 16, Padding = new Padding(16), AutoScroll = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 16; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var warning = new Label
        {
            Name = "ProxyAvailability", AccessibleName = "Proxy networking availability", Dock = DockStyle.Fill, AutoSize = true,
            Text = "Proxy networking is not available in this build. A saved proxy draft blocks this profile from starting. " +
                   "The optional transport test below does not activate a browser proxy."
        };
        layout.Controls.Add(warning, 0, 0); layout.SetColumnSpan(warning, 2);
        void Field(string text, Control control, int row)
        {
            layout.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 4) }, 0, row);
            control.Margin = new Padding(0, 4, 0, 4); layout.Controls.Add(control, 1, row);
        }
        protocol.Items.AddRange(["No saved proxy (browser defaults)", "HTTP — draft only", "HTTPS — draft only", "SOCKS5 — draft only"]);
        Field("Mode", protocol, 1); Field("Host", host, 2); Field("Port", port, 3); Field("Label", label, 4);
        layout.Controls.Add(authentication, 1, 5); Field("Username", username, 6); Field("Password", password, 7);
        layout.Controls.Add(credentialHint, 1, 8);
        clearCredentials.Click += (_, _) => { authentication.Checked = false; username.Clear(); password.Clear(); };
        layout.Controls.Add(clearCredentials, 1, 9);
        var diagnosticWarning = new Label
        {
            Name = "ProxyTestWarning", AccessibleName = "Proxy test limitations", Dock = DockStyle.Fill, AutoSize = true,
            Text = "Test the saved revision with .NET transport only; unsaved edits are ignored. This does not test Chromium, DNS or WebRTC or prove isolation. " +
                   "Your chosen endpoint receives a request. HTTP proxy Basic credentials are exposed on the proxy network hop. " +
                   "Close the profile first; it cannot start while the test runs."
        };
        layout.Controls.Add(diagnosticWarning, 0, 10); layout.SetColumnSpan(diagnosticWarning, 2);
        Field("Diagnostic URL", diagnosticEndpoint, 11);
        var testButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        testButtons.Controls.AddRange([test, cancelTest]); layout.Controls.Add(testButtons, 1, 12);
        layout.Controls.Add(testResult, 0, 13); layout.SetColumnSpan(testResult, 2);
        layout.Controls.Add(feedback, 0, 14); layout.SetColumnSpan(feedback, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([cancel, save]); layout.Controls.Add(buttons, 0, 15); layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        var settings = original?.Settings;
        protocol.SelectedIndex = settings is null ? 0 : (int)settings.Protocol;
        if (settings is not null && settings.Protocol != ProxyProtocol.Direct)
        {
            host.Text = settings.Host; port.Value = settings.Port; label.Text = settings.Label;
            authentication.Checked = settings.AuthenticationRequired;
        }
        credentialHint.Text = settings?.CredentialId is not null
            ? "A protected credential is saved. Leave both fields empty to keep it; enter both to replace it."
            : "Enter both fields to add a protected credential.";
        protocol.SelectedIndexChanged += (_, _) => UpdateControls(); authentication.CheckedChanged += (_, _) => UpdateControls();
        UpdateControls(); AcceptButton = save; CancelButton = cancel;
        save.Click += async (_, _) => await SaveDraft();
        test.Click += async (_, _) => await TestSavedDraft();
        cancelTest.Click += (_, _) => testCancellation?.Cancel();
        FormClosing += (_, e) => { if (saving || testing) { e.Cancel = true; testCancellation?.Cancel(); } };
    }

    private void UpdateControls()
    {
        var proxied = protocol.SelectedIndex > 0;
        host.Enabled = port.Enabled = label.Enabled = authentication.Enabled = proxied;
        username.Enabled = password.Enabled = proxied && authentication.Checked;
        test.Enabled = diagnostics is not null && original is not null && original.Settings.Protocol != ProxyProtocol.Direct && !saving && !testing;
    }

    private async Task SaveDraft()
    {
        if (saving) return;
        try
        {
            var selected = (ProxyProtocol)protocol.SelectedIndex;
            ProxyCredentials? replacement = null;
            ProxyDraft draft;
            if (selected == ProxyProtocol.Direct) draft = new(ProxyProtocol.Direct, "", 0, false, null, "", false);
            else
            {
                if (selected == ProxyProtocol.Socks5 && authentication.Checked)
                    throw new ArgumentException("SOCKS5 username/password authentication is unsupported by Chromium's documented proxy implementation.");
                Guid? credential = null;
                if (authentication.Checked)
                {
                    if (username.Text.Length > 0 || password.Text.Length > 0)
                        replacement = new ProxyCredentials(username.Text, password.Text);
                    else credential = original?.Settings.AuthenticationRequired == true ? original.Settings.CredentialId : null;
                }
                draft = new(selected, host.Text.Trim(), decimal.ToInt32(port.Value), authentication.Checked, credential, label.Text.Trim(), true);
            }
            ProxyValidation.Validate(replacement is null ? draft : draft with { CredentialId = Guid.NewGuid() });
            var prompt = selected == ProxyProtocol.Direct
                ? "This removes the saved proxy and allows the profile to start with Chromium's existing system/network defaults. Continue?"
                : "Proxy networking is not available yet. Saving this draft will block the profile from starting until a supported build is available or you remove the draft. Continue?";
            if ((selected != ProxyProtocol.Direct || (original is not null && original.Settings.Protocol != ProxyProtocol.Direct)) && !confirm(prompt)) return;
            saving = true; SetBusy(true); feedback.Text = "Saving proxy settings…";
            var result = await Task.Run(() => editor.Save(profile, draft, original?.Revision ?? 0, replacement));
            if (IsDisposed) return;
            password.Clear(); username.Clear();
            SavedMessage = result.CleanupWarning ?? (selected == ProxyProtocol.Direct
                ? "Saved browser-default networking."
                : "Saved proxy draft. This profile cannot start until proxy networking is supported or the draft is removed.");
            DialogResult = DialogResult.OK; saving = false; Close();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!IsDisposed) feedback.Text = "Could not save proxy settings: " + ex.Message;
        }
        finally { saving = false; if (!IsDisposed) SetBusy(false); }
    }

    private async Task TestSavedDraft()
    {
        if (testing || saving || original is null || diagnostics is null) return;
        try
        {
            var endpoint = diagnosticEndpoint.Text;
            _ = ProxyTransportTester.ValidateEndpoint(endpoint);
            using var cancellation = new CancellationTokenSource();
            testCancellation = cancellation; testing = true; SetBusy(true);
            testResult.Text = "Testing the saved draft using .NET transport…";
            var result = await Task.Run(() => diagnostics.TestAsync(profile, original, endpoint, cancellation.Token));
            if (IsDisposed) return;
            var ip = result.ReportedIp is null ? "" : " Endpoint-reported IP: " + result.ReportedIp + ".";
            testResult.Text = $"{result.Outcome} ({result.Protocol}, {result.Elapsed.TotalMilliseconds:0} ms). {result.Detail}{ip}";
        }
        catch (OperationCanceledException) { if (!IsDisposed) testResult.Text = "The transport test was canceled."; }
        catch (ArgumentException ex) { if (!IsDisposed) testResult.Text = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!IsDisposed) testResult.Text = "Could not test the saved draft. Close the profile, reopen proxy settings, and check its saved configuration.";
        }
        finally
        {
            testing = false; testCancellation = null;
            if (!IsDisposed) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        protocol.Enabled = !busy; clearCredentials.Enabled = !busy; save.Enabled = cancel.Enabled = !busy;
        diagnosticEndpoint.Enabled = !busy; cancelTest.Enabled = testing;
        if (busy) test.Enabled = false;
        if (busy) host.Enabled = port.Enabled = label.Enabled = authentication.Enabled = username.Enabled = password.Enabled = false;
        else UpdateControls();
    }
}
