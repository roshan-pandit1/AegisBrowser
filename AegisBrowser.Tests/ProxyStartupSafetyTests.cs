using AegisBrowser.Core;

// These tests exercise the existing pre-CEF gates using disposable profiles.
// They do not start Chromium or establish network containment.
internal static class ProxyStartupSafetyTests
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception("FAIL proxy startup: " + label);
            checks++;
        }
        void Reject(Action action, string label)
        {
            var rejected = false;
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { rejected = true; }
            Check(rejected, label);
        }

        var testRoot = Path.Combine(Path.GetTempPath(), "aegis-proxy-startup-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(testRoot, "profiles");
        try
        {
            var profiles = new ProfileRepository(root);
            var protectedProfile = profiles.Create("protected");
            var directProfile = profiles.Create("direct");
            var protectedPath = BrowserPolicy.ProfilePath(root, protectedProfile.Slug);
            var directPath = BrowserPolicy.ProfilePath(root, directProfile.Slug);
            var repository = new ProxyRepository(root);
            var missingCredential = Guid.NewGuid();
            // Save directly to exercise recovery from a missing credential blob.
            // No actual username, password or network connection is involved.
            var required = new ProxyDraft(ProxyProtocol.Http, "proxy.example", 8080,
                true, missingCredential, "", true);
            using (var protectedLease = new ProfileLease(protectedPath))
            using (var directLease = new ProfileLease(directPath))
            {
                repository.Save(protectedProfile, protectedLease, required, 0);
                repository.Save(directProfile, directLease,
                    new ProxyDraft(ProxyProtocol.Direct, "", 0, false, null, "", false), 0);
                var privacyPath = Path.Combine(protectedPath, "privacy.json");
                const string malformed = "{not-valid-json";
                File.WriteAllText(privacyPath, malformed);
                Reject(() => new PrivacySettingsStore(protectedPath, requireExistingDirectory: true).Load(),
                    "malformed privacy settings are rejected");
                Check(File.ReadAllText(privacyPath) == malformed, "malformed privacy settings remain intact");

                // The application invokes this pre-CEF guard before opening MainForm.
                // Its rejection must not depend on whether credentials can be read or
                // whether privacy settings are also invalid.
                ProfileSettingsTransactions.EnsureNoPending(root, protectedProfile.Slug);
                Reject(() => repository.EnsureRuntimeReady(protectedProfile, protectedLease),
                    "required proxy remains blocked with malformed privacy and missing credentials");
                var blob = Path.Combine(root, ProxyCredentialVault.DirectoryName,
                    protectedProfile.Id.ToString("N"), missingCredential.ToString("N") + ".bin");
                Check(!File.Exists(blob), "missing credential remains absent");
                if (OperatingSystem.IsWindows())
                    Reject(() => new ProxyCredentialVault(root).Read(protectedProfile.Id, missingCredential),
                        "missing profile-bound credentials are rejected");

                Reject(() => repository.Save(protectedProfile, protectedLease,
                    required with { RequireProxy = false }, 1), "proxy cannot be saved as optional fallback");
                Check(repository.Get(protectedProfile, protectedLease)?.Settings == required,
                    "rejected downgrade preserves required configuration");
                Reject(() => { using var duplicate = new ProfileLease(protectedPath); },
                    "profile lease cannot be acquired twice");

                var pendingDirectory = Path.Combine(root, ".settings-pending");
                Directory.CreateDirectory(pendingDirectory);
                var pendingMarker = Path.Combine(pendingDirectory, "profile-" + protectedProfile.Slug + ".json");
                File.WriteAllText(pendingMarker, "interrupted fixture");
                Reject(() => ProfileSettingsTransactions.EnsureNoPending(root, protectedProfile.Slug),
                    "interrupted settings transaction blocks startup");
                Check(File.ReadAllText(pendingMarker) == "interrupted fixture", "interrupted marker is preserved");
                Reject(() => repository.EnsureRuntimeReady(protectedProfile, protectedLease),
                    "required proxy remains blocked while transaction is interrupted");

                ProfileSettingsTransactions.EnsureNoPending(root, directProfile.Slug);
                repository.EnsureRuntimeReady(directProfile, directLease);
                checks++;
                Check(repository.Get(directProfile, directLease)?.Settings.Protocol == ProxyProtocol.Direct,
                    "independent direct profile retains its own saved state");
            }

            using (var restartedLease = new ProfileLease(protectedPath))
            {
                Reject(() => ProfileSettingsTransactions.EnsureNoPending(root, protectedProfile.Slug),
                    "interrupted transaction remains blocked after lease restart");
                Reject(() => repository.EnsureRuntimeReady(protectedProfile, restartedLease),
                    "required proxy remains blocked after lease restart");
                Check(File.ReadAllText(Path.Combine(protectedPath, "privacy.json")) == "{not-valid-json",
                    "privacy settings remain intact across lease restart");
            }
            Console.WriteLine($"PASS: {checks} pre-CEF proxy and privacy startup safety checks");
        }
        finally
        {
            // The path is freshly generated under the OS temporary directory.
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
        }
    }
}
