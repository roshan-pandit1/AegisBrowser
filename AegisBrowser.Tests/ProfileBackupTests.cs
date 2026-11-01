using System.Text.Json;
using AegisBrowser.Core;

internal static class ProfileBackupTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL profile backup: " + name); count++; }
        void Reject(Action action, string name)
        {
            try { action(); } catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { count++; return; }
            throw new Exception("FAIL profile backup (accepted): " + name);
        }
        var root = Path.Combine(Path.GetTempPath(), "aegis-backups-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new ProfileRepository(root);
            var profile = repository.Create(new ProfileDraft("work", "Before", "old note", ["old"], ["Work"]));
            var path = BrowserPolicy.ProfilePath(root, "work");
            Directory.CreateDirectory(Path.Combine(path, "Cache"));
            File.WriteAllText(Path.Combine(path, "Cache", "Cookies"), "protected browser data");
            new SearchSettingsStore(path).Save(new SearchSettings("google"));
            var service = new ProfileBackupService(root, repository);
            var reminder = service.GetRetentionReminder();
            var updatedReminder = service.SaveRetentionReminder(reminder, 1);
            Check(new ProfileBackupService(root, repository).GetRetentionReminder() == updatedReminder, "retention reminder persists");
            Reject(() => service.SaveRetentionReminder(reminder, 2), "stale retention setting rejected");
            using (var lease = new ProfileLease(path))
                Reject(() => service.Create(profile, false, true), "settings backup requires closed source");
            var backup = service.Create(profile, false, true);
            var id = backup.Manifest.Content.BackupId;
            Check(service.List().Single().Valid && service.Verify(id).Profile.Id == profile.Id && backup.Manifest.Content.Settings.Search?.EngineId == "google",
                "consistent SQLite backup and selected settings validate");
            Check(service.List().Count == 1 && updatedReminder.Threshold == 1, "retention reminder never deletes backups");
            var changed = repository.Update(profile with { DisplayName = "After", Notes = "new note", Tags = ["new"] }, profile.Revision);
            using (var lease = new ProfileLease(path)) Reject(() => service.RestoreMetadata(id, changed), "locked profile cannot be restored");
            Check(repository.GetBySlug("work")!.DisplayName == "After", "locked restore leaves current metadata intact");
            var restored = service.RestoreMetadata(id, changed);
            Check(restored.Id == profile.Id && restored.Revision == changed.Revision + 1 && restored.DisplayName == "Before" && restored.Notes == "old note" &&
                File.ReadAllText(Path.Combine(path, "Cache", "Cookies")) == "protected browser data", "metadata restored transactionally without touching browser data");
            Check(Directory.GetFiles(Path.Combine(root, ProfileRepository.BackupDirectoryName), "*.sqlite3").Length >= 1, "current metadata safety snapshot retained");
            var newer = repository.Update(restored with { Notes = "newest" }, restored.Revision);
            Reject(() => service.RestoreMetadata(id, restored), "stale restore preview rejected");
            Check(repository.GetBySlug("work")!.Notes == "newest", "stale restore preserves newer state");
            var other = repository.Create("other");
            Reject(() => service.RestoreMetadata(id, other), "conflicting identity rejected");
            var newProfile = service.RestoreAsNew(id, "recovered", "Recovered");
            Check(newProfile.Id != profile.Id && newProfile.Notes == "old note" &&
                new SearchSettingsStore(BrowserPolicy.ProfilePath(root, "recovered")).Load().EngineId == "google" &&
                !Directory.Exists(Path.Combine(BrowserPolicy.ProfilePath(root, "recovered"), "Cache")), "restore as new allocates identity and restores only selected settings");
            Reject(() => service.RestoreAsNew(id, "work", "Overwrite"), "restore as new cannot overwrite profile");

            var directory = Path.Combine(root, "manual-backups", "backup-" + id.ToString("D"));
            var manifestPath = Path.Combine(directory, "manifest.json");
            var original = File.ReadAllBytes(manifestPath);
            var manifest = JsonSerializer.Deserialize<ProfileBackupManifest>(original)!;
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest with { Sha256 = new string('0', 64) }));
            Reject(() => service.Verify(id), "corrupt manifest rejected");
            Check(!service.List().Single().Valid, "invalid backup is visible but disabled");
            File.WriteAllBytes(manifestPath, original);
            var database = Path.Combine(directory, "metadata.sqlite3");
            using (var file = new FileStream(database, FileMode.Append, FileAccess.Write, FileShare.None)) file.WriteByte(1);
            Reject(() => service.Verify(id), "corrupt database rejected");
            Check(repository.GetBySlug("work")!.Notes == "newest", "corrupt backup leaves current database unchanged");

            var failedRoot = Path.Combine(root, "failed");
            var failedRepo = new ProfileRepository(failedRoot);
            var failedSource = failedRepo.Create("source");
            var failedService = new ProfileBackupService(failedRoot, failedRepo);
            var failedBackup = failedService.Create(failedSource, false, false);
            var failedCurrent = failedRepo.Update(failedSource with { DisplayName = "Newer" }, failedSource.Revision);
            File.WriteAllText(Path.Combine(failedRoot, ProfileRepository.BackupDirectoryName), "blocks safety backup");
            Reject(() => failedService.RestoreMetadata(failedBackup.Manifest.Content.BackupId, failedCurrent), "failed safety backup aborts restore");
            Check(failedRepo.GetBySlug("source")!.DisplayName == "Newer", "failed restore preserves current data");
            var interrupted = Path.Combine(failedRoot, "manual-backups", ".creating-backup-interrupted");
            Directory.CreateDirectory(interrupted); File.WriteAllText(Path.Combine(interrupted, "sentinel"), "retain");
            Check(failedService.List().Count == 1 && File.ReadAllText(Path.Combine(interrupted, "sentinel")) == "retain", "incomplete backup ignored and retained");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"PASS: {count} profile backup checks");
    }
}
