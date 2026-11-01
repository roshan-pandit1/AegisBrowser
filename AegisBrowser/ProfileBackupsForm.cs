using AegisBrowser.Core;

namespace AegisBrowser;

internal sealed class ProfileBackupsForm : Form
{
    private readonly ProfileBackupService service;
    private readonly ProfileRepository repository;
    private readonly ProfileDefinition? selectedProfile;
    private readonly ListBox list = new() { Name = "BackupList", AccessibleName = "Metadata backups", Dock = DockStyle.Fill };
    private readonly TextBox details = new() { Name = "BackupDetails", AccessibleName = "Backup preview", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly CheckBox privacy = new() { Text = "Include selected profile's privacy settings", AutoSize = true };
    private readonly CheckBox search = new() { Text = "Include selected profile's search settings", AutoSize = true };
    private readonly Label feedback = new() { Name = "BackupFeedback", AccessibleName = "Backup messages", Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Button create = new() { Name = "CreateBackup", Text = "Create backup", AutoSize = true };
    private readonly Button restore = new() { Name = "RestoreMetadata", Text = "Restore metadata…", AutoSize = true };
    private readonly Button restoreNew = new() { Name = "RestoreAsNew", Text = "Restore as new profile…", AutoSize = true };
    private readonly Button refresh = new() { Text = "Refresh", AutoSize = true };
    private readonly NumericUpDown retention = new() { Name = "BackupRetentionReminder", AccessibleName = "Backup retention reminder threshold", Minimum = 1, Maximum = 10_000, Value = 20, Width = 70 };
    private readonly Button saveRetention = new() { Text = "Save reminder", AccessibleName = "Save retention reminder", AutoSize = true };
    private BackupRetentionReminder? reminder;
    private bool busy;
    private sealed record Item(ProfileBackupInfo Value) { public override string ToString() => Value.Valid ? $"{Value.CreatedUtc?.ToLocalTime():g} — {Value.Description}" : Value.Description; }
    private ProfileBackupInfo? Selected => (list.SelectedItem as Item)?.Value;

    public ProfileBackupsForm(ProfileBackupService service, ProfileRepository repository, ProfileDefinition? selectedProfile)
    {
        this.service = service; this.repository = repository; this.selectedProfile = selectedProfile;
        Text = "Metadata backups"; AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
        Width = 850; Height = 620; MinimumSize = new Size(560, 420);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        var flags = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        flags.Controls.AddRange([privacy, search, new Label { Text = "Remind after", AutoSize = true }, retention, saveRetention]); layout.Controls.Add(flags, 0, 0); layout.Controls.Add(list, 0, 1); layout.Controls.Add(details, 0, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        buttons.Controls.AddRange([create, restore, restoreNew, refresh]); layout.Controls.Add(buttons, 0, 3); layout.Controls.Add(feedback, 0, 4); Controls.Add(layout);
        create.Enabled = selectedProfile is not null;
        create.Click += async (_, _) => await CreateBackup();
        refresh.Click += async (_, _) => await RefreshBackups();
        saveRetention.Click += async (_, _) => await SaveReminder();
        restore.Click += async (_, _) => await RestoreMetadata();
        restoreNew.Click += async (_, _) => await RestoreNew();
        list.SelectedIndexChanged += async (_, _) => await Preview();
        Shown += async (_, _) => await RefreshBackups();
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
    }

    private async Task RefreshBackups()
    {
        try
        {
            feedback.Text = "Validating backups…";
            var data = await Task.Run(service.List);
            var policy = await Task.Run(service.GetRetentionReminder); if (IsDisposed) return;
            reminder = policy; retention.Value = policy.Threshold;
            list.Items.Clear(); foreach (var item in data) list.Items.Add(new Item(item));
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            else details.Text = "No manual backups. Select a profile on the dashboard, then create one.";
            feedback.Text = data.Count >= policy.Threshold ? "Retention reminder reached. Backups are never deleted automatically." :
                "Backups contain metadata and selected settings, not Chromium data; none are deleted automatically.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { if (!IsDisposed) feedback.Text = "Could not list backups: " + ex.Message; }
    }
    private async Task SaveReminder()
    {
        var current = reminder; if (current is null || busy) return;
        try { var value = (int)retention.Value; reminder = await Task.Run(() => service.SaveRetentionReminder(current, value)); if (!IsDisposed) feedback.Text = "Reminder saved. Backups are never deleted automatically."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!IsDisposed) feedback.Text = "Could not save reminder: " + ex.Message; }
    }
    private async Task Preview()
    {
        var selected = Selected; if (selected is null || !selected.Valid) { details.Text = selected?.Description ?? "Select a backup."; return; }
        try
        {
            var preview = await Task.Run(() => service.Verify(selected.Id));
            if (IsDisposed || Selected?.Id != selected.Id) return;
            var p = preview.Profile; var c = preview.Manifest.Content;
            details.Text = $"Backup: {c.CreatedUtc.ToLocalTime():g}\r\nProfile: {p.DisplayName}\r\nID: {p.Id:D}\r\nSlug: {p.Slug}\r\n" +
                $"Tags: {string.Join(", ", p.Tags)}\r\nFolders: {string.Join(", ", p.Folders)}\r\nNotes:\r\n{p.Notes}\r\n\r\n" +
                $"Privacy settings: {(c.Settings.Privacy is null ? "not included" : "included")}\r\nSearch settings: {(c.Settings.Search is null ? "not included" : "included")}\r\n" +
                "SQLite metadata is backed up for all profiles; this screen restores only the selected backup profile.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { if (!IsDisposed) details.Text = "Backup invalid: " + ex.Message; }
    }
    private async Task CreateBackup()
    {
        if (selectedProfile is null || busy) return;
        var p = privacy.Checked; var s = search.Checked; SetBusy(true); feedback.Text = "Creating a consistent metadata backup…";
        try { _ = await Task.Run(() => service.Create(selectedProfile, p, s)); if (!IsDisposed) await RefreshBackups(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!IsDisposed) feedback.Text = "Backup failed: " + ex.Message; }
        finally { if (!IsDisposed) SetBusy(false); }
    }
    private async Task RestoreMetadata()
    {
        var item = Selected; if (item is null || !item.Valid || busy) return;
        try
        {
            var preview = await Task.Run(() => service.Verify(item.Id));
            var current = await Task.Run(() => repository.GetBySlug(preview.Profile.Slug));
            if (IsDisposed) return;
            if (current is null) { feedback.Text = "The original metadata is missing. Restore as a new profile instead."; return; }
            if (MessageBox.Show(this, "Restore the previewed name, notes, tags and folders for '" + current.DisplayName + "'? " +
                "The profile must be closed. A safety metadata backup will be created first; browser storage and settings are not changed.",
                "Confirm metadata restore", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            SetBusy(true); feedback.Text = "Restoring metadata…";
            _ = await Task.Run(() => service.RestoreMetadata(item.Id, current));
            if (!IsDisposed) feedback.Text = "Metadata restored. A safety snapshot was retained.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!IsDisposed) feedback.Text = "Restore failed: " + ex.Message; }
        finally { if (!IsDisposed) SetBusy(false); }
    }
    private async Task RestoreNew()
    {
        var item = Selected; if (item is null || !item.Valid || busy) return;
        try
        {
            var preview = await Task.Run(() => service.Verify(item.Id));
            if (IsDisposed) return;
            using var form = new ProfileCopyForm("Restore as new profile", preview.Profile.DisplayName, false,
                (slug, name, _, _, _) => service.RestoreAsNew(item.Id, slug, name));
            if (form.ShowDialog(this) == DialogResult.OK) feedback.Text = "Restored as new profile " + form.CreatedSlug + ".";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!IsDisposed) feedback.Text = "Restore failed: " + ex.Message; }
    }
    private void SetBusy(bool value)
    {
        busy = value; create.Enabled = !value && selectedProfile is not null; restore.Enabled = restoreNew.Enabled = refresh.Enabled = !value;
        list.Enabled = privacy.Enabled = search.Enabled = retention.Enabled = saveRetention.Enabled = !value;
    }
}
