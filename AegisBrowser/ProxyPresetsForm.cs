using AegisBrowser.Core;

namespace AegisBrowser;

internal sealed class ProxyPresetsForm : Form
{
    private sealed record Item(ProxyPreset Value) { public override string ToString() => Value.Name + " · " + Value.Settings.Protocol; }
    private readonly IProxyPresetRepository repository;
    private readonly bool choose;
    private readonly Func<string, bool> confirm;
    private readonly IProxyPresetTransfer? transfer;
    private readonly Func<string?> chooseExportPath, chooseImportPath;
    private readonly TextBox search = new() { Name = "ProxyPresetSearch", AccessibleName = "Search proxy presets", Dock = DockStyle.Fill, PlaceholderText = "Search presets" };
    private readonly ListBox list = new() { Name = "ProxyPresetList", AccessibleName = "Proxy presets", Dock = DockStyle.Fill };
    private readonly TextBox name = new() { Name = "ProxyPresetName", AccessibleName = "Proxy preset name", Dock = DockStyle.Fill, MaxLength = 120 };
    private readonly TextBox description = new() { Name = "ProxyPresetDescription", AccessibleName = "Proxy preset description", Dock = DockStyle.Fill, MaxLength = 1000 };
    private readonly ComboBox protocol = new() { Name = "ProxyPresetProtocol", AccessibleName = "Proxy preset protocol", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox host = new() { Name = "ProxyPresetHost", AccessibleName = "Proxy preset host", Dock = DockStyle.Fill };
    private readonly NumericUpDown port = new() { Name = "ProxyPresetPort", AccessibleName = "Proxy preset port", Minimum = 1, Maximum = 65535, Value = 8080, Dock = DockStyle.Fill };
    private readonly TextBox label = new() { Name = "ProxyPresetLabel", AccessibleName = "Proxy preset endpoint label", Dock = DockStyle.Fill, MaxLength = 120 };
    private readonly Label feedback = new() { Name = "ProxyPresetFeedback", AccessibleName = "Proxy preset messages", Dock = DockStyle.Fill, AutoSize = true };
    private readonly Button create = new() { Name = "NewProxyPreset", Text = "New", AccessibleName = "Create a new proxy preset", AutoSize = true };
    private readonly Button edit = new() { Name = "EditProxyPreset", Text = "Edit selected", AccessibleName = "Edit selected proxy preset", AutoSize = true };
    private readonly Button duplicate = new() { Name = "DuplicateProxyPreset", Text = "Duplicate selected", AccessibleName = "Duplicate selected proxy preset", AutoSize = true };
    private readonly Button refresh = new() { Name = "RefreshProxyPresets", Text = "Refresh", AccessibleName = "Refresh proxy presets", AutoSize = true };
    private readonly Button save = new() { Name = "SaveProxyPreset", Text = "Save", AccessibleName = "Save proxy preset", AutoSize = true };
    private readonly Button delete = new() { Name = "DeleteProxyPreset", Text = "Delete selected", AccessibleName = "Delete selected proxy preset", AutoSize = true };
    private readonly Button use = new() { Name = "UseProxyPreset", Text = "Use selected", AccessibleName = "Copy selected proxy preset into bulk draft", AutoSize = true };
    private readonly Button export = new() { Name = "ExportProxyPreset", Text = "Export selected", AccessibleName = "Export selected proxy preset", AutoSize = true };
    private readonly Button import = new() { Name = "ImportProxyPreset", Text = "Import preset", AccessibleName = "Preview and import a proxy preset", AutoSize = true };
    private readonly Button close = new() { Text = "Close", AccessibleName = "Close proxy presets", AutoSize = true };
    private IReadOnlyList<ProxyPreset> all = [];
    private ProxyPreset? editing;
    private bool busy;
    public ProxyPreset? Chosen { get; private set; }

    public ProxyPresetsForm(IProxyPresetRepository repository, bool choose, Func<string, bool>? confirm = null,
        IProxyPresetTransfer? transfer = null, Func<string?>? chooseExportPath = null, Func<string?>? chooseImportPath = null)
    {
        this.repository = repository; this.choose = choose;
        this.transfer = transfer;
        this.chooseExportPath = chooseExportPath ?? ChooseExportPath;
        this.chooseImportPath = chooseImportPath ?? ChooseImportPath;
        this.confirm = confirm ?? (message => MessageBox.Show(this, message, "Proxy presets", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes);
        Text = "Proxy presets — saved drafts"; AutoScaleMode = AutoScaleMode.Dpi; StartPosition = FormStartPosition.CenterParent;
        Width = 800; Height = 680; MinimumSize = new Size(600, 540);
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(16) };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.Controls.Add(new Label { Name = "ProxyPresetWarning", AccessibleName = "Proxy preset limitations", Dock = DockStyle.Fill, AutoSize = true,
            Text = "Presets contain non-secret endpoint settings only. They do not activate a proxy. Exported files are unencrypted; " +
                   "do not put credentials in names, descriptions, labels or hosts." }, 0, 0);
        outer.Controls.Add(search, 0, 1);
        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38)); main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        main.Controls.Add(list, 0, 0);
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 8, Padding = new Padding(12, 0, 0, 0), AutoScroll = true };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 8; i++) fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        void Field(string text, Control control, int row)
        { fields.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); control.Margin = new Padding(0, 4, 0, 4); fields.Controls.Add(control, 1, row); }
        protocol.Items.AddRange(["HTTP — draft only", "HTTPS — draft only", "SOCKS5 — draft only"]); protocol.SelectedIndex = 0;
        Field("Name", name, 0); Field("Description", description, 1); Field("Protocol", protocol, 2);
        Field("Host", host, 3); Field("Port", port, 4); Field("Label", label, 5);
        fields.Controls.Add(feedback, 0, 6); fields.SetColumnSpan(feedback, 2);
        main.Controls.Add(fields, 1, 0); outer.Controls.Add(main, 0, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = true };
        buttons.Controls.AddRange([close, use, import, export, delete, save, duplicate, edit, create, refresh]); outer.Controls.Add(buttons, 0, 3); Controls.Add(outer);
        use.Visible = choose; import.Visible = export.Visible = transfer is not null; search.TextChanged += (_, _) => Filter();
        create.Click += (_, _) => ResetEditor(); edit.Click += (_, _) => EditSelected(); duplicate.Click += (_, _) => DuplicateSelected();
        refresh.Click += async (_, _) => await RefreshList();
        save.Click += async (_, _) => await Save(); delete.Click += async (_, _) => await DeleteSelected();
        export.Click += async (_, _) => await ExportSelected(); import.Click += async (_, _) => await ImportPreset();
        use.Click += async (_, _) => await UseSelected(); close.Click += (_, _) => Close();
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        Shown += async (_, _) => await LoadInitially();
    }

    private ProxyPreset? Selected => (list.SelectedItem as Item)?.Value;

    private async Task LoadInitially()
    {
        try { busy = true; SetBusy(true); feedback.Text = "Loading presets…"; await Reload(); if (!IsDisposed) feedback.Text = "Select or create a preset."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!IsDisposed) feedback.Text = "Could not load presets: " + ex.Message; }
        finally { busy = false; if (!IsDisposed) SetBusy(false); }
    }

    private async Task Reload(Guid? select = null)
    {
        var loaded = await Task.Run(repository.List);
        if (IsDisposed) return;
        all = loaded; Filter();
        if (select is Guid id)
            for (var i = 0; i < list.Items.Count; i++) if (list.Items[i] is Item item && item.Value.Id == id) { list.SelectedIndex = i; break; }
    }

    private void Filter()
    {
        var selected = Selected?.Id; var query = search.Text.Trim();
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (var preset in all.Where(item => query.Length == 0 || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Description.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Settings.Host.Contains(query, StringComparison.OrdinalIgnoreCase)))
                list.Items.Add(new Item(preset));
            if (selected is Guid id)
                for (var i = 0; i < list.Items.Count; i++) if (list.Items[i] is Item item && item.Value.Id == id) { list.SelectedIndex = i; break; }
        }
        finally { list.EndUpdate(); }
    }

    private void ResetEditor()
    {
        editing = null; name.Clear(); description.Clear(); host.Clear(); label.Clear(); port.Value = 8080; protocol.SelectedIndex = 0;
        feedback.Text = "Creating a new preset.";
    }

    private void EditSelected()
    {
        if (Selected is not { } selected) { feedback.Text = "Select a preset to edit."; return; }
        editing = selected; name.Text = selected.Name; description.Text = selected.Description; host.Text = selected.Settings.Host;
        label.Text = selected.Settings.Label; port.Value = selected.Settings.Port; protocol.SelectedIndex = (int)selected.Settings.Protocol - 1;
        feedback.Text = "Editing preset revision " + selected.Revision + ".";
    }

    private void DuplicateSelected()
    {
        if (Selected is not { } selected) { feedback.Text = "Select a preset to duplicate."; return; }
        editing = null; name.Text = selected.Name[..Math.Min(selected.Name.Length, 115)] + " copy";
        description.Text = selected.Description; host.Text = selected.Settings.Host; label.Text = selected.Settings.Label;
        port.Value = selected.Settings.Port; protocol.SelectedIndex = (int)selected.Settings.Protocol - 1;
        feedback.Text = "Review and save the new independent preset.";
    }

    private async Task RefreshList()
    {
        if (busy) return;
        try
        {
            busy = true; SetBusy(true); var selected = Selected?.Id; await Reload(selected);
            if (!IsDisposed) feedback.Text = "Presets refreshed. Choose Edit selected to load current values.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!IsDisposed) feedback.Text = "Could not refresh presets: " + ex.Message; }
        finally { busy = false; if (!IsDisposed) SetBusy(false); }
    }

    private async Task Save()
    {
        if (busy) return;
        try
        {
            var draft = new ProxyDraft((ProxyProtocol)(protocol.SelectedIndex + 1), host.Text.Trim(), decimal.ToInt32(port.Value), false, null, label.Text.Trim(), true);
            ProxyValidation.Validate(draft);
            var draftName = name.Text.Trim(); var draftDescription = description.Text;
            busy = true; SetBusy(true); feedback.Text = "Saving preset…";
            var saved = await Task.Run(() => editing is null
                ? repository.Create(draftName, draftDescription, draft)
                : repository.Update(editing with { Name = draftName, Description = draftDescription, Settings = draft }, editing.Revision));
            if (IsDisposed) return;
            editing = saved; await Reload(saved.Id); feedback.Text = "Preset saved. It is not an active proxy.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!IsDisposed) feedback.Text = "Could not save preset: " + ex.Message; }
        finally { busy = false; if (!IsDisposed) SetBusy(false); }
    }

    private async Task DeleteSelected()
    {
        if (busy || Selected is not { } selected) { if (!busy) feedback.Text = "Select a preset to delete."; return; }
        if (!confirm("Delete the selected proxy preset? Existing profile settings are unchanged.")) return;
        try
        {
            busy = true; SetBusy(true); feedback.Text = "Deleting preset…";
            await Task.Run(() => repository.Delete(selected.Id, selected.Revision));
            if (IsDisposed) return;
            if (editing?.Id == selected.Id) ResetEditor();
            await Reload(); feedback.Text = "Preset deleted; profile settings were not changed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!IsDisposed) feedback.Text = "Could not delete preset: " + ex.Message; }
        finally { busy = false; if (!IsDisposed) SetBusy(false); }
    }

    private async Task UseSelected()
    {
        if (!choose || busy || Selected is not { } selected) { if (!busy) feedback.Text = "Select a preset to use."; return; }
        try
        {
            busy = true; SetBusy(true);
            var current = await Task.Run(() => repository.List().SingleOrDefault(item => item.Id == selected.Id));
            if (current is null || current != selected) throw new IOException("Preset changed; refresh and select it again.");
            if (IsDisposed) return;
            Chosen = current; busy = false; DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!IsDisposed) feedback.Text = "Could not use preset: " + ex.Message; }
        finally { busy = false; if (!IsDisposed) SetBusy(false); }
    }

    private string? ChooseExportPath()
    {
        using var dialog = new SaveFileDialog { Title = "Export proxy preset to a new file", Filter = "Aegis proxy preset (*.json)|*.json",
            FileName = "aegis-proxy-preset.json", OverwritePrompt = false };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    private string? ChooseImportPath()
    {
        using var dialog = new OpenFileDialog { Title = "Preview proxy preset", Filter = "Aegis proxy preset (*.json)|*.json" };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    private async Task ExportSelected()
    {
        if (busy || transfer is null || Selected is not { } selected) { if (!busy) feedback.Text = "Select a preset to export."; return; }
        var path = chooseExportPath(); if (path is null) return;
        try
        {
            busy = true; SetBusy(true); feedback.Text = "Exporting preset…";
            await Task.Run(() => transfer.Export(path, selected));
            if (!IsDisposed) feedback.Text = "Preset exported without credentials. Existing files were not replaced.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!IsDisposed) feedback.Text = "Could not export preset: " + ex.Message; }
        finally { busy = false; if (!IsDisposed) SetBusy(false); }
    }

    private async Task ImportPreset()
    {
        if (busy || transfer is null) return;
        var path = chooseImportPath(); if (path is null) return;
        try
        {
            busy = true; SetBusy(true); feedback.Text = "Validating preset…";
            var preview = await Task.Run(() => transfer.Preview(path));
            if (IsDisposed) return;
            var prompt = $"Import preset '{preview.Name}' ({preview.Protocol}, {preview.Host}:{preview.Port})? " +
                "This creates a new inactive preset without credentials. The checksum does not prove who created the file.";
            if (!confirm(prompt)) { feedback.Text = "Preset import canceled."; return; }
            var created = await Task.Run(() => transfer.Import(preview));
            if (IsDisposed) return;
            await Reload(created.Id); feedback.Text = "Preset imported with a new ID. No profile was changed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!IsDisposed) feedback.Text = "Could not import preset: " + ex.Message; }
        finally { busy = false; if (!IsDisposed) SetBusy(false); }
    }

    private void SetBusy(bool value)
    {
        search.Enabled = list.Enabled = name.Enabled = description.Enabled = protocol.Enabled = host.Enabled = port.Enabled = label.Enabled = !value;
        create.Enabled = edit.Enabled = duplicate.Enabled = refresh.Enabled = save.Enabled = delete.Enabled = use.Enabled = close.Enabled = !value;
        import.Enabled = export.Enabled = !value && transfer is not null;
    }
}
