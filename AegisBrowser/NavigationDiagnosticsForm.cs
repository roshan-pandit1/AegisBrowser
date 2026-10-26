using AegisBrowser.Core;

namespace AegisBrowser;

internal sealed class NavigationDiagnosticsForm : Form
{
    private readonly NavigationDiagnostics diagnostics;
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = true, AllowUserToAddRows = false };
    private readonly CheckBox enabled = new() { Text = "Collect locally (all ordinary tabs)", AutoSize = true };
    private readonly Button export = new() { Text = "Stop and export JSON", AutoSize = true };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };
    internal NavigationDiagnosticsForm(NavigationDiagnostics diagnostics)
    {
        this.diagnostics = diagnostics;
        Text = "Local navigation diagnostics"; Width = 1000; Height = 550; AutoScaleMode = AutoScaleMode.Dpi;
        enabled.Checked = diagnostics.Enabled;
        enabled.CheckedChanged += (_, _) => { diagnostics.Enabled = enabled.Checked; UpdateRows(); };
        var clear = new Button { Text = "Clear", AutoSize = true }; clear.Click += (_, _) => { diagnostics.Clear(); UpdateRows(); };
        export.Click += async (_, _) => await ExportAsync();
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true }; bar.Controls.AddRange([enabled, clear, export]);
        var notice = new Label { Dock = DockStyle.Bottom, Height = 70, AutoEllipsis = true,
            Text = "Off by default. Disabling clears memory. Stop and export saves a snapshot; cancelling discards it. Paths are heavily redacted; console text is omitted. HTTP completion does not mean survey acceptance. If a provider reports a security result, AegisBrowser does not override it. Export includes visited registered hostnames; review before sharing." };
        Controls.Add(grid); Controls.Add(bar); Controls.Add(notice);
        refresh.Tick += RefreshTick; refresh.Start(); UpdateRows();
    }
    private void RefreshTick(object? sender, EventArgs e) => UpdateRows();
    private void UpdateRows() => grid.DataSource = diagnostics.Snapshot();
    private async Task ExportAsync()
    {
        export.Enabled = false;
        // Capture and stop atomically before the save dialog can pump new UI messages.
        var report = diagnostics.StopForExport();
        enabled.Checked = false;
        UpdateRows();
        try
        {
            using var dialog = new SaveFileDialog { Filter = "JSON report|*.json", FileName = "navigation-diagnostics.json", Title = "Save local trace — includes visited registered hostnames" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            if (!await report.TrySaveAsync(dialog.FileName) && !IsDisposed)
                MessageBox.Show(this, "The local report could not be saved. No report was saved. Collection remains stopped.", "Export failed");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { if (!IsDisposed) MessageBox.Show(this, "The export dialog could not be opened. Collection remains stopped.", "Export failed"); }
        finally { if (!IsDisposed) export.Enabled = true; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { refresh.Stop(); refresh.Tick -= RefreshTick; refresh.Dispose(); }
        base.Dispose(disposing);
    }
}
