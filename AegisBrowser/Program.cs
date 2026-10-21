using AegisBrowser.Core;
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
        try
        {
            var profile = BrowserPolicy.ReadProfile(args);
            var path = BrowserPolicy.ProfilePath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AegisBrowser", "Profiles"), profile);
            lease = new ProfileLease(path);
            var settings = new CefSettings
            {
                RootCachePath = path,
                CachePath = Path.Combine(path, "Cache"),
                LogFile = Path.Combine(path, "chromium.log"),
                LogSeverity = LogSeverity.Warning
            };
            // Do not pass application command-line arguments through to Chromium.
            settings.CommandLineArgsDisabled = true;
            initialized = Cef.Initialize(settings);
            if (!initialized) throw new InvalidOperationException("Chromium initialization failed.");
            using var context = Cef.GetGlobalRequestContext();
            if (!context.RegisterSchemeHandlerFactory("https", "aegis-diagnostics.invalid", new DiagnosticsFactory()))
                throw new InvalidOperationException("Could not register local diagnostics.");
            using var window = new MainForm(profile, path);
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
            if (initialized) Cef.Shutdown();
            lease?.Dispose();
        }
    }
}
