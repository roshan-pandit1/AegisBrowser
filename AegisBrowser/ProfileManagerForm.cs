using System.ComponentModel;
using AegisBrowser.Core;

namespace AegisBrowser;

/// <summary>Profile dashboard. Browser processes retain their own native close lifecycle.</summary>
public sealed class ProfileManagerForm : Form
{
    private readonly ProfileProcessSupervisor processes;
    private readonly ProfileDashboard dashboard;
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 3000 };
    private readonly TextBox search = new() { Name = "ProfileSearch", AccessibleName = "Search profiles, tags and folders", Width = 220, PlaceholderText = "Search profiles, tags, folders" };
    private readonly ComboBox statusFilter = new() { Name = "StatusFilter", AccessibleName = "Filter by status", DropDownStyle = ComboBoxStyle.DropDownList, Width = 125 };
    private readonly ComboBox folderFilter = new() { Name = "FolderFilter", AccessibleName = "Filter by folder", DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly ComboBox sort = new() { Name = "SortProfiles", AccessibleName = "Sort profiles", DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly DataGridView grid = new()
    {
        Name = "ProfileGrid", AccessibleName = "Profiles", Dock = DockStyle.Fill, ReadOnly = true,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false,
        BackgroundColor = Color.White, BorderStyle = BorderStyle.None, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
    };
    private readonly Label summary = new() { Dock = DockStyle.Fill, AccessibleName = "Profile list status", AutoSize = true };
    private readonly Label feedback = new() { Name = "DashboardFeedback", AccessibleName = "Dashboard messages", Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly TextBox details = new() { Name = "ProfileDetails", AccessibleName = "Selected profile details", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle };
    private readonly Button openDetails = new() { Text = "Launch", AccessibleName = "Launch selected profile", AutoSize = true };
    private readonly Button editDetails = new() { Text = "Edit metadata", AccessibleName = "Edit selected profile", AutoSize = true };
    private readonly ContextMenuStrip actions = new();
    private IReadOnlyList<DashboardProfile> allProfiles = [];
    private bool loading, pendingRefresh, filterUpdating;
    private string? pendingSelection;

    public ProfileManagerForm(string root)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("Could not find the application executable.");
        processes = new ProfileProcessSupervisor(new ProfileLeaseProbe(root),
            new ProfileChildStarter(executable, typeof(ProfileManagerForm).Assembly.Location, AppContext.BaseDirectory));
        dashboard = new ProfileDashboard(root, new ProfileRepository(root), processes);
        Text = "Aegis Browser — Profiles";
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1300; Height = 760; MinimumSize = new Size(900, 560);
        KeyPreview = true;
        BuildLayout();
        BindEvents();
        summary.Text = "Loading profiles…";
        Shown += async (_, _) => { await RefreshProfiles(); refreshTimer.Start(); };
        FormClosed += (_, _) => { refreshTimer.Stop(); refreshTimer.Dispose(); processes.Dispose(); actions.Dispose(); };
    }

    private void BuildLayout()
    {
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.White };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var sidebar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16), BackColor = Color.FromArgb(28, 39, 57), AutoScroll = true };
        sidebar.Controls.Add(new Label { Text = "AEGIS BROWSER", ForeColor = Color.White, Font = new Font(Font.FontFamily, 12, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 4, 0, 24) });
        sidebar.Controls.Add(new Label { Text = "Profiles", ForeColor = Color.White, AutoSize = true, Margin = new Padding(0, 0, 0, 14) });
        var create = new Button { Name = "CreateProfile", Text = "+ New profile", AccessibleName = "Create profile", Width = 150, Height = 38 };
        var refresh = new Button { Text = "Refresh", AccessibleName = "Refresh profiles", Width = 150, Height = 36 };
        create.Click += (_, _) => CreateProfile();
        refresh.Click += async (_, _) => await RefreshProfiles(manual: true);
        sidebar.Controls.Add(create); sidebar.Controls.Add(refresh);
        sidebar.Controls.Add(new Label { Text = "Local profiles\nEach profile uses its own browser storage.", ForeColor = Color.Gainsboro, Width = 150, Height = 70, Margin = new Padding(0, 24, 0, 0) });

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new Padding(16) };
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize)); content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        content.Controls.Add(new Label { Text = "Browser profiles", AccessibleName = "Browser profiles dashboard", Font = new Font(Font.FontFamily, 17, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 10) }, 0, 0);
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
        statusFilter.Items.AddRange(["All statuses", "Running / in use", "Stopped"]); statusFilter.SelectedIndex = 0;
        sort.Items.AddRange(["Name A–Z", "Recently updated"]); sort.SelectedIndex = 0;
        folderFilter.Items.Add("All folders"); folderFilter.SelectedIndex = 0;
        var clear = new Button { Text = "Clear filters", AccessibleName = "Clear profile filters", AutoSize = true };
        clear.Click += (_, _) => { search.Clear(); statusFilter.SelectedIndex = 0; folderFilter.SelectedIndex = 0; sort.SelectedIndex = 0; };
        tools.Controls.AddRange([search, statusFilter, folderFilter, sort, clear]);
        content.Controls.Add(tools, 0, 1); content.Controls.Add(summary, 0, 2);
        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 8, 0, 0) };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "DisplayName", HeaderText = "Name", Width = 170 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ProfileId", HeaderText = "Profile ID", Width = 250 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Slug", HeaderText = "Storage slug", Width = 125 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Tags", HeaderText = "Tags", Width = 140 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Folders", HeaderText = "Folders", Width = 140 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Updated", HeaderText = "Updated", Width = 150 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", Width = 110 });
        grid.Columns.Add(new DataGridViewButtonColumn { Name = "Launch", HeaderText = "", Text = "Launch", UseColumnTextForButtonValue = true, Width = 75 });
        grid.Columns.Add(new DataGridViewButtonColumn { Name = "Edit", HeaderText = "", Text = "Edit", UseColumnTextForButtonValue = true, Width = 60 });
        grid.Columns.Add(new DataGridViewButtonColumn { Name = "Actions", HeaderText = "", Text = "Actions", UseColumnTextForButtonValue = true, Width = 75 });
        main.Controls.Add(grid, 0, 0);
        var detailPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12, 0, 0, 0) };
        detailPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); detailPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); detailPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        detailPanel.Controls.Add(new Label { Text = "Profile details", Font = new Font(Font, FontStyle.Bold), AutoSize = true }, 0, 0);
        detailPanel.Controls.Add(details, 0, 1);
        var detailButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        detailButtons.Controls.AddRange([openDetails, editDetails]); detailPanel.Controls.Add(detailButtons, 0, 2);
        main.Controls.Add(detailPanel, 1, 0); content.Controls.Add(main, 0, 3); content.Controls.Add(feedback, 0, 4);
        shell.Controls.Add(sidebar, 0, 0); shell.Controls.Add(content, 1, 0); Controls.Add(shell);
        actions.Items.Add("Launch", null, async (_, _) => await LaunchSelected());
        actions.Items.Add("Edit metadata", null, async (_, _) => await EditSelected());
        actions.Items.Add("Refresh", null, async (_, _) => await RefreshProfiles(manual: true));
    }

    private void BindEvents()
    {
        search.TextChanged += (_, _) => ApplyFilters();
        statusFilter.SelectedIndexChanged += (_, _) => ApplyFilters();
        folderFilter.SelectedIndexChanged += (_, _) => { if (!filterUpdating) ApplyFilters(); };
        sort.SelectedIndexChanged += (_, _) => ApplyFilters();
        grid.SelectionChanged += (_, _) => ShowDetails();
        grid.CellDoubleClick += async (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && grid.Columns[e.ColumnIndex] is not DataGridViewButtonColumn)
                await LaunchSelected();
        };
        grid.CellContentClick += async (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            grid.ClearSelection(); grid.Rows[e.RowIndex].Selected = true;
            switch (grid.Columns[e.ColumnIndex].Name)
            {
                case "Launch": await LaunchSelected(); break;
                case "Edit": await EditSelected(); break;
                case "Actions":
                    var bounds = grid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, true);
                    actions.Show(grid.PointToScreen(new Point(bounds.Left, bounds.Bottom))); break;
            }
        };
        grid.CellMouseDown += (_, e) => { if (e.Button == MouseButtons.Right && e.RowIndex >= 0) { grid.ClearSelection(); grid.Rows[e.RowIndex].Selected = true; actions.Show(Cursor.Position); } };
        openDetails.Click += async (_, _) => await LaunchSelected();
        editDetails.Click += async (_, _) => await EditSelected();
        refreshTimer.Tick += async (_, _) => await RefreshProfiles();
        KeyDown += async (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.F) { search.Focus(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.N) { CreateProfile(); e.Handled = true; }
            else if (e.KeyCode == Keys.F5) { await RefreshProfiles(manual: true); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter && grid.Focused)
            {
                e.Handled = true; e.SuppressKeyPress = true;
                var cell = grid.CurrentCell;
                if (cell is not null && grid.Columns[cell.ColumnIndex].Name == "Edit") await EditSelected();
                else if (cell is not null && grid.Columns[cell.ColumnIndex].Name == "Actions")
                {
                    var bounds = grid.GetCellDisplayRectangle(cell.ColumnIndex, cell.RowIndex, true);
                    actions.Show(grid.PointToScreen(new Point(bounds.Left, bounds.Bottom)));
                }
                else await LaunchSelected();
            }
        };
    }

    private DashboardProfile? Selected => grid.SelectedRows.Count == 0 ? null : grid.SelectedRows[0].Tag as DashboardProfile;

    private async Task RefreshProfiles(string? select = null, bool manual = false)
    {
        if (loading) { pendingRefresh = true; pendingSelection = select ?? pendingSelection; return; }
        loading = true;
        try
        {
            var loaded = await Task.Run(dashboard.Load);
            if (IsDisposed) return;
            allProfiles = loaded;
            UpdateFolders();
            ApplyFilters(select);
            if (manual) feedback.Text = "Profiles refreshed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (!IsDisposed) { summary.Text = "Could not refresh profiles."; feedback.Text = ex.Message; }
        }
        finally
        {
            loading = false;
            if (!IsDisposed && pendingRefresh)
            {
                var next = pendingSelection; pendingSelection = null; pendingRefresh = false;
                BeginInvoke(new Action(async () => await RefreshProfiles(next)));
            }
        }
    }

    private void UpdateFolders()
    {
        var selected = folderFilter.SelectedIndex > 0 ? folderFilter.SelectedItem as string : null;
        var folders = allProfiles.SelectMany(p => p.Metadata?.Folders ?? []).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
        filterUpdating = true;
        try
        {
            folderFilter.Items.Clear(); folderFilter.Items.Add("All folders"); folderFilter.Items.AddRange(folders);
            folderFilter.SelectedIndex = selected is null ? 0 :
                Array.FindIndex(folders, value => string.Equals(value, selected, StringComparison.OrdinalIgnoreCase)) + 1;
        }
        finally { filterUpdating = false; }
    }

    private void ApplyFilters(string? select = null)
    {
        if (filterUpdating || statusFilter.SelectedIndex < 0 || sort.SelectedIndex < 0) return;
        var previous = select ?? Selected?.Slug;
        var filter = statusFilter.SelectedIndex switch { 1 => DashboardStatusFilter.Running, 2 => DashboardStatusFilter.Stopped, _ => DashboardStatusFilter.All };
        var folder = folderFilter.SelectedIndex > 0 ? folderFilter.SelectedItem as string : null;
        var rows = ProfileDashboard.Filter(allProfiles, search.Text, filter, folder, sort.SelectedIndex == 1 ? DashboardSort.Updated : DashboardSort.Name);
        // Periodic status refreshes must not steal keyboard focus or reset the scroll position.
        var unchanged = rows.Count == grid.Rows.Count && rows.Select((profile, index) =>
        {
            var old = grid.Rows[index].Tag as DashboardProfile;
            return old is not null && old.Slug == profile.Slug && old.Metadata?.Revision == profile.Metadata?.Revision &&
                old.Metadata?.Id == profile.Metadata?.Id && old.Status == profile.Status;
        }).All(equal => equal);
        if (unchanged && (select is null || Selected?.Slug == select))
        {
            for (var i = 0; i < rows.Count; i++) grid.Rows[i].Tag = rows[i];
            summary.Text = rows.Count == 0 ? "No profiles match these filters. Clear filters or create a profile." : $"{rows.Count} profile(s) shown · Refreshes every 3 seconds";
            ShowDetails();
            return;
        }
        var scroll = grid.Rows.Count > 0 ? grid.FirstDisplayedScrollingRowIndex : -1;
        var column = grid.CurrentCell?.ColumnIndex ?? 0;
        grid.SuspendLayout();
        try
        {
            grid.Rows.Clear();
            foreach (var profile in rows)
            {
                var metadata = profile.Metadata;
                var index = grid.Rows.Add(profile.DisplayName, metadata?.Id.ToString("D") ?? "Not registered", profile.Slug,
                    metadata is null ? "" : string.Join(", ", metadata.Tags), metadata is null ? "" : string.Join(", ", metadata.Folders),
                    metadata?.UpdatedUtc.ToLocalTime().ToString("g") ?? "—", StatusText(profile.Status.Activity));
                grid.Rows[index].Tag = profile;
            }
            grid.ClearSelection();
            var selected = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (r.Tag as DashboardProfile)?.Slug == previous);
            if (selected is null && grid.Rows.Count > 0) selected = grid.Rows[0];
            if (selected is not null) { selected.Selected = true; grid.CurrentCell = selected.Cells[Math.Clamp(column, 0, grid.Columns.Count - 1)]; }
            if (scroll >= 0 && grid.Rows.Count > 0) grid.FirstDisplayedScrollingRowIndex = Math.Min(scroll, grid.Rows.Count - 1);
        }
        finally { grid.ResumeLayout(); }
        summary.Text = rows.Count == 0 ? "No profiles match these filters. Clear filters or create a profile." : $"{rows.Count} profile(s) shown · Refreshes every 3 seconds";
        ShowDetails();
    }

    private static string StatusText(ProfileActivity state) => state switch
    {
        ProfileActivity.InUse => "In use", ProfileActivity.Unknown => "Unknown", ProfileActivity.Missing => "Not created / missing",
        _ => state.ToString()
    };

    private void ShowDetails()
    {
        var p = Selected;
        openDetails.Enabled = p?.CanLaunch == true; editDetails.Enabled = p is not null;
        if (p is null) { details.Text = "Select a profile to view its details."; return; }
        var m = p.Metadata;
        details.Text = $"{p.DisplayName}\r\n\r\nStatus: {StatusText(p.Status.Activity)}\r\n{p.Status.Detail}\r\n\r\n" +
            $"Profile ID: {m?.Id.ToString("D") ?? "Not registered"}\r\nStorage slug: {p.Slug}\r\n" +
            $"Updated: {m?.UpdatedUtc.ToLocalTime().ToString("g") ?? "—"}\r\n\r\n" +
            $"Tags: {(m is null ? "" : string.Join(", ", m.Tags))}\r\nFolders: {(m is null ? "" : string.Join(", ", m.Folders))}\r\n\r\nNotes:\r\n{m?.Notes ?? ""}";
    }

    private void CreateProfile()
    {
        using var dialog = new ProfileEditorForm(dashboard, null);
        if (dialog.ShowDialog(this) == DialogResult.OK) _ = RefreshProfiles(dialog.SavedSlug, true);
    }

    private async Task EditSelected()
    {
        var profile = Selected; if (profile is null) return;
        try
        {
            feedback.Text = "Loading profile metadata…";
            var definition = await Task.Run(() => dashboard.PrepareEdit(profile.Slug));
            if (IsDisposed) return;
            using var dialog = new ProfileEditorForm(dashboard, definition);
            if (dialog.ShowDialog(this) == DialogResult.OK) await RefreshProfiles(dialog.SavedSlug, true);
            else await RefreshProfiles(profile.Slug);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (!IsDisposed) feedback.Text = "Could not edit profile: " + ex.Message; }
    }

    private async Task LaunchSelected()
    {
        var profile = Selected; if (profile is null) return;
        try
        {
            await Task.Run(() => dashboard.Launch(profile));
            if (!IsDisposed) { feedback.Text = "Starting " + profile.DisplayName + "."; await RefreshProfiles(profile.Slug); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception)
        { if (!IsDisposed) { feedback.Text = "Could not launch profile: " + ex.Message; await RefreshProfiles(profile.Slug); } }
    }
}
