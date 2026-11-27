using System;
using System.Windows.Forms;

namespace AegisBrowser.WebView2Feasibility;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new PrototypeForm());
    }
}
