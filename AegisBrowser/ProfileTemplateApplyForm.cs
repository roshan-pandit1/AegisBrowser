using AegisBrowser.Core;

namespace AegisBrowser;

internal sealed class ProfileTemplateApplyForm : Form
{
    private readonly ProfileTemplateRepository templates;
    private readonly ProfileSettingsTransactions service;
    private readonly IReadOnlyList<ProfileDefinition> targets;
    private readonly ListBox list = new() { Name = "ApplyTemplateList", AccessibleName = "Templates with saved settings", Dock = DockStyle.Fill };
    private readonly TextBox results = new() { Name = "ApplyTemplateResults", AccessibleName = "Template application results", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly ProgressBar progress = new() { AccessibleName = "Template application progress", Dock = DockStyle.Fill };
    private readonly Button apply = new() { Name = "ConfirmTemplateApply", Text = "Apply settings…", AutoSize = true };
    private readonly Button cancel = new() { Text = "Cancel remaining", AutoSize = true, Enabled = false };
    private readonly Button close = new() { Text = "Close", AutoSize = true };
    private CancellationTokenSource? cancellation;
    private sealed record Item(ProfileTemplate Value)
    { public override string ToString() => Value.Name + " — " + (Value.Settings.Privacy is null ? "" : "privacy ") + (Value.Settings.Search is null ? "" : "search"); }

    public ProfileTemplateApplyForm(ProfileTemplateRepository templates, ProfileSettingsTransactions service, IReadOnlyList<ProfileDefinition> targets)
    {
        this.templates = templates; this.service = service; this.targets = targets;
        Text = "Apply template settings"; AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
        Width = 720; Height = 560; MinimumSize = new Size(500, 400);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(16) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Selected profiles must be closed and already have each settings file. Notes, tags and folders are unchanged. Interruptions require recovery before opening a profile.", AutoSize = true, MaximumSize = new Size(650, 0) }, 0, 0);
        layout.Controls.Add(list, 0, 1); layout.Controls.Add(results, 0, 2); layout.Controls.Add(progress, 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([close, cancel, apply]); layout.Controls.Add(buttons, 0, 4); Controls.Add(layout);
        Shown += async (_, _) => await LoadTemplates();
        apply.Click += async (_, _) => await Apply();
        cancel.Click += (_, _) => { cancellation?.Cancel(); cancel.Enabled = false; };
        close.Click += (_, _) => Close();
        FormClosing += (_, e) => { if (cancellation is not null) { cancellation.Cancel(); e.Cancel = true; } };
    }
    private async Task LoadTemplates()
    {
        try
        {
            var values = await Task.Run(templates.List); if (IsDisposed) return;
            foreach (var value in values.Where(t => t.Settings.Privacy is not null || t.Settings.Search is not null)) list.Items.Add(new Item(value));
            if (list.Items.Count > 0) list.SelectedIndex = 0; else results.Text = "No template has saved privacy or search settings.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { if (!IsDisposed) results.Text = "Could not load templates: " + ex.Message; }
    }
    private async Task Apply()
    {
        if (list.SelectedItem is not Item item) return;
        if (MessageBox.Show(this, $"Replace the saved settings indicated by this template in {targets.Count} selected closed profiles?", "Confirm settings application",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        cancellation = new CancellationTokenSource(); var token = cancellation.Token;
        apply.Enabled = close.Enabled = list.Enabled = false; cancel.Enabled = true;
        progress.Maximum = targets.Count; progress.Value = 0; results.Clear();
        var reporter = new Progress<ProfileOperationProgress>(p => { if (!IsDisposed) progress.Value = Math.Min(progress.Maximum, p.Completed); });
        try
        {
            var output = await Task.Run(() => service.Apply(targets, item.Value, token, reporter));
            if (!IsDisposed) results.Lines = output.Select(r => r.Slug + " — " + r.Outcome + ": " + r.Message).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!IsDisposed) results.Text = "Could not apply template: " + ex.Message; }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            if (!IsDisposed) { apply.Enabled = close.Enabled = list.Enabled = true; cancel.Enabled = false; }
        }
    }
}
