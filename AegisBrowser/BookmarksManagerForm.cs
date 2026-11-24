using System.Text.Json;
using AegisBrowser.Core;

namespace AegisBrowser;

internal sealed class BookmarksManagerForm : Form
{
    private readonly BookmarkStore store;
    private readonly Action<string> open, newTab;
    private readonly TreeView tree = new() { Name = "BookmarkFolders", Dock = DockStyle.Fill, AccessibleName = "Bookmark folders", HideSelection = false };
    private readonly ListView list = new() { Name = "BookmarkList", Dock = DockStyle.Fill, AccessibleName = "Bookmarks", View = View.Details, FullRowSelect = true, MultiSelect = false };
    private readonly TextBox search = new() { Name = "BookmarkSearch", Dock = DockStyle.Fill, AccessibleName = "Search bookmarks", PlaceholderText = "Search title, host or path" };
    private readonly Label message = new() { AutoSize = true, AccessibleName = "Bookmark messages" };
    private bool updating;

    internal BookmarksManagerForm(BookmarkStore store, Action<string> open, Action<string> newTab)
    {
        this.store = store; this.open = open; this.newTab = newTab;
        Text = "Bookmarks"; DesktopStyle.Apply(this); StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(900, 600); MinimumSize = new Size(520, 360); KeyPreview = true;
        list.Columns.Add("Title", 260); list.Columns.Add("Address", 420);
        list.Resize += (_, _) => { if (list.ClientSize.Width > 100) { list.Columns[0].Width = list.ClientSize.Width * 2 / 5; list.Columns[1].Width = list.ClientSize.Width * 3 / 5 - SystemInformation.VerticalScrollBarWidth; } };
        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        void Button(string text, Action action)
        {
            var button = new Button { Text = text, AccessibleName = text, AutoSize = true };
            DesktopStyle.Button(button); button.Click += (_, _) => action(); commands.Controls.Add(button);
        }
        Button("Open", () => Open(false)); Button("Open in new tab", () => Open(true));
        Button("Add", Add); Button("Edit", Edit); Button("Remove", Remove);
        Button("New folder", NewFolder); Button("Rename folder", RenameFolder); Button("Delete folder", DeleteFolder);
        Button("Import", Import); Button("Export", Export);
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        split.ColumnStyles.Add(new(SizeType.Percent, 25)); split.ColumnStyles.Add(new(SizeType.Percent, 75));
        split.Controls.Add(tree, 0, 0); split.Controls.Add(list, 1, 0);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.Controls.Add(search, 0, 0); layout.Controls.Add(commands, 0, 1); layout.Controls.Add(split, 0, 2); layout.Controls.Add(message, 0, 3);
        Controls.Add(layout);
        search.TextChanged += (_, _) => RefreshList();
        tree.AfterSelect += (_, _) => { if (!updating) RefreshList(); };
        list.DoubleClick += (_, _) => Open(false);
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && list.Focused) { Open(false); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete && list.Focused) { Remove(); e.Handled = true; }
            else if (e.KeyCode == Keys.F2) { if (tree.Focused) RenameFolder(); else if (list.Focused) Edit(); e.Handled = true; }
        };
        store.Changed += StoreChanged;
        FormClosed += (_, _) => store.Changed -= StoreChanged;
        RefreshFolders();
    }

    private void StoreChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { if (InvokeRequired) BeginInvoke((Action)RefreshFolders); else RefreshFolders(); }
        catch (InvalidOperationException) { }
    }
    private Guid? Folder => tree.SelectedNode?.Tag is Guid id ? id : null;
    private BookmarkEntry? Selected => list.SelectedItems.Count == 0 ? null : list.SelectedItems[0].Tag as BookmarkEntry;
    private void RefreshFolders()
    {
        var selected = Folder;
        updating = true;
        try
        {
            tree.Nodes.Clear();
            var root = tree.Nodes.Add("Bookmarks"); root.Tag = null;
            foreach (var folder in store.Snapshot().Folders.OrderBy(f => f.Order))
            { var node = root.Nodes.Add(folder.Name); node.Tag = folder.Id; if (folder.Id == selected) tree.SelectedNode = node; }
            root.Expand(); tree.SelectedNode ??= root;
        }
        finally { updating = false; }
        RefreshList();
    }
    private void RefreshList()
    {
        var query = search.Text.Trim();
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (var entry in store.Snapshot().Items.Where(x => x.FolderId == Folder).OrderBy(x => x.Order))
            {
                var uri = new Uri(entry.Url);
                if (query.Length > 0 && !entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !uri.Host.Contains(query, StringComparison.OrdinalIgnoreCase) && !uri.AbsolutePath.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                var row = new ListViewItem(entry.Title) { Tag = entry }; row.SubItems.Add(entry.Url); list.Items.Add(row);
            }
        }
        finally { list.EndUpdate(); }
    }
    private void Open(bool separate)
    {
        if (Selected is not { } item) return;
        if (separate) newTab(item.Url); else open(item.Url);
    }
    private void Add() { using var editor = new BookmarkEditorForm(store, null, "", ""); editor.ShowDialog(this); }
    private void Edit() { if (Selected is { } item) { using var editor = new BookmarkEditorForm(store, item, item.Url, item.Title); editor.ShowDialog(this); } }
    private void Remove()
    {
        if (Selected is not { } item || MessageBox.Show(this, "Remove this bookmark?", "Remove bookmark", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        Save(d => d.Items.RemoveAll(x => x.Id == item.Id));
    }
    private string? Prompt(string caption, string initial)
    {
        using var dialog = new Form { Text = caption, StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        DesktopStyle.Apply(dialog);
        var input = new TextBox { Text = initial, Width = 300, AccessibleName = "Folder name" };
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var panel = new FlowLayoutPanel { AutoSize = true, Padding = new Padding(12) };
        panel.Controls.AddRange([input, save, cancel]); dialog.Controls.Add(panel); dialog.AcceptButton = save; dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK ? input.Text : null;
    }
    private void Save(Action<BookmarkDocument> change)
    {
        try { var document = store.Snapshot(); change(document); store.Save(document); message.Text = ""; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { message.Text = ex.Message; }
    }
    private void NewFolder()
    {
        var name = Prompt("New folder", "");
        if (name is null) return;
        Save(d => { var text = BookmarkStore.CleanText(name); if (text.Length == 0) throw new ArgumentException("Enter a folder name."); var now = DateTimeOffset.UtcNow; d.Folders.Add(new(Guid.NewGuid(), text, now, now, d.Folders.Count)); });
    }
    private void RenameFolder()
    {
        var id = Folder;
        if (id is null) return;
        var folder = store.Snapshot().Folders.Single(f => f.Id == id);
        var name = Prompt("Rename folder", folder.Name);
        if (name is null) return;
        Save(d => { var text = BookmarkStore.CleanText(name); if (text.Length == 0) throw new ArgumentException("Enter a folder name."); var index = d.Folders.FindIndex(f => f.Id == id); d.Folders[index] = d.Folders[index] with { Name = text, ModifiedUtc = DateTimeOffset.UtcNow }; });
    }
    private void DeleteFolder()
    {
        var id = Folder;
        if (id is null || MessageBox.Show(this, "Delete this folder and all bookmarks in it?", "Delete folder", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        Save(d => { d.Items.RemoveAll(x => x.FolderId == id); d.Folders.RemoveAll(x => x.Id == id); });
    }
    private void Export()
    {
        using var dialog = new SaveFileDialog { InitialDirectory = store.ProfileDirectory, Filter = "Bookmark JSON|*.json", AddExtension = true, OverwritePrompt = true, Title = "Export inside this profile directory" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { store.Export(dialog.FileName); message.Text = "Bookmarks exported."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { message.Text = ex.Message; }
    }
    private void Import()
    {
        using var dialog = new OpenFileDialog { InitialDirectory = store.ProfileDirectory, Filter = "Bookmark JSON|*.json", Title = "Import from this profile directory" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var incoming = store.ReadImport(dialog.FileName);
            var accepted = new List<(string Title, string Url)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var rejected = 0; var duplicates = 0;
            var rejectedRows = new List<int>();
            var rowNumber = 0;
            foreach (var item in incoming.Items)
            {
                rowNumber++;
                try
                {
                    var url = BookmarkStore.NormalizeUrl(item.Url);
                    var title = BookmarkStore.CleanText(item.Title);
                    if (title.Length == 0) title = new Uri(url).Host;
                    if (!seen.Add(url)) { duplicates++; continue; }
                    accepted.Add((title, url));
                }
                catch (Exception ex) when (ex is ArgumentException or NullReferenceException) { rejected++; rejectedRows.Add(rowNumber); }
            }
            if (MessageBox.Show(this, $"Import into a new folder named Imported bookmarks?\nNew: {accepted.Count}\nDuplicates: {duplicates}\nRejected: {rejected}\nRejected item numbers: {(rejectedRows.Count == 0 ? "none" : string.Join(", ", rejectedRows))}", "Preview import", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            Save(d =>
            {
                var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid();
                d.Folders.Add(new(id, "Imported bookmarks", now, now, d.Folders.Count));
                foreach (var entry in accepted) d.Items.Add(new(Guid.NewGuid(), entry.Title, entry.Url, id, now, now, d.Items.Count));
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException) { message.Text = "Import failed: " + ex.Message; }
    }
}
