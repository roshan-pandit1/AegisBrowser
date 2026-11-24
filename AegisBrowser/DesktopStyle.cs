namespace AegisBrowser;

internal static class DesktopStyle
{
    internal static Color Header => SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(21, 35, 58);
    internal static Color Surface => SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(240, 244, 249);

    internal static void Apply(Form form)
    {
        form.AutoScaleDimensions = new SizeF(96, 96);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.BackColor = Surface;
        form.ForeColor = SystemColors.WindowText;
    }

    internal static void Button(Button button, bool primary = false)
    {
        button.MinimumSize = new Size(0, 36);
        if (!primary || SystemInformation.HighContrast) return;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Color.FromArgb(25, 85, 178);
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(20, 67, 142);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(15, 55, 117);
    }
}
