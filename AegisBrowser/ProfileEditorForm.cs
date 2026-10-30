using AegisBrowser.Core;

namespace AegisBrowser;

internal sealed class ProfileEditorForm : Form
{
    private readonly ProfileDashboard dashboard;
    private readonly ProfileDefinition? original;
    private readonly TextBox slug = new() { Name = "EditSlug", AccessibleName = "Storage slug", Dock = DockStyle.Fill };
    private readonly TextBox displayName = new() { Name = "EditDisplayName", AccessibleName = "Display name", Dock = DockStyle.Fill };
    private readonly TextBox notes = new() { Name = "EditNotes", AccessibleName = "Profile notes", Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox tags = new() { Name = "EditTags", AccessibleName = "Tags, one per line", Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox folders = new() { Name = "EditFolders", AccessibleName = "Folders, one per line", Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly Label message = new() { Name = "EditorFeedback", AccessibleName = "Profile editor messages", Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Button save = new() { Name = "SaveProfile", Text = "Save", AccessibleName = "Save profile", AutoSize = true };
    private readonly Button cancel = new() { Text = "Cancel", AccessibleName = "Cancel editing", DialogResult = DialogResult.Cancel, AutoSize = true };
    public string? SavedSlug { get; private set; }

    public ProfileEditorForm(ProfileDashboard dashboard, ProfileDefinition? original)
    {
        this.dashboard = dashboard; this.original = original;
        Text = original is null ? "Create profile" : "Edit profile metadata";
        AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
        Width = 620; Height = 700; MinimumSize = new Size(460, 520);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        void Field(string title, Control control, int row)
        { layout.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 8, 4, 4) }, 0, row); control.Margin = new Padding(0, 4, 0, 4); layout.Controls.Add(control, 1, row); }
        Field("Storage slug", slug, 0); Field("Display name", displayName, 1);
        var identity = new Label { Name = "ProfileIdentity", AccessibleName = "Profile ID and revision", Dock = DockStyle.Fill, AutoSize = true,
            Text = original is null ? "A stable ID is assigned on creation." : $"ID: {original.Id:D}   Revision: {original.Revision}" };
        layout.Controls.Add(identity, 1, 2);
        Field("Notes", notes, 3); Field("Tags (one per line)", tags, 4); Field("Folders (one per line)", folders, 5);
        layout.Controls.Add(message, 0, 6); layout.SetColumnSpan(message, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([cancel, save]); layout.Controls.Add(buttons, 0, 7); layout.SetColumnSpan(buttons, 2); Controls.Add(layout);
        if (original is not null)
        {
            slug.Text = original.Slug; slug.ReadOnly = true; displayName.Text = original.DisplayName; notes.Text = original.Notes;
            tags.Lines = original.Tags.ToArray(); folders.Lines = original.Folders.ToArray();
        }
        AcceptButton = save; CancelButton = cancel;
        save.Click += async (_, _) => await SaveProfile();
    }

    private async Task SaveProfile()
    {
        save.Enabled = false; cancel.Enabled = false; message.Text = "Saving…";
        var name = displayName.Text.Trim();
        var note = notes.Text;
        var selectedTags = Lines(tags); var selectedFolders = Lines(folders);
        var storageSlug = slug.Text.Trim();
        try
        {
            var saved = await Task.Run(() => original is null
                ? dashboard.Create(new ProfileDraft(storageSlug, name, note, selectedTags, selectedFolders))
                : dashboard.Save(original with { DisplayName = name, Notes = note, Tags = selectedTags, Folders = selectedFolders }));
            if (IsDisposed) return;
            SavedSlug = saved.Slug; DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (!IsDisposed) message.Text = "Could not save: " + ex.Message + " Your edits remain here; reload the profile if it changed elsewhere.";
        }
        finally { if (!IsDisposed) { save.Enabled = true; cancel.Enabled = true; } }
    }

    private static string[] Lines(TextBox box) => box.Lines.Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
}
