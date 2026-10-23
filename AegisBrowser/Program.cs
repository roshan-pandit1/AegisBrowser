using AegisBrowser.Core;
using AegisBrowser.Lab;
using CefSharp;
using CefSharp.WinForms;

namespace AegisBrowser;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var initialized = false;
        ProfileLease? lease = null;
        LabServer? lab = null;
        try
        {
            LabProfile? labProfile = args.Length == 1 && args[0].StartsWith("--lab=", StringComparison.Ordinal)
                ? LabProfiles.Parse(args[0][6..]) : null;
            var profile = labProfile is null ? BrowserPolicy.ReadProfile(args) : "lab";
            var path = BrowserPolicy.ProfilePath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AegisBrowser", "Profiles"), profile);
            if (labProfile is not null) path = LabProfiles.PathFor(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), labProfile.Value);
            lease = new ProfileLease(path);
            if (labProfile is not null) lab = LabServer.StartAsync().GetAwaiter().GetResult();
            var settings = new CefSettings
            {
                RootCachePath = path,
                CachePath = Path.Combine(path, "Cache"),
                LogFile = Path.Combine(path, "chromium.log"),
                LogSeverity = LogSeverity.Warning
            };
            // Do not pass application command-line arguments through to Chromium.
            settings.CommandLineArgsDisabled = true;
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(settings);
            if (!initialized) throw new InvalidOperationException("Chromium initialization failed.");
            using var context = Cef.GetGlobalRequestContext();
            if (!context.RegisterSchemeHandlerFactory("https", "aegis-diagnostics.invalid", new DiagnosticsFactory()))
                throw new InvalidOperationException("Could not register local diagnostics.");
            using Form window = lab is null ? new MainForm(profile, path) : new LabDashboard(lab, labProfile!.Value);
            Application.Run(window);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Aegis Browser could not start. The profile may already be open.\n\n" + ex.Message,
                "Aegis Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
        finally
        {
            if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); }
            if (lab is not null) lab.DisposeAsync().AsTask().GetAwaiter().GetResult();
            lease?.Dispose();
        }
    }
}
