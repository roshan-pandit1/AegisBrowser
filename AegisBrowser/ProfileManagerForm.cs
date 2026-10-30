using System.Diagnostics;
using System.ComponentModel;
using AegisBrowser.Core;

namespace AegisBrowser;

/// <summary>Local profile launcher. Every Open action starts a separate CEF process.</summary>
public sealed class ProfileManagerForm : Form
{
    private readonly ProfileCatalog catalog;
    private readonly ListBox profiles = new() { Dock = DockStyle.Fill, AccessibleName = "Browser profiles" };
    private readonly TextBox newName = new()
    {
        Width = 175, PlaceholderText = "New profile name", AccessibleName = "New profile name"
    };
    private readonly Label feedback = new()
    {
        Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
        Text = "Profiles use separate local browser storage. Opening a profile starts a new process."
    };

    public ProfileManagerForm(string root)
    {
        catalog = new ProfileCatalog(root);
        Text = "Aegis Browser — Profiles";
        StartPosition = FormStartPosition.CenterScreen;
        Width = 700;
        Height = 480;
        MinimumSize = new Size(520, 360);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(14)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        var heading = new Label
        {
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            Text = "Browser profiles", Font = new Font(Font.FontFamily, 15, FontStyle.Bold)
        };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true,
            FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 8, 0, 0)
        };
        var create = new Button { Text = "Create", Width = 85, AccessibleName = "Create profile" };
        var open = new Button { Text = "Open", Width = 85, AccessibleName = "Open profile" };
        var refresh = new Button { Text = "Refresh", Width = 85, AccessibleName = "Refresh profiles" };
        create.Click += (_, _) => CreateProfile();
        open.Click += (_, _) => OpenSelected();
        refresh.Click += (_, _) => RefreshProfiles();
        profiles.DoubleClick += (_, _) => OpenSelected();
        actions.Controls.Add(newName);
        actions.Controls.Add(create);
        actions.Controls.Add(open);
        actions.Controls.Add(refresh);
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(profiles, 0, 1);
        layout.Controls.Add(actions, 0, 2);
        layout.Controls.Add(feedback, 0, 3);
        Controls.Add(layout);

        try { RefreshProfiles(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            feedback.Text = "Could not read profiles: " + ex.Message;
        }
    }

    private void RefreshProfiles(string? select = null)
    {
        var previous = select ?? profiles.SelectedItem as string ?? "default";
        // The default profile can be launched before its storage directory exists.
        var names = catalog.List().Append("default").Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        profiles.BeginUpdate();
        try
        {
            profiles.Items.Clear();
            profiles.Items.AddRange(names);
            profiles.SelectedItem = previous;
            if (profiles.SelectedIndex < 0 && profiles.Items.Count > 0) profiles.SelectedIndex = 0;
        }
        finally { profiles.EndUpdate(); }
    }

    private void CreateProfile()
    {
        try
        {
            var created = catalog.Create(newName.Text.Trim());
            newName.Clear();
            RefreshProfiles(created);
            feedback.Text = "Created " + created + ". Select Open to launch it.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            feedback.Text = ex.Message;
        }
    }

    private void OpenSelected()
    {
        if (profiles.SelectedItem is not string name) return;
        try
        {
            var executable = Environment.ProcessPath
                ?? throw new IOException("Could not find the current application executable.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory
            };
            // During dotnet run the host process is dotnet; pass the application DLL first.
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(ProfileManagerForm).Assembly.Location);
            start.ArgumentList.Add("--profile=" + name);
            using var child = Process.Start(start) ?? throw new IOException("Could not launch the profile.");
            feedback.Text = "Started " + name + ". A running instance prevents opening it twice.";
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            feedback.Text = "Could not launch profile: " + ex.Message;
        }
    }
}
