using AegisBrowser.Core;

namespace AegisBrowser;

internal sealed class ProfileCopyForm : Form
{
    private readonly Func<string, string, bool, bool, bool, ProfileDefinition> create;
    private readonly TextBox slug = new() { Name = "CopySlug", AccessibleName = "New storage slug", Dock = DockStyle.Fill };
    private readonly TextBox display = new() { Name = "CopyName", AccessibleName = "New display name", Dock = DockStyle.Fill };
    private readonly CheckBox notes = new() { Text = "Copy notes", AccessibleName = "Copy profile notes", AutoSize = true };
    private readonly CheckBox privacy = new() { Text = "Copy privacy settings", AccessibleName = "Copy privacy settings", AutoSize = true };
    private readonly CheckBox search = new() { Text = "Copy search settings", AccessibleName = "Copy search settings", AutoSize = true };
    private readonly Label message = new() { Name = "CopyFeedback", AccessibleName = "Duplication messages", Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Button save = new() { Name = "CopySave", Text = "Create", AccessibleName = "Create copied profile", AutoSize = true };
    private readonly Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
    public string? CreatedSlug { get; private set; }

    public ProfileCopyForm(string title, string suggestedName, bool options, Func<string, string, bool, bool, bool, ProfileDefinition> create)
    {
        this.create = create; Text = title; AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
        Width = 580; Height = options ? 420 : 300; MinimumSize = new Size(460, 280);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Storage slug", AutoSize = true }, 0, 0); layout.Controls.Add(slug, 1, 0);
        layout.Controls.Add(new Label { Text = "Display name", AutoSize = true }, 0, 1); layout.Controls.Add(display, 1, 1); display.Text = suggestedName;
        var flags = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        if (options) { flags.Controls.AddRange([notes, privacy, search]); }
        flags.Controls.Add(new Label { Text = options ? "Clean copy only. Cookies, sessions and browser storage are not copied. Full duplication is unavailable." : "Creates fresh browser storage. Existing profiles are never replaced.", AutoSize = true, MaximumSize = new Size(380, 0) });
        layout.Controls.Add(flags, 1, 2); layout.Controls.Add(message, 0, 4); layout.SetColumnSpan(message, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([cancel, save]); layout.Controls.Add(buttons, 0, 5); layout.SetColumnSpan(buttons, 2); Controls.Add(layout);
        AcceptButton = save; CancelButton = cancel;
        save.Click += async (_, _) =>
        {
            var s = slug.Text.Trim(); var d = display.Text.Trim(); var n = notes.Checked; var p = privacy.Checked; var q = search.Checked;
            save.Enabled = false; cancel.Enabled = false; message.Text = "Creating profile…";
            try
            {
                var result = await Task.Run(() => create(s, d, n, p, q));
                if (!IsDisposed) { CreatedSlug = result.Slug; DialogResult = DialogResult.OK; Close(); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { if (!IsDisposed) message.Text = "Could not create profile: " + ex.Message; }
            finally { if (!IsDisposed) { save.Enabled = true; cancel.Enabled = true; } }
        };
    }
}

internal sealed class TemplateCaptureForm : Form
{
    private readonly TextBox name = new() { Name = "TemplateName", AccessibleName = "Template name", Dock = DockStyle.Fill };
    private readonly TextBox description = new() { Name = "TemplateDescription", AccessibleName = "Template description", Dock = DockStyle.Fill, Multiline = true };
    private readonly CheckBox privacy = new() { Text = "Include privacy settings", AutoSize = true, AccessibleName = "Include privacy settings" };
    private readonly CheckBox search = new() { Text = "Include search settings", AutoSize = true, AccessibleName = "Include search settings" };
    private readonly Label feedback = new() { Name = "TemplateCaptureFeedback", Dock = DockStyle.Fill, AccessibleName = "Template creation messages", AutoEllipsis = true };
    private readonly Button save = new() { Name = "SaveTemplate", Text = "Save template", AutoSize = true };
    private readonly Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
    public TemplateCaptureForm(ProfileTemplateService service, ProfileDefinition source)
    {
        Text = "Save profile as template"; AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
        Width = 580; Height = 400; MinimumSize = new Size(460, 320);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Template name", AutoSize = true }, 0, 0); layout.Controls.Add(name, 1, 0); name.Text = source.DisplayName;
        layout.Controls.Add(new Label { Text = "Description", AutoSize = true }, 0, 1); layout.Controls.Add(description, 1, 1);
        var flags = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true }; flags.Controls.AddRange([privacy, search]); layout.Controls.Add(flags, 1, 2);
        layout.Controls.Add(feedback, 0, 3); layout.SetColumnSpan(feedback, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([cancel, save]); layout.Controls.Add(buttons, 0, 4); layout.SetColumnSpan(buttons, 2); Controls.Add(layout);
        AcceptButton = save; CancelButton = cancel;
        save.Click += async (_, _) =>
        {
            var n = name.Text.Trim(); var d = description.Text; var p = privacy.Checked; var s = search.Checked;
            save.Enabled = false; cancel.Enabled = false; feedback.Text = "Saving template…";
            try { _ = await Task.Run(() => service.SaveFromProfile(source, n, d, p, s)); if (!IsDisposed) { DialogResult = DialogResult.OK; Close(); } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { if (!IsDisposed) feedback.Text = "Could not save template: " + ex.Message; }
            finally { if (!IsDisposed) { save.Enabled = true; cancel.Enabled = true; } }
        };
    }
}

internal sealed class TemplateEditorForm : Form
{
    private readonly TextBox name = new() { Name = "TemplateEditName", AccessibleName = "Template name", Dock = DockStyle.Fill };
    private readonly TextBox description = new() { Name = "TemplateEditDescription", AccessibleName = "Template description", Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox tags = new() { Name = "TemplateEditTags", AccessibleName = "Template tags, one per line", Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox folders = new() { Name = "TemplateEditFolders", AccessibleName = "Template folders, one per line", Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    private readonly CheckBox privacy = new() { Text = "Keep saved privacy settings", AutoSize = true };
    private readonly CheckBox search = new() { Text = "Keep saved search settings", AutoSize = true };
    private readonly Label feedback = new() { Name = "TemplateEditFeedback", AccessibleName = "Template editor messages", Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Button save = new() { Name = "TemplateEditSave", Text = "Save", AutoSize = true };
    private readonly Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };

    public TemplateEditorForm(ProfileTemplateRepository repository, ProfileTemplate original)
    {
        Text = "Edit template"; AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
        Width = 620; Height = 650; MinimumSize = new Size(460, 500);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7, Padding = new Padding(16) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (var i = 0; i < 3; i++) layout.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        void Field(string title, Control control, int row) { layout.Controls.Add(new Label { Text = title, AutoSize = true }, 0, row); layout.Controls.Add(control, 1, row); }
        Field("Name", name, 0); Field("Description", description, 1); Field("Tags (one per line)", tags, 2); Field("Folders (one per line)", folders, 3);
        var flags = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true }; flags.Controls.AddRange([privacy, search]); layout.Controls.Add(flags, 1, 4);
        layout.Controls.Add(feedback, 0, 5); layout.SetColumnSpan(feedback, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([cancel, save]); layout.Controls.Add(buttons, 0, 6); layout.SetColumnSpan(buttons, 2); Controls.Add(layout);
        name.Text = original.Name; description.Text = original.Description; tags.Lines = original.Tags.ToArray(); folders.Lines = original.Folders.ToArray();
        privacy.Checked = privacy.Enabled = original.Settings.Privacy is not null;
        search.Checked = search.Enabled = original.Settings.Search is not null;
        AcceptButton = save; CancelButton = cancel;
        save.Click += async (_, _) =>
        {
            var candidate = original with { Name = name.Text.Trim(), Description = description.Text, Tags = Lines(tags), Folders = Lines(folders),
                Settings = new(privacy.Checked ? original.Settings.Privacy : null, search.Checked ? original.Settings.Search : null) };
            save.Enabled = false; cancel.Enabled = false; feedback.Text = "Saving…";
            try { _ = await Task.Run(() => repository.Update(candidate, original.Revision)); if (!IsDisposed) { DialogResult = DialogResult.OK; Close(); } }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { if (!IsDisposed) feedback.Text = "Could not save: " + ex.Message + " Your edits remain here."; }
            finally { if (!IsDisposed) { save.Enabled = true; cancel.Enabled = true; } }
        };
    }
    private static string[] Lines(TextBox box) => box.Lines.Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
}

internal sealed class ProfileTemplatesForm : Form
{
    private readonly ProfileTemplateRepository repository;
    private readonly ProfileTemplateService service;
    private readonly TextBox search = new() { Name = "TemplateSearch", AccessibleName = "Search templates", Dock = DockStyle.Top, PlaceholderText = "Search templates, tags and folders" };
    private readonly ListBox list = new() { Name = "TemplateList", AccessibleName = "Templates", Dock = DockStyle.Fill };
    private readonly Label feedback = new() { Name = "TemplatesFeedback", AccessibleName = "Template messages", Dock = DockStyle.Fill, AutoEllipsis = true };
    private IReadOnlyList<ProfileTemplate> all = [];
    private sealed record Item(ProfileTemplate Value) { public override string ToString() => Value.Name; }
    private ProfileTemplate? Selected => (list.SelectedItem as Item)?.Value;

    public ProfileTemplatesForm(ProfileTemplateRepository repository, ProfileTemplateService service)
    {
        this.repository = repository; this.service = service;
        Text = "Profile templates"; AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
        Width = 760; Height = 540; MinimumSize = new Size(500, 360);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(16) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(search, 0, 0); layout.Controls.Add(list, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        Button Action(string label, EventHandler handler) { var b = new Button { Text = label, AccessibleName = label, AutoSize = true }; b.Click += handler; return b; }
        buttons.Controls.Add(Action("Create profile", (_, _) => CreateProfile()));
        buttons.Controls.Add(Action("Edit", (_, _) => Edit()));
        buttons.Controls.Add(Action("Duplicate", async (_, _) => await Duplicate()));
        buttons.Controls.Add(Action("Delete", async (_, _) => await Delete()));
        buttons.Controls.Add(Action("Refresh", async (_, _) => await RefreshTemplates()));
        layout.Controls.Add(buttons, 0, 2); layout.Controls.Add(feedback, 0, 3); Controls.Add(layout);
        search.TextChanged += (_, _) => Filter();
        Shown += async (_, _) => await RefreshTemplates();
    }

    private async Task RefreshTemplates()
    {
        try { var data = await Task.Run(repository.List); if (!IsDisposed) { all = data; Filter(); feedback.Text = all.Count == 0 ? "No templates yet. Save a profile as a template from the dashboard." : "Select a template."; } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { if (!IsDisposed) feedback.Text = "Could not read templates: " + ex.Message; }
    }
    private void Filter()
    {
        var prior = Selected?.Id; var text = search.Text.Trim();
        var visible = all.Where(t => text.Length == 0 || t.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
            t.Description.Contains(text, StringComparison.OrdinalIgnoreCase) || t.Tags.Any(x => x.Contains(text, StringComparison.OrdinalIgnoreCase)) ||
            t.Folders.Any(x => x.Contains(text, StringComparison.OrdinalIgnoreCase))).ToArray();
        list.BeginUpdate(); try { list.Items.Clear(); foreach (var t in visible) list.Items.Add(new Item(t));
            var index = Array.FindIndex(visible, t => t.Id == prior); if (list.Items.Count > 0) list.SelectedIndex = Math.Max(index, 0); }
        finally { list.EndUpdate(); }
    }
    private void Edit()
    {
        var template = Selected; if (template is null) return;
        using var form = new TemplateEditorForm(repository, template);
        if (form.ShowDialog(this) == DialogResult.OK) _ = RefreshTemplates();
    }
    private async Task Duplicate()
    {
        var template = Selected; if (template is null) return;
        var name = template.Name[..Math.Min(template.Name.Length, 115)] + " copy";
        try { _ = await Task.Run(() => service.Duplicate(template, name)); await RefreshTemplates(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { if (!IsDisposed) feedback.Text = "Could not duplicate template: " + ex.Message; }
    }
    private async Task Delete()
    {
        var template = Selected; if (template is null) return;
        if (MessageBox.Show(this, "Delete template '" + template.Name + "'? Profiles created from it will remain.", "Delete template", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { await Task.Run(() => repository.Delete(template.Id, template.Revision)); await RefreshTemplates(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { if (!IsDisposed) feedback.Text = "Could not delete template: " + ex.Message; }
    }
    private void CreateProfile()
    {
        var template = Selected; if (template is null) return;
        using var form = new ProfileCopyForm("Create profile from template", template.Name, false,
            (slug, name, _, _, _) => service.CreateProfile(template, slug, name));
        if (form.ShowDialog(this) == DialogResult.OK) feedback.Text = "Created " + form.CreatedSlug + ".";
    }
}
