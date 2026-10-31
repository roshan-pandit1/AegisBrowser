using AegisBrowser.Core;

namespace AegisBrowser;

internal sealed class ProxyBulkDraftForm : Form
{
    private readonly ProxyBulkDrafts service;
    private readonly IReadOnlyList<ProfileDefinition> targets;
    private readonly Func<string, bool> confirm;
    private readonly ComboBox mode = new() { Name = "BulkProxyMode", AccessibleName = "Bulk saved proxy mode", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox host = new() { Name = "BulkProxyHost", AccessibleName = "Bulk proxy hostname or IP address", Dock = DockStyle.Fill };
    private readonly NumericUpDown port = new() { Name = "BulkProxyPort", AccessibleName = "Bulk proxy port", Minimum = 1, Maximum = 65535, Value = 8080, Dock = DockStyle.Fill };
    private readonly TextBox label = new() { Name = "BulkProxyLabel", AccessibleName = "Bulk proxy label", Dock = DockStyle.Fill, MaxLength = 120 };
    private readonly TextBox results = new() { Name = "BulkProxyResults", AccessibleName = "Per-profile proxy results", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly ProgressBar progress = new() { Name = "BulkProxyProgress", AccessibleName = "Bulk proxy progress", Dock = DockStyle.Fill };
    private readonly Button previewButton = new() { Name = "PreviewBulkProxy", Text = "Refresh preview", AccessibleName = "Refresh bulk proxy preview", AutoSize = true };
    private readonly Button applyButton = new() { Name = "ApplyBulkProxy", Text = "Apply", AccessibleName = "Apply bulk proxy drafts", AutoSize = true, Enabled = false };
    private readonly Button cancelButton = new() { Name = "CancelBulkProxy", Text = "Cancel remaining", AccessibleName = "Cancel remaining bulk proxy changes", AutoSize = true, Enabled = false };
    private readonly Button closeButton = new() { Text = "Close", AccessibleName = "Close bulk proxy drafts", AutoSize = true };
    private IReadOnlyList<ProxyBulkPreview>? previews;
    private CancellationTokenSource? operation;

    public ProxyBulkDraftForm(ProxyBulkDrafts service, IReadOnlyList<ProfileDefinition> targets, Func<string, bool>? confirm = null)
    {
        this.service = service; this.targets = targets;
        this.confirm = confirm ?? (message => MessageBox.Show(this, message, "Bulk proxy drafts", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes);
        Text = "Bulk proxy drafts"; AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
        Width = 760; Height = 650; MinimumSize = new Size(560, 480);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 6; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var explanation = new Label { Name = "BulkProxyWarning", AccessibleName = "Bulk proxy limitations", Dock = DockStyle.Fill, AutoSize = true,
            Text = $"{targets.Count} profiles selected. Proxy networking is unavailable: saved proxy drafts block launch. " +
                   "Running profiles and profiles with credentials are skipped; credentials are never shared." };
        layout.Controls.Add(explanation, 0, 0); layout.SetColumnSpan(explanation, 2);
        void Field(string text, Control control, int row)
        { layout.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); control.Margin = new Padding(0, 4, 0, 4); layout.Controls.Add(control, 1, row); }
        mode.Items.AddRange(["HTTP — draft only", "HTTPS — draft only", "SOCKS5 — draft only", "Remove drafts (browser defaults)"]);
        mode.SelectedIndex = 0;
        Field("Action", mode, 1); Field("Host", host, 2); Field("Port", port, 3); Field("Label", label, 4);
        layout.Controls.Add(progress, 0, 5); layout.SetColumnSpan(progress, 2);
        layout.Controls.Add(results, 0, 6); layout.SetColumnSpan(results, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([closeButton, cancelButton, applyButton, previewButton]); layout.Controls.Add(buttons, 0, 7); layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        mode.SelectedIndexChanged += (_, _) => UpdateInputs();
        previewButton.Click += async (_, _) => await Preview();
        applyButton.Click += async (_, _) => await Apply();
        cancelButton.Click += (_, _) => { operation?.Cancel(); cancelButton.Enabled = false; };
        closeButton.Click += (_, _) => Close();
        FormClosing += (_, e) => { if (operation is not null) { operation.Cancel(); e.Cancel = true; } };
        Shown += async (_, _) => await Preview();
        UpdateInputs();
    }

    private void UpdateInputs()
    {
        var editable = operation is null && mode.SelectedIndex != 3;
        host.Enabled = port.Enabled = label.Enabled = editable;
    }

    private void SetBusy(bool busy)
    {
        previewButton.Enabled = closeButton.Enabled = mode.Enabled = !busy;
        cancelButton.Enabled = busy;
        applyButton.Enabled = !busy && previews is not null;
        UpdateInputs();
    }

    private async Task Preview()
    {
        if (operation is not null) return;
        previews = null; operation = new CancellationTokenSource(); var token = operation.Token;
        SetBusy(true); results.Text = "Loading preview…"; progress.Value = 0;
        try
        {
            var snapshot = await Task.Run(() => service.Preview(targets, token));
            if (IsDisposed) return;
            previews = snapshot;
            results.Lines = snapshot.Select(item => item.Profile.Slug + " — " + item.Outcome + ": " + item.Message).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!IsDisposed) results.Text = "Could not preview: " + ex.Message; }
        finally { operation.Dispose(); operation = null; if (!IsDisposed) SetBusy(false); }
    }

    private async Task Apply()
    {
        if (operation is not null || previews is null) return;
        try
        {
            var draft = mode.SelectedIndex == 3
                ? new ProxyDraft(ProxyProtocol.Direct, "", 0, false, null, "", false)
                : new ProxyDraft((ProxyProtocol)(mode.SelectedIndex + 1), host.Text.Trim(), decimal.ToInt32(port.Value), false, null, label.Text.Trim(), true);
            ProxyValidation.Validate(draft);
            var prompt = draft.Protocol == ProxyProtocol.Direct
                ? "This allows eligible profiles to use Chromium's existing system/network defaults. Profiles containing credentials are skipped. Continue?"
                : "This saves non-secret proxy drafts and blocks eligible profiles from launching until networking is supported or the drafts are removed. Continue?";
            if (!confirm(prompt)) return;
            var snapshot = previews; operation = new CancellationTokenSource(); var token = operation.Token;
            SetBusy(true); progress.Maximum = snapshot.Count; progress.Value = 0; results.Clear();
            var reporter = new Progress<ProfileOperationProgress>(value => { if (!IsDisposed) progress.Value = Math.Min(progress.Maximum, value.Completed); });
            var output = await Task.Run(() => service.Apply(snapshot, draft, token, reporter));
            if (!IsDisposed) results.Lines = output.Select(item => item.Slug + " — " + item.Outcome + ": " + item.Message).ToArray();
            previews = null; // A new preview is required after any attempted change.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!IsDisposed) results.Text = "Could not apply proxy drafts: " + ex.Message; }
        finally { operation?.Dispose(); operation = null; if (!IsDisposed) SetBusy(false); }
    }
}
