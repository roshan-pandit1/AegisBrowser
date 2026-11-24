using AegisBrowser.Core;

namespace AegisBrowser;

internal sealed class BookmarkEditorForm : Form
{
    internal BookmarkEditorForm(BookmarkStore store, BookmarkEntry? item, string url, string title)
    {
        Text = item is null ? "Add bookmark" : "Edit bookmark";
        DesktopStyle.Apply(this);
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 270);
        var name = new TextBox { Name = "BookmarkTitle", Text = item?.Title ?? title, Dock = DockStyle.Fill, AccessibleName = "Bookmark title" };
        var address = new TextBox { Name = "BookmarkUrl", Text = item?.Url ?? url, Dock = DockStyle.Fill, AccessibleName = "Bookmark address" };
        var folders = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, AccessibleName = "Bookmark folder" };
        var snapshot = store.Snapshot();
        folders.Items.Add(new FolderChoice(null, "Bookmarks"));
        foreach (var folder in snapshot.Folders.OrderBy(f => f.Order)) folders.Items.Add(new FolderChoice(folder.Id, folder.Name));
        folders.SelectedIndex = Math.Max(0, Enumerable.Range(0, folders.Items.Count).FirstOrDefault(i => ((FolderChoice)folders.Items[i]!).Id == item?.FolderId));
        var message = new Label { AutoSize = true, ForeColor = Color.DarkRed, AccessibleName = "Bookmark editor messages" };
        var save = new Button { Name = "SaveBookmark", Text = "Save", AutoSize = true, AccessibleName = "Save bookmark" };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, AccessibleName = "Cancel bookmark editing" };
        var remove = new Button { Text = "Remove", AutoSize = true, Visible = item is not null, AccessibleName = "Remove bookmark" };
        DesktopStyle.Button(save, true);
        save.Click += (_, _) =>
        {
            try
            {
                var normalized = BookmarkStore.NormalizeUrl(address.Text);
                var clean = BookmarkStore.CleanText(name.Text);
                if (clean.Length == 0) clean = new Uri(normalized).Host;
                var folder = ((FolderChoice)folders.SelectedItem!).Id;
                var current = store.Snapshot();
                if (current.Items.Any(entry => entry.Id != item?.Id && entry.FolderId == folder && entry.Url == normalized))
                    throw new ArgumentException("This folder already contains that address.");
                var now = DateTimeOffset.UtcNow;
                var updated = new BookmarkEntry(item?.Id ?? Guid.NewGuid(), clean, normalized, folder, item?.CreatedUtc ?? now, now, item?.Order ?? current.Items.Count);
                current.Items.RemoveAll(entry => entry.Id == updated.Id);
                current.Items.Add(updated);
                store.Save(current);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            { message.Text = ex.Message; }
        };
        remove.Click += (_, _) =>
        {
            if (item is null || MessageBox.Show(this, "Remove this bookmark?", "Remove bookmark", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try { var current = store.Snapshot(); current.Items.RemoveAll(entry => entry.Id == item.Id); store.Save(current); DialogResult = DialogResult.OK; Close(); }
            catch (IOException ex) { message.Text = ex.Message; }
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 8 };
        foreach (var control in new Control[] { new Label { Text = "Title", AutoSize = true }, name, new Label { Text = "Address", AutoSize = true }, address, new Label { Text = "Folder", AutoSize = true }, folders, message })
            layout.Controls.Add(control);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([cancel, save, remove]); layout.Controls.Add(buttons);
        Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
    }
}

internal sealed record FolderChoice(Guid? Id, string Name)
{
    public override string ToString() => Name;
}
