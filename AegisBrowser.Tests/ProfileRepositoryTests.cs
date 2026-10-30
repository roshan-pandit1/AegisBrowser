using System.Diagnostics;
using System.Reflection;
using AegisBrowser.Core;
using Microsoft.Data.Sqlite;

internal static class ProfileRepositoryTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL profile metadata: " + name);
            count++;
        }
        void Reject(Action action, string name)
        {
            try { action(); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { count++; return; }
            throw new Exception("FAIL profile metadata (accepted): " + name);
        }

        var testRoot = Path.Combine(Path.GetTempPath(), "aegis-profile-metadata-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(testRoot, "profiles");
        var dbPath = Path.Combine(root, ProfileRepository.DatabaseFileName);
        try
        {
            var repository = new ProfileRepository(root);
            Check(repository.GetBySlug("default") is null && repository.List().Count == 0 && !Directory.Exists(root), "lookup does not create storage");
            var workPath = BrowserPolicy.ProfilePath(root, "work");
            var cachePath = Path.Combine(workPath, "Cache", "Default");
            Directory.CreateDirectory(cachePath);
            File.WriteAllText(Path.Combine(cachePath, "Cookies"), "existing-cookie-data");
            File.WriteAllText(Path.Combine(workPath, "privacy.json"), "existing-privacy-data");
            File.WriteAllText(Path.Combine(workPath, "search.json"), "existing-search-data");
            var catalog = new ProfileCatalog(root);
            Check(catalog.List().SequenceEqual(["work"]) && !File.Exists(dbPath), "legacy discovery is read-only");
            ProfileDefinition work;
            using (var lease = new ProfileLease(workPath))
            {
                work = repository.GetOrMigrate("WORK", lease);
                Reject(() => { using var duplicate = new ProfileLease(workPath); }, "migration does not weaken profile lease");
            }
            Check(work.Id != Guid.Empty && work.Slug == "work" && work.DisplayName == "work" && work.MetadataVersion == 1, "legacy identity created");
            using (var lease = new ProfileLease(workPath))
                Check(repository.GetOrMigrate("work", lease).Id == work.Id && new ProfileRepository(root).GetBySlug("WORK")?.Id == work.Id,
                    "identity stable across reopen and case");
            Check(File.ReadAllText(Path.Combine(cachePath, "Cookies")) == "existing-cookie-data" &&
                File.ReadAllText(Path.Combine(workPath, "privacy.json")) == "existing-privacy-data" &&
                File.ReadAllText(Path.Combine(workPath, "search.json")) == "existing-search-data", "legacy browser data and settings preserved");
            var backupRoot = Path.Combine(root, ProfileRepository.BackupDirectoryName);
            Check(Directory.GetFiles(backupRoot, "*.sqlite3").Length == 1, "migration made a database backup");

            var updated = repository.Update(work with
            {
                DisplayName = "Work profile", Notes = "private note", Tags = ["qa", "client-a"], Folders = ["Customers", "Testing"]
            }, work.Revision);
            var loaded = repository.GetBySlug("work")!;
            Check(updated.Revision == 2 && loaded.Id == work.Id && loaded.DisplayName == "Work profile" && loaded.Notes == "private note" &&
                loaded.Tags.SequenceEqual(["client-a", "qa"]) && loaded.Folders.SequenceEqual(["Customers", "Testing"]),
                "metadata fields round trip");
            Reject(() => repository.Update(work with { Notes = "stale" }, work.Revision), "stale revision rejected");
            Reject(() => repository.Update(loaded with { Tags = ["QA", "qa"] }, loaded.Revision), "case duplicate tags rejected");
            Reject(() => repository.Update(loaded with { DisplayName = " " }, loaded.Revision), "empty display name rejected");
            Check(repository.GetBySlug("work")!.Notes == "private note", "invalid and stale updates leave data intact");

            var beforeSecond = Directory.GetFiles(backupRoot, "*.sqlite3").ToHashSet(StringComparer.OrdinalIgnoreCase);
            var defaultPath = BrowserPolicy.ProfilePath(root, "default");
            Directory.CreateDirectory(defaultPath);
            File.WriteAllText(Path.Combine(defaultPath, "sentinel"), "default-profile-data");
            ProfileDefinition defaultProfile;
            using (var lease = new ProfileLease(defaultPath)) defaultProfile = repository.GetOrMigrate("default", lease);
            Check(defaultProfile.Id != work.Id && File.ReadAllText(Path.Combine(defaultPath, "sentinel")) == "default-profile-data", "default and named profiles remain independent");
            var secondBackup = Directory.GetFiles(backupRoot, "*.sqlite3").Single(path => !beforeSecond.Contains(path));
            var restoreRoot = Path.Combine(testRoot, "restored");
            Directory.CreateDirectory(restoreRoot);
            File.Copy(secondBackup, Path.Combine(restoreRoot, ProfileRepository.DatabaseFileName));
            var restored = new ProfileRepository(restoreRoot);
            Check(restored.GetBySlug("work")?.Id == work.Id && restored.GetBySlug("work")?.Notes == "private note" &&
                restored.GetBySlug("default") is null, "pre-migration backup restores prior metadata");

            var created = repository.Create("Research_1");
            Check(created.Slug == "research_1" && created.Id != work.Id && repository.GetBySlug("RESEARCH_1")?.Id == created.Id &&
                Directory.Exists(BrowserPolicy.ProfilePath(root, "research_1")), "new profile has stable metadata and old folder layout");
            Check(repository.List().Select(p => p.Slug).SequenceEqual(["default", "research_1", "work"]), "metadata list sorted by slug");
            var closedLease = new ProfileLease(workPath);
            closedLease.Dispose();
            Reject(() => repository.GetOrMigrate("work", closedLease), "disposed lease rejected");
            using (var wrongLease = new ProfileLease(defaultPath))
                Reject(() => repository.GetOrMigrate("work", wrongLease), "different profile lease rejected");
            Reject(() => repository.Create("RESEARCH_1"), "case duplicate slug rejected");
            foreach (var invalid in new[] { "", "../escape", "a/b", "a.b", "with space", new string('x', 41) })
                Reject(() => repository.Create(invalid), "invalid slug " + invalid);

            var racers = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
            {
                try { return repository.Create("race").Id; }
                catch (IOException) { return Guid.Empty; }
            })).ToArray();
            Task.WaitAll(racers);
            Check(racers.Count(t => t.Result != Guid.Empty) == 1 && repository.GetBySlug("RACE")?.Id == racers.Single(t => t.Result != Guid.Empty).Result,
                "concurrent creators produce exactly one identity");

            var failedRoot = Path.Combine(testRoot, "failure");
            var failedPath = BrowserPolicy.ProfilePath(failedRoot, "legacy");
            Directory.CreateDirectory(failedPath);
            File.WriteAllText(Path.Combine(failedPath, "sentinel"), "untouched");
            File.WriteAllText(Path.Combine(failedRoot, ProfileRepository.BackupDirectoryName), "blocks backup directory");
            var failedRepository = new ProfileRepository(failedRoot);
            using (var lease = new ProfileLease(failedPath)) Reject(() => failedRepository.GetOrMigrate("legacy", lease), "backup failure stops migration");
            Check(failedRepository.GetBySlug("legacy") is null && File.ReadAllText(Path.Combine(failedPath, "sentinel")) == "untouched",
                "failed migration leaves browser data and metadata unchanged");
            File.Delete(Path.Combine(failedRoot, ProfileRepository.BackupDirectoryName));
            using (var lease = new ProfileLease(failedPath)) Check(failedRepository.GetOrMigrate("legacy", lease).Id != Guid.Empty, "migration retries after failure");

            if (OperatingSystem.IsWindows())
            {
                var readonlyPath = BrowserPolicy.ProfilePath(failedRoot, "readonly");
                Directory.CreateDirectory(readonlyPath);
                File.WriteAllText(Path.Combine(readonlyPath, "sentinel"), "read-only data");
                var readonlyDb = Path.Combine(failedRoot, ProfileRepository.DatabaseFileName);
                var attributes = File.GetAttributes(readonlyDb);
                try
                {
                    File.SetAttributes(readonlyDb, attributes | FileAttributes.ReadOnly);
                    using var lease = new ProfileLease(readonlyPath);
                    Reject(() => failedRepository.GetOrMigrate("readonly", lease), "read-only metadata prevents migration");
                }
                finally { File.SetAttributes(readonlyDb, attributes); }
                Check(failedRepository.GetBySlug("readonly") is null && File.ReadAllText(Path.Combine(readonlyPath, "sentinel")) == "read-only data",
                    "read-only failure preserves data");
                using var retryLease = new ProfileLease(readonlyPath);
                Check(failedRepository.GetOrMigrate("readonly", retryLease).Id != Guid.Empty, "migration retries when write permission returns");
            }

            var interruptedPath = BrowserPolicy.ProfilePath(root, "interrupted");
            Directory.CreateDirectory(interruptedPath);
            File.WriteAllText(Path.Combine(interruptedPath, "sentinel"), "interrupted data");
            CrashDuringTransaction(dbPath);
            Check(repository.GetBySlug("work")!.Notes == "private note" && repository.GetBySlug("interrupted") is null,
                "uncommitted migration rolls back after process termination");
            using (var lease = new ProfileLease(interruptedPath))
                Check(repository.GetOrMigrate("interrupted", lease).Id != Guid.Empty && File.ReadAllText(Path.Combine(interruptedPath, "sentinel")) == "interrupted data",
                    "interrupted migration retries without cache loss");

            var corruptRoot = Path.Combine(testRoot, "corrupt");
            var corruptPath = BrowserPolicy.ProfilePath(corruptRoot, "legacy");
            Directory.CreateDirectory(corruptPath);
            File.WriteAllText(Path.Combine(corruptPath, "sentinel"), "safe");
            var corruptDb = Path.Combine(corruptRoot, ProfileRepository.DatabaseFileName);
            var invalidBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            File.WriteAllBytes(corruptDb, invalidBytes);
            using (var lease = new ProfileLease(corruptPath)) Reject(() => new ProfileRepository(corruptRoot).GetOrMigrate("legacy", lease), "corrupt metadata rejected");
            Check(File.ReadAllBytes(corruptDb).SequenceEqual(invalidBytes) && File.ReadAllText(Path.Combine(corruptPath, "sentinel")) == "safe", "corrupt database and cache preserved");

            using (var db = Open(dbPath))
            using (var command = db.CreateCommand()) { command.CommandText = "PRAGMA user_version=99"; command.ExecuteNonQuery(); }
            Reject(() => repository.GetBySlug("work"), "future database version rejected");
            using (var db = Open(dbPath))
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT notes FROM profiles WHERE slug='work'";
                Check((string?)command.ExecuteScalar() == "private note", "future version left intact");
            }

            if (OperatingSystem.IsWindows()) CheckJunction(testRoot, Check, Reject);
        }
        finally { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); }
        Console.WriteLine($"PASS: {count} profile metadata checks");
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void CrashDuringTransaction(string dbPath)
    {
        var executable = Environment.ProcessPath ?? throw new Exception("Process path unavailable");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--hold-metadata-transaction");
        start.ArgumentList.Add(dbPath);
        using var child = Process.Start(start) ?? throw new Exception("Could not start metadata crash probe");
        try
        {
            var line = child.StandardOutput.ReadLineAsync();
            if (!line.Wait(TimeSpan.FromSeconds(15)) || line.Result != "READY") throw new Exception("Metadata crash probe did not become ready");
            child.Kill(true);
            if (!child.WaitForExit(15000)) throw new Exception("Metadata crash probe did not terminate");
        }
        finally { if (!child.HasExited) { child.Kill(true); child.WaitForExit(); } }
    }

    private static void CheckJunction(string testRoot, Action<bool, string> check, Action<Action, string> reject)
    {
        var root = Path.Combine(testRoot, "junction-profiles");
        var target = Path.Combine(testRoot, "junction-target");
        var link = BrowserPolicy.ProfilePath(root, "linked");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "sentinel"), "outside");
        MakeJunction(link, target);
        try
        {
            reject(() => ProfileRepository.CheckSafeProfilePath(root, "linked"), "linked profile rejected before lease");
            check(!new ProfileCatalog(root).List().Contains("linked"), "junction excluded from profile discovery");
            check(File.ReadAllText(Path.Combine(target, "sentinel")) == "outside" && !File.Exists(Path.Combine(target, ".profile.lock")), "junction target untouched");
        }
        finally { Directory.Delete(link); }

        var oldPath = BrowserPolicy.ProfilePath(root, "legacy");
        Directory.CreateDirectory(oldPath);
        var backupLink = Path.Combine(root, ProfileRepository.BackupDirectoryName);
        MakeJunction(backupLink, target);
        try
        {
            using var lease = new ProfileLease(oldPath);
            reject(() => new ProfileRepository(root).GetOrMigrate("legacy", lease), "linked backup directory rejected");
            check(Directory.GetFiles(target).Length == 1 && File.ReadAllText(Path.Combine(target, "sentinel")) == "outside", "backup junction target untouched");
        }
        finally { Directory.Delete(backupLink); }
    }

    private static void MakeJunction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new Exception("Could not create junction probe");
        if (!process.WaitForExit(15000)) { process.Kill(true); throw new Exception("Junction probe timed out"); }
        if (process.ExitCode != 0) throw new Exception("Could not create junction for profile test: " + process.StandardError.ReadToEnd());
    }
}
