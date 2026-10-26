using AegisBrowser.Core;

namespace AegisBrowser;

internal sealed class SearchSettingsForm : Form
{
    private readonly ComboBox engines = new() { Name = "SearchEngine", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, AccessibleName = "Default search engine" };
    private readonly Label current = new() { Name = "CurrentSearchEngine", AutoSize = true };
    private readonly Action<SearchSettings> save;

    internal SearchSettingsForm(SearchSettings settings, Action<SearchSettings> save)
    {
        this.save = save;
        Text = "Search Settings"; AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(480, 230); MinimumSize = new Size(400, 250);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Default search engine", AutoSize = true }, 0, 0);
        engines.Items.AddRange(SearchEngines.All.Cast<object>().ToArray());
        engines.SelectedItem = SearchEngines.Get(settings.EngineId);
        layout.Controls.Add(engines, 0, 1);
        current.Text = "Current engine: " + SearchEngines.Get(settings.EngineId).Name;
        layout.Controls.Add(current, 0, 2);
        layout.Controls.Add(new Label { Text = "Searches are sent only when you press Enter.\nThis setting applies to this browser profile.", AutoSize = true }, 0, 3);
        var button = new Button { Name = "SaveSearchSettings", Text = "Save settings", AutoSize = true };
        button.Click += (_, _) =>
        {
            try { SaveSelection(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { MessageBox.Show(this, ex.Message, "Search settings could not be saved", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        layout.Controls.Add(button, 0, 4); Controls.Add(layout); AcceptButton = button;
    }
    internal void SaveSelection()
    {
        var engine = (SearchEngine)engines.SelectedItem!;
        save(new(engine.Id));
        current.Text = "Current engine: " + engine.Name;
    }
}
