using System.Diagnostics;
using System.Reflection;
using AegisBrowser.Core;

internal static class ProfileSettingsTransactionTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL settings transaction: " + name); count++; }
        void Reject(Action action, string name)
        { try { action(); } catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { count++; return; } throw new Exception("FAIL settings transaction (accepted): " + name); }
        var root = Path.Combine(Path.GetTempPath(), "aegis-settings-transaction-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new ProfileRepository(root); var templates = new ProfileTemplateRepository(root);
            var profile = repository.Create(new ProfileDraft("work", "Work", "notes", ["tag"], ["folder"]));
            var path = BrowserPolicy.ProfilePath(root, "work");
            Directory.CreateDirectory(Path.Combine(path, "Cache")); File.WriteAllText(Path.Combine(path, "Cache", "sentinel"), "browser data");
            new PrivacySettingsStore(path).Save(new PrivacyConfiguration { Mode = PrivacyMode.Standard });
            new SearchSettingsStore(path).Save(new SearchSettings("duckduckgo"));
            var template = templates.Create("Configured", "", [], [], new(new PrivacyConfiguration { Mode = PrivacyMode.Strict }, new SearchSettings("google")));
            var service = new ProfileSettingsTransactions(root, repository, templates);
            var result = service.Apply([profile], template, CancellationToken.None);
            Check(result.Single().Outcome == ProfileOperationOutcome.Succeeded && new PrivacySettingsStore(path).Load().Mode == PrivacyMode.Strict &&
                new SearchSettingsStore(path).Load().EngineId == "google", "template applies selected settings");
            ProfileSettingsTransactions.EnsureNoPending(root, "work");
            Check(repository.GetBySlug("work")!.Revision == profile.Revision && File.ReadAllText(Path.Combine(path, "Cache", "sentinel")) == "browser data",
                "metadata and Chromium storage remain unchanged");
            using (var lease = new ProfileLease(path))
                Check(service.Apply([profile], template, CancellationToken.None).Single().Outcome == ProfileOperationOutcome.Failed, "running profile rejected");
            var missing = repository.Create("missing");
            Check(service.Apply([missing], template, CancellationToken.None).Single().Outcome == ProfileOperationOutcome.Failed &&
                !File.Exists(Path.Combine(BrowserPolicy.ProfilePath(root, "missing"), "privacy.json")), "missing original settings are not overwritten");
            var changed = repository.Update(profile with { DisplayName = "Changed" }, profile.Revision);
            Check(service.Apply([profile], template, CancellationToken.None).Single().Outcome == ProfileOperationOutcome.Failed, "stale profile rejected");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Check(service.Apply([changed], template, cancellation.Token).Single().Outcome == ProfileOperationOutcome.Canceled, "cancellation stops before work");
            }

            new PrivacySettingsStore(path).Save(new PrivacyConfiguration { Mode = PrivacyMode.Standard });
            new SearchSettingsStore(path).Save(new SearchSettings("duckduckgo"));
            var child = StartInterrupted(root, "work", template.Id);
            try
            {
                var ready = child.StandardOutput.ReadLineAsync();
                if (!ready.Wait(TimeSpan.FromSeconds(15)) || ready.Result != "READY") throw new Exception("Settings child did not reach interruption point.");
                child.Kill(true);
                if (!child.WaitForExit(15000)) throw new Exception("Settings child did not terminate.");
            }
            finally { if (!child.HasExited) { child.Kill(true); child.WaitForExit(); } child.Dispose(); }
            Reject(() => ProfileSettingsTransactions.EnsureNoPending(root, "work"), "interrupted operation blocks browser startup");
            var privacyPath = Path.Combine(path, "privacy.json");
            var appliedBytes = File.ReadAllBytes(privacyPath);
            new PrivacySettingsStore(path).Save(new PrivacyConfiguration { Mode = PrivacyMode.Custom });
            Reject(() => service.Recover(changed), "external settings change blocks automatic rollback");
            Check(new PrivacySettingsStore(path).Load().Mode == PrivacyMode.Custom, "external change preserved during failed recovery");
            File.WriteAllBytes(privacyPath, appliedBytes);
            service.Recover(changed);
            ProfileSettingsTransactions.EnsureNoPending(root, "work");
            Check(new PrivacySettingsStore(path).Load().Mode == PrivacyMode.Standard && new SearchSettingsStore(path).Load().EngineId == "duckduckgo" &&
                File.ReadAllText(Path.Combine(path, "Cache", "sentinel")) == "browser data", "recovery restores both original settings without touching Chromium data");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"PASS: {count} settings transaction checks");
    }

    private static Process StartInterrupted(string root, string slug, Guid template)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("Process path unavailable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--settings-apply-child"); start.ArgumentList.Add(root); start.ArgumentList.Add(slug); start.ArgumentList.Add(template.ToString("D"));
        return Process.Start(start) ?? throw new IOException("Could not start settings interruption probe.");
    }
}
