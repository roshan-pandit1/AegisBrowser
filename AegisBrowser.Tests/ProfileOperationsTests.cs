using AegisBrowser.Core;

internal static class ProfileOperationsTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL profile operations: " + name); count++; }
        void Reject(Action action, string name)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { count++; return; }
            throw new Exception("FAIL profile operations (accepted): " + name);
        }
        var root = Path.Combine(Path.GetTempPath(), "aegis-operations-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profiles = new ProfileRepository(root);
            var source = profiles.Create(new ProfileDraft("source", "Source", "private note", ["qa"], ["Work"]));
            var sourcePath = BrowserPolicy.ProfilePath(root, "source");
            var cache = Path.Combine(sourcePath, "Cache", "Default"); Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, "Cookies"), "sensitive sentinel");
            new PrivacySettingsStore(sourcePath).Save(new PrivacyConfiguration { Mode = PrivacyMode.Strict });
            new SearchSettingsStore(sourcePath).Save(new SearchSettings("google"));
            var duplicates = new ProfileDuplicationService(root, profiles);
            var clean = duplicates.DuplicateClean(source, "clean", "Source copy", false, false, false);
            var cleanPath = BrowserPolicy.ProfilePath(root, "clean");
            Check(clean.Id != source.Id && clean.Notes == "" && clean.Tags.SequenceEqual(["qa"]) && clean.Folders.SequenceEqual(["Work"]), "clean duplicate has independent identity and metadata");
            Check(!Directory.Exists(Path.Combine(cleanPath, "Cache")) && !File.Exists(Path.Combine(cleanPath, "privacy.json")) &&
                !File.Exists(Path.Combine(cleanPath, "search.json")) && File.ReadAllText(Path.Combine(cache, "Cookies")) == "sensitive sentinel", "clean duplicate omits browser storage and unselected settings");
            var configured = duplicates.DuplicateClean(source, "configured", "Configured copy", true, true, true);
            var configuredPath = BrowserPolicy.ProfilePath(root, "configured");
            Check(configured.Id != source.Id && configured.Notes == "private note" && new PrivacySettingsStore(configuredPath).Load().Mode == PrivacyMode.Strict &&
                new SearchSettingsStore(configuredPath).Load().EngineId == "google" && !Directory.Exists(Path.Combine(configuredPath, "Cache")), "selected application settings copied without Chromium data");
            Reject(() => duplicates.DuplicateClean(source, "CLEAN", "Duplicate", false, false, false), "duplicate destination rejected");
            using (var lease = new ProfileLease(sourcePath))
            {
                Reject(() => duplicates.DuplicateClean(source, "busy-settings", "Busy", false, true, false), "settings capture requires a closed source");
                Check(duplicates.DuplicateClean(source, "busy-metadata", "Metadata only", false, false, false).Id != source.Id, "metadata-only copy works while source is open");
            }
            var updated = profiles.Update(source with { DisplayName = "Changed" }, source.Revision);
            Reject(() => duplicates.DuplicateClean(source, "stale", "Stale", false, false, false), "stale source rejected");
            Check(!Directory.Exists(BrowserPolicy.ProfilePath(root, "stale")), "stale copy creates no profile");
            Reject(() => duplicates.DuplicateClean(updated, "../escape", "Bad", false, false, false), "invalid destination rejected");

            var templates = new ProfileTemplateRepository(root);
            var service = new ProfileTemplateService(root, profiles, templates);
            var template = service.SaveFromProfile(updated, "Work template", "description", true, true);
            var loaded = new ProfileTemplateRepository(root).List().Single();
            Check(loaded.Id == template.Id && loaded.SchemaVersion == 1 && loaded.Settings.Search?.EngineId == "google" &&
                loaded.Settings.Privacy?.Mode == PrivacyMode.Strict && loaded.Tags.SequenceEqual(["qa"]), "template persists selected settings and labels");
            var revised = templates.Update(loaded with { Name = "Renamed template", Tags = ["new"], Folders = ["Team"] }, loaded.Revision);
            Reject(() => templates.Update(loaded with { Name = "stale" }, loaded.Revision), "stale template edit rejected");
            Reject(() => templates.Delete(loaded.Id, loaded.Revision), "stale template delete rejected");
            var templateCopy = service.Duplicate(revised, "Template copy");
            Check(templateCopy.Id != revised.Id && templateCopy.Revision == 1 && templates.List().Count == 2, "template duplicate gets new identity");
            var fromTemplate = service.CreateProfile(revised, "from-template", "Template profile");
            var templatePath = BrowserPolicy.ProfilePath(root, "from-template");
            Check(fromTemplate.Id != source.Id && fromTemplate.Tags.SequenceEqual(["new"]) && fromTemplate.Folders.SequenceEqual(["Team"]) &&
                new SearchSettingsStore(templatePath).Load().EngineId == "google" && !Directory.Exists(Path.Combine(templatePath, "Cache")), "template creates fresh profile with settings");
            Reject(() => service.CreateProfile(revised, "FROM-TEMPLATE", "Again"), "template cannot overwrite an existing profile");
            templates.Delete(templateCopy.Id, templateCopy.Revision);
            Check(new ProfileTemplateRepository(root).List().Count == 1 && Directory.Exists(templatePath), "deleting a template preserves created profiles");

            var corruptRoot = Path.Combine(root, "corrupt-test");
            var corruptRepo = new ProfileTemplateRepository(corruptRoot);
            var corrupt = corruptRepo.Create("Good", "", [], [], new(null, null));
            var corruptPath = Path.Combine(corruptRoot, "templates", "template-" + corrupt.Id.ToString("D") + ".json");
            var unsupported = File.ReadAllText(corruptPath).Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 99");
            File.WriteAllText(corruptPath, unsupported);
            Reject(() => corruptRepo.List(), "unsupported template version rejected");
            Check(File.ReadAllText(corruptPath) == unsupported, "unsupported template preserved");

            var catalog = new ProfileCatalog(root);
            Reject(() => catalog.Create("interrupted", stage => { File.WriteAllText(Path.Combine(stage, "owned-test"), "retained"); throw new IOException("simulated interruption"); }), "staged creation failure reported");
            var leftover = Directory.GetDirectories(root, ".creating-*").Single();
            Check(!Directory.Exists(BrowserPolicy.ProfilePath(root, "interrupted")) && File.ReadAllText(Path.Combine(leftover, "owned-test")) == "retained" &&
                !catalog.List().Contains("interrupted"), "incomplete staging remains distinguishable and untouched");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"PASS: {count} profile operation checks");
    }
}
