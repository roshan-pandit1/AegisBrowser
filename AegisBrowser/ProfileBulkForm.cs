using AegisBrowser.Core;

namespace AegisBrowser;

internal sealed class ProfileBulkForm : Form
{
    private readonly ProfileBulkOperations service;
    private readonly IReadOnlyList<ProfileDefinition> targets;
    private readonly bool duplicate;
    private readonly ComboBox operation = new() { Name = "BulkAction", AccessibleName = "Bulk action", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly TextBox labels = new() { Name = "BulkLabels", AccessibleName = "Tags or folders, one per line", Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox results = new() { Name = "BulkResults", AccessibleName = "Per-profile operation results", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly ProgressBar progress = new() { Name = "BulkProgress", AccessibleName = "Bulk operation progress", Dock = DockStyle.Fill };
    private readonly Button start = new() { Name = "StartBulk", Text = "Start", AutoSize = true };
    private readonly Button cancel = new() { Name = "CancelBulk", Text = "Cancel remaining", AutoSize = true, Enabled = false };
    private readonly Button close = new() { Text = "Close", AutoSize = true };
    private CancellationTokenSource? cancellation;

    public ProfileBulkForm(ProfileBulkOperations service, IReadOnlyList<ProfileDefinition> targets, bool duplicate)
    {
        this.service = service; this.targets = targets; this.duplicate = duplicate;
        Text = duplicate ? "Clean duplicate profiles" : "Bulk profile labels";
        AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
        Width = 700; Height = 600; MinimumSize = new Size(500, 420);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(16) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = $"{targets.Count} profiles. Completed changes remain if you cancel; each result is reported below.", AutoSize = true }, 0, 0);
        operation.Items.AddRange(["Add tags", "Remove tags", "Assign folders", "Remove folders"]); operation.SelectedIndex = 0;
        operation.Visible = !duplicate; labels.Visible = !duplicate;
        layout.Controls.Add(operation, 0, 1); layout.Controls.Add(labels, 0, 2); layout.Controls.Add(results, 0, 3); layout.Controls.Add(progress, 0, 4);
        layout.Controls.Add(new Label { Text = duplicate ? "Clean copies exclude notes, settings and Chromium data." : "Enter one tag or folder per line.", AutoSize = true }, 0, 5);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([close, cancel, start]); layout.Controls.Add(buttons, 0, 6); Controls.Add(layout);
        start.Click += async (_, _) => await Run();
        cancel.Click += (_, _) => { cancellation?.Cancel(); cancel.Enabled = false; };
        close.Click += (_, _) => Close();
        FormClosing += (_, e) => { if (cancellation is not null) { cancellation.Cancel(); e.Cancel = true; } };
    }

    private async Task Run()
    {
        if (duplicate && MessageBox.Show(this, $"Create clean copies of {targets.Count} selected profiles?", "Confirm clean copies",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var selected = duplicate ? ProfileBulkAction.DuplicateClean : (ProfileBulkAction)operation.SelectedIndex;
        var values = labels.Lines.Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        start.Enabled = false; close.Enabled = false; cancel.Enabled = true; operation.Enabled = false; labels.Enabled = false;
        progress.Maximum = targets.Count; progress.Value = 0; results.Clear();
        var reporter = new Progress<ProfileOperationProgress>(p => { if (!IsDisposed) progress.Value = Math.Min(progress.Maximum, p.Completed); });
        try
        {
            var output = await Task.Run(() => service.Run(targets, selected, values, token, reporter));
            if (!IsDisposed) results.Lines = output.Select(r => r.Slug + " — " + r.Outcome + ": " + r.Message).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!IsDisposed) results.Text = "Could not start the operation: " + ex.Message; }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            if (!IsDisposed) { start.Enabled = true; close.Enabled = true; cancel.Enabled = false; operation.Enabled = true; labels.Enabled = true; }
        }
    }
}
