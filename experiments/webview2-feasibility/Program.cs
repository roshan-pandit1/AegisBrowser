using System;
using System.Windows.Forms;

namespace AegisBrowser.WebView2Feasibility;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (!ProfileRootPolicy.TryParse(args, out var configuration, out _) ||
            !ProfileRootPolicy.ValidateExistingRoot(configuration, out _))
        {
            MessageBox.Show("The explicit lab profile root or shutdown timeout is invalid or unavailable.",
                "Prototype configuration", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new PrototypeForm(configuration));
    }
}
