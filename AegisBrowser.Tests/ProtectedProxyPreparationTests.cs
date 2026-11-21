using AegisBrowser.Core;

// Local preparation only: no CEF initialization, networking, policy provider,
// or claim of verified containment is involved.
internal static class ProtectedProxyPreparationTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool ok, string label)
        {
            if (!ok) throw new Exception("FAIL protected proxy preparation: " + label);
            count++;
        }
        void Reject(Action action, string label)
        {
            var rejected = false;
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { rejected = true; }
            Check(rejected, label);
        }

        var testRoot = Path.Combine(Path.GetTempPath(), "aegis-protected-preparation-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(testRoot, "profiles");
        try
        {
            var profiles = new ProfileRepository(root);
            var profile = profiles.Create("prepared");
            var other = profiles.Create("other");
            var profilePath = BrowserPolicy.ProfilePath(root, profile.Slug);
            var otherPath = BrowserPolicy.ProfilePath(root, other.Slug);
            var repository = new ProxyRepository(root);
            var settings = new ProxyDraft(ProxyProtocol.Http, "proxy.example", 8080, false, null, "", true);
            using (var lease = new ProfileLease(profilePath))
            using (var otherLease = new ProfileLease(otherPath))
            {
                repository.Save(profile, lease, settings, 0);
                var prepared = ProtectedProxyPreparation.Prepare(root, profile, lease);
                Check(prepared.ProfileId == profile.Id && prepared.ProxyRevision == 1,
                    "preparation is bound to the selected profile and revision");
                Check(!prepared.CanStartProtectedBrowsing && prepared.ContainmentState == ProxyContainmentState.NotConfigured,
                    "valid settings cannot authorize browsing without containment");
                Check(!prepared.BlockingReason.Contains("proxy.example", StringComparison.OrdinalIgnoreCase),
                    "blocked diagnostic does not expose the endpoint");
                Reject(() => repository.EnsureRuntimeReady(profile, lease), "existing startup guard remains active");
                Reject(() => ProtectedProxyPreparation.Prepare(root, profile, otherLease), "another profile lease is rejected");

                var privacyFile = Path.Combine(profilePath, "privacy.json");
                File.WriteAllText(privacyFile, "{invalid");
                Reject(() => ProtectedProxyPreparation.Prepare(root, profile, lease), "malformed privacy blocks preparation");
                Check(File.ReadAllText(privacyFile) == "{invalid", "invalid privacy file remains intact");
                File.WriteAllText(privacyFile, "{}");

                var missingId = Guid.NewGuid();
                var requiredAuth = settings with { AuthenticationRequired = true, CredentialId = missingId };
                repository.Save(profile, lease, requiredAuth, 1);
                if (OperatingSystem.IsWindows())
                {
                    Reject(() => ProtectedProxyPreparation.Prepare(root, profile, lease), "missing profile credential blocks preparation");
                    var vault = new ProxyCredentialVault(root);
                    var credentialId = vault.Store(profile.Id, new ProxyCredentials("fixture-user", "fixture-password"));
                    repository.Save(profile, lease, requiredAuth with { CredentialId = credentialId }, 2);
                    var authenticated = ProtectedProxyPreparation.Prepare(root, profile, lease);
                    Check(!authenticated.CanStartProtectedBrowsing && authenticated.ProxyRevision == 3,
                        "available credentials do not bypass containment");
                    Check(!authenticated.BlockingReason.Contains("fixture-user", StringComparison.Ordinal) &&
                          !authenticated.BlockingReason.Contains("fixture-password", StringComparison.Ordinal),
                        "blocking reason does not expose credentials");
                }

                var pending = Path.Combine(root, ".settings-pending");
                Directory.CreateDirectory(pending);
                var marker = Path.Combine(pending, "profile-" + profile.Slug + ".json");
                File.WriteAllText(marker, "interrupted");
                Reject(() => ProtectedProxyPreparation.Prepare(root, profile, lease), "incomplete settings transaction blocks preparation");
                Check(File.ReadAllText(marker) == "interrupted", "pending transaction evidence remains intact");
                Reject(() => ProtectedProxyPreparation.Prepare(root, other, otherLease), "independent direct profile is not a required proxy");
            }

            var expiredLease = new ProfileLease(profilePath);
            expiredLease.Dispose();
            Reject(() => ProtectedProxyPreparation.Prepare(root, profile, expiredLease), "released profile lease is rejected");
            Console.WriteLine($"PASS: {count} preparation-only protected proxy checks");
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
        }
    }
}
