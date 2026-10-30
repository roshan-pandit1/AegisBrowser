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
            var root = ProfileCatalog.DefaultRoot;
            // The manager is a regular WinForms window; it never initializes CEF or holds a profile lease.
            if (args.Length == 1 && args[0].Equals("--profiles", StringComparison.OrdinalIgnoreCase))
            {
                using var manager = new ProfileManagerForm(root);
                Application.Run(manager);
                return;
            }
            var profile = BrowserPolicy.ReadProfile(args);
            var path = BrowserPolicy.ProfilePath(root, profile);
            ProfileRepository.CheckSafeProfilePath(root, profile);
            lease = new ProfileLease(path);
            _ = new ProfileRepository(root).GetOrMigrate(profile, lease);
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
            if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); }
            lease?.Dispose();
        }
    }
}
