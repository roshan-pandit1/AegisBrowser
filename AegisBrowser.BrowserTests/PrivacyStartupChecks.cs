using AegisBrowser.Core;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // The fake initializer makes the actual production startup boundary observable
    // without starting Chromium or treating this as a native integration result.
    private static void CheckPrivacyStartupBeforeChromium(string root)
    {
        var path = Path.Combine(root, "privacy-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var file = Path.Combine(path, "privacy.json");
            const string malformed = "{not-valid-json";
            File.WriteAllText(file, malformed);
            var initializationCalls = 0;
            bool Initialize() { initializationCalls++; return true; }

            var failed = false;
            try { AegisBrowser.Program.InitializeChromiumAfterPrivacyCheck(path, Initialize); }
            catch (IOException) { failed = true; }
            Check(failed && initializationCalls == 0, "malformed privacy settings block Chromium initialization");
            Check(File.ReadAllText(file) == malformed, "startup preflight preserves malformed privacy settings");

            new PrivacySettingsStore(path).Save(new PrivacyConfiguration());
            Check(AegisBrowser.Program.InitializeChromiumAfterPrivacyCheck(path, Initialize) && initializationCalls == 1,
                "valid privacy settings permit Chromium initialization");
            Check(!AegisBrowser.Program.InitializeChromiumAfterPrivacyCheck(path, () => false),
                "Chromium initialization failure is propagated");

            using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                failed = false;
                try { AegisBrowser.Program.InitializeChromiumAfterPrivacyCheck(path, Initialize); }
                catch (IOException) { failed = true; }
                Check(failed && initializationCalls == 1, "unreadable privacy settings block Chromium initialization");
            }

            failed = false;
            try { AegisBrowser.Program.InitializeChromiumAfterPrivacyCheck(Path.Combine(path, "missing"), Initialize); }
            catch (IOException) { failed = true; }
            Check(failed && initializationCalls == 1, "missing profile directory blocks Chromium initialization");
        }
        finally { Directory.Delete(path, recursive: true); }
    }
}
