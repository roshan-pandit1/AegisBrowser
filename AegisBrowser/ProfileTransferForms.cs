using AegisBrowser.Core;

namespace AegisBrowser;

internal sealed class ProfileExportForm : Form
{
    private readonly ProfileTransfer transfer;
    private readonly IReadOnlyList<ProfileDefinition> profiles;
    private readonly IReadOnlyList<ProfileTemplate> templates;
    private readonly CheckBox privacy = new() { Text = "Include profile privacy settings (profiles must be closed)", AutoSize = true, AccessibleName = "Include privacy settings" };
    private readonly CheckBox search = new() { Text = "Include profile search settings (profiles must be closed)", AutoSize = true, AccessibleName = "Include search settings" };
    private readonly Label feedback = new() { Name = "ExportFeedback", AccessibleName = "Export messages", Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Button save = new() { Name = "SaveExport", Text = "Choose destination…", AutoSize = true };
    private readonly Button close = new() { Text = "Close", AutoSize = true };

    public ProfileExportForm(ProfileTransfer transfer, IReadOnlyList<ProfileDefinition> profiles, IReadOnlyList<ProfileTemplate> templates)
    {
        this.transfer = transfer; this.profiles = profiles; this.templates = templates;
        Text = "Export profile metadata"; AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
        Width = 650; Height = 340; MinimumSize = new Size(480, 300);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(16) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Exports are unencrypted and can include notes, labels and template settings. Cookies, sessions and browser caches are excluded.", AutoSize = true, MaximumSize = new Size(570, 0) }, 0, 0);
        privacy.Enabled = search.Enabled = profiles.Count > 0;
        layout.Controls.Add(privacy, 0, 1); layout.Controls.Add(search, 0, 2); layout.Controls.Add(feedback, 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([close, save]); layout.Controls.Add(buttons, 0, 4); Controls.Add(layout);
        close.Click += (_, _) => Close();
        save.Click += async (_, _) => await Save();
    }

    private async Task Save()
    {
        using var file = new SaveFileDialog { Title = "Export metadata to a new file", Filter = "Aegis JSON (*.json)|*.json|Aegis ZIP (*.zip)|*.zip", FileName = "aegis-profiles.json", OverwritePrompt = false };
        if (file.ShowDialog(this) != DialogResult.OK) return;
        var p = privacy.Checked; var s = search.Checked; var zip = file.FilterIndex == 2;
        save.Enabled = false; close.Enabled = false; feedback.Text = "Exporting…";
        try
        {
            await Task.Run(() => transfer.Export(file.FileName, profiles, templates, p, s, zip));
            if (!IsDisposed) feedback.Text = "Export saved. Existing files were not replaced.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!IsDisposed) feedback.Text = "Export failed: " + ex.Message; }
        finally { if (!IsDisposed) { save.Enabled = true; close.Enabled = true; } }
    }
}

internal sealed class ProfileImportForm : Form
{
    private readonly ProfileTransfer transfer;
    private readonly ProfileImportPreview preview;
    private readonly TextBox details = new() { Name = "ImportPreview", AccessibleName = "Import preview and results", Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly ProgressBar progress = new() { Name = "ImportProgress", AccessibleName = "Import progress", Dock = DockStyle.Fill };
    private readonly Button import = new() { Name = "ConfirmImport", Text = "Import", AutoSize = true };
    private readonly Button cancel = new() { Text = "Cancel remaining", AutoSize = true, Enabled = false };
    private readonly Button close = new() { Text = "Close", AutoSize = true };
    private CancellationTokenSource? cancellation;

    public ProfileImportForm(ProfileTransfer transfer, ProfileImportPreview preview)
    {
        this.transfer = transfer; this.preview = preview;
        Text = "Review metadata import"; AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
        Width = 720; Height = 560; MinimumSize = new Size(500, 400);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(16) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Review the new names below. The checksum detects corruption, not the file's origin. Existing profiles are never replaced.", AutoSize = true, MaximumSize = new Size(650, 0) }, 0, 0);
        details.Lines = preview.Profiles.Select(p => "Profile: " + p.Source.DisplayName + " → " + p.DestinationSlug)
            .Concat(preview.Templates.Select(t => "Template: " + t.Name + " → new ID and imported name")).ToArray();
        layout.Controls.Add(details, 0, 1); layout.Controls.Add(progress, 0, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([close, cancel, import]); layout.Controls.Add(buttons, 0, 3); Controls.Add(layout);
        import.Click += async (_, _) => await Import();
        cancel.Click += (_, _) => { cancellation?.Cancel(); cancel.Enabled = false; };
        close.Click += (_, _) => Close();
        FormClosing += (_, e) => { if (cancellation is not null) { cancellation.Cancel(); e.Cancel = true; } };
    }

    private async Task Import()
    {
        cancellation = new CancellationTokenSource(); var token = cancellation.Token;
        import.Enabled = false; close.Enabled = false; cancel.Enabled = true;
        progress.Maximum = preview.Profiles.Length + preview.Templates.Length; progress.Value = 0;
        var reporter = new Progress<ProfileOperationProgress>(p => { if (!IsDisposed) progress.Value = Math.Min(progress.Maximum, p.Completed); });
        try
        {
            var results = await Task.Run(() => transfer.Import(preview, token, reporter));
            if (!IsDisposed) details.Lines = results.Select(r => r.Slug + " — " + r.Outcome + ": " + r.Message).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!IsDisposed) details.Text = "Import could not start: " + ex.Message; }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            if (!IsDisposed) { close.Enabled = true; cancel.Enabled = false; }
        }
    }
}
