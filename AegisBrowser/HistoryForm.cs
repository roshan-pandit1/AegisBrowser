using AegisBrowser.Core;

namespace AegisBrowser;

internal sealed class HistoryForm : Form
{
    private readonly SessionHistoryService history;
    private readonly Action<string> openCurrent, openNew;
    private readonly TextBox search = new() { Dock = DockStyle.Top, AccessibleName = "Search session history", AccessibleDescription = "Filter by title, hostname or address.", PlaceholderText = "Search this session" };
    private readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false, AccessibleName = "Visited pages", AccessibleDescription = "Session visits, newest first." };
    private readonly Label empty = new() { Dock = DockStyle.Fill, Text = "No matching pages in this session.", TextAlign = ContentAlignment.MiddleCenter, AccessibleName = "No matching history" };
    private readonly Button pause = new() { AutoSize = true, AccessibleName = "Pause history recording" };
    private readonly Label recording = new() { AutoSize = true, Anchor = AnchorStyles.Left, AccessibleName = "History recording status" };

    internal HistoryForm(SessionHistoryService history, Action<string> openCurrent, Action<string> openNew)
    {
        this.history = history;
        this.openCurrent = openCurrent;
        this.openNew = openNew;
        Text = "Session history";
        AccessibleName = "Session history";
        DesktopStyle.Apply(this);
        ClientSize = new Size(900, 600);
        MinimumSize = new Size(520, 320);
        list.Columns.Add("Visited"); list.Columns.Add("Title"); list.Columns.Add("Site");
        list.Name = "HistoryList";
        search.Name = "HistorySearch";
        pause.Name = "HistoryPause";
        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8), AutoSize = false, AccessibleName = "History controls" };
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        main.Controls.Add(search, 0, 0);
        var results = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8), AccessibleName = "History results" };
        results.Controls.Add(list); results.Controls.Add(empty);
        main.Controls.Add(results, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty, AccessibleName = "History actions" };
        Button Action(string text, string name, Action action)
        {
            var button = new Button { Text = text, Name = name, AccessibleName = text, AccessibleDescription = text + " in session history.", AutoSize = true };
            DesktopStyle.Button(button);
            button.Click += (_, _) => action();
            return button;
        }
        actions.Controls.Add(Action("Open", "HistoryOpen", () => Open(false)));
        actions.Controls.Add(Action("Open in new tab", "HistoryOpenNew", () => Open(true)));
        actions.Controls.Add(Action("Delete selected", "HistoryDelete", DeleteSelected));
        actions.Controls.Add(Action("Clear all", "HistoryClear", Clear));
        DesktopStyle.Button(pause);
        pause.Click += (_, _) => history.SetPaused(!history.IsPaused);
        actions.Controls.Add(pause);
        actions.Controls.Add(recording);
        main.Controls.Add(actions, 0, 2);
        var notice = new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(900, 0),
            Text = "Session-only history is not private browsing. Clearing it does not clear Chromium caches or cookies, or hide activity from websites, the network or the operating system.",
            AccessibleName = "Session history privacy information"
        };
        main.Controls.Add(notice, 0, 3);
        Controls.Add(main);
        search.TextChanged += (_, _) => RefreshEntries();
        list.DoubleClick += (_, _) => Open(false);
        list.Resize += (_, _) => ResizeColumns();
        history.Changed += HistoryChanged;
        FormClosed += (_, _) => history.Changed -= HistoryChanged;
        Shown += (_, _) => RefreshEntries();
        RefreshEntries();
    }

    private void HistoryChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke((Action)(() => { if (!IsDisposed && !Disposing) RefreshEntries(); })); }
        catch (InvalidOperationException) { }
    }

    private void RefreshEntries()
    {
        if (IsDisposed) return;
        var selected = list.SelectedItems.Cast<ListViewItem>().Select(item => (Guid)item.Tag!).ToHashSet();
        var values = history.Snapshot(search.Text);
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            foreach (var value in values)
            {
                var item = new ListViewItem(value.VisitedUtc.ToLocalTime().ToString("g")) { Tag = value.Id };
                item.SubItems.Add(value.Title); item.SubItems.Add(new Uri(value.Url).Host);
                list.Items.Add(item); item.Selected = selected.Contains(value.Id);
            }
        }
        finally { list.EndUpdate(); }
        empty.Visible = values.Count == 0;
        list.Visible = values.Count != 0;
        pause.Text = history.IsPaused ? "Resume recording" : "Pause recording";
        pause.AccessibleName = pause.Text;
        pause.AccessibleDescription = history.IsPaused ? "History recording is paused." : "History recording is active.";
        recording.Text = history.IsPaused ? "Recording: paused" : "Recording: active";
        recording.AccessibleDescription = recording.Text;
        ResizeColumns();
    }

    private void ResizeColumns()
    {
        if (list.Columns.Count != 3) return;
        var width = Math.Max(180, list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
        list.Columns[0].Width = width * 35 / 100;
        list.Columns[1].Width = width * 35 / 100;
        list.Columns[2].Width = width * 30 / 100;
    }

    private void Open(bool newTab)
    {
        if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not Guid id) return;
        var item = history.Snapshot().FirstOrDefault(entry => entry.Id == id);
        if (item is null) return;
        if (newTab) openNew(item.Url); else openCurrent(item.Url);
    }
    private void DeleteSelected() => history.Remove(list.SelectedItems.Cast<ListViewItem>().Select(item => (Guid)item.Tag!).ToArray());
    private void Clear()
    {
        if (MessageBox.Show(this, "Clear all pages in this session history? Chromium caches and cookies are not affected.",
            "Clear session history", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) history.Clear();
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape: Close(); return true;
            case Keys.Control | Keys.F: search.Focus(); search.SelectAll(); return true;
            case Keys.Enter when list.Focused: Open(false); return true;
            case Keys.Delete when list.Focused: DeleteSelected(); return true;
            default: return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
