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
        Text = "Search settings";
        DesktopStyle.Apply(this);
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(440, 225); MinimumSize = new Size(340, 240);
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
        var button = new Button { Name = "SaveSearchSettings", Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        cancel.Click += (_, _) => Close();
        DesktopStyle.Button(button, primary: true);
        DesktopStyle.Button(cancel);
        var feedback = new Label { Dock = DockStyle.Fill, AutoSize = true, AccessibleName = "Search settings messages" };
        var information = (Label)layout.GetControlFromPosition(0, 3)!;
        layout.Controls.Remove(information);
        var messages = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        messages.Controls.AddRange([information, feedback]);
        messages.ClientSizeChanged += (_, _) => feedback.MaximumSize = new Size(Math.Max(1, messages.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 8), 0);
        layout.Controls.Add(messages, 0, 3);
        button.Click += (_, _) =>
        {
            try { SaveSelection(); feedback.Text = "Search settings saved."; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { feedback.Text = "Could not save: " + ex.Message; }
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange([cancel, button]);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        AcceptButton = button;
        CancelButton = cancel;
    }
    internal void SaveSelection()
    {
        var engine = (SearchEngine)engines.SelectedItem!;
        save(new(engine.Id));
        current.Text = "Current engine: " + engine.Name;
    }
}
