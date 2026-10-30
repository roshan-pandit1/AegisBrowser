using System.Text;
using AegisBrowser.Core;
using Microsoft.Data.Sqlite;

internal static class ProxyTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL proxy: " + name); count++; }
        void Reject(Action action, string name)
        {
            try { action(); } catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { count++; return; }
            throw new Exception("FAIL proxy (accepted): " + name);
        }
        var direct = new ProxyDraft(ProxyProtocol.Direct, "", 0, false, null, "", false);
        var http = new ProxyDraft(ProxyProtocol.Http, "proxy.example", 8080, false, null, "Test", true);
        foreach (var draft in new[] { direct, http, http with { Protocol = ProxyProtocol.Https, Host = "127.0.0.1", Port = 443 },
            http with { Protocol = ProxyProtocol.Socks5, Host = "2001:db8::1", Port = 1080 } })
        { ProxyValidation.Validate(draft); count++; }
        foreach (var host in new[] { "", "https://example.com", "user:pass@example.com", "proxy:1234", "../escape", "a b", "a..b", "-bad.test", "256.1.1.1", "01.2.3.4", "[::1]", "fe80::1%3", "host\nname", new string('a', 254) })
            Reject(() => ProxyValidation.Validate(http with { Host = host }), "invalid host");
        foreach (var invalid in new[] { http with { Protocol = (ProxyProtocol)99 }, http with { Port = 0 }, http with { Port = 65536 },
            http with { RequireProxy = false }, http with { AuthenticationRequired = true }, http with { CredentialId = Guid.Empty },
            direct with { Host = "proxy.example" }, direct with { CredentialId = Guid.NewGuid() }, http with { Label = "x\ny" } })
            Reject(() => ProxyValidation.Validate(invalid), "invalid configuration");

        var testRoot = Path.Combine(Path.GetTempPath(), "aegis-proxy-tests-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(testRoot, "profiles");
        try
        {
            var profiles = new ProfileRepository(root);
            var first = profiles.Create("first"); var second = profiles.Create("second");
            var firstPath = BrowserPolicy.ProfilePath(root, first.Slug);
            var cache = Path.Combine(firstPath, "Cache"); Directory.CreateDirectory(cache);
            var sentinel = Path.Combine(cache, "Cookies"); File.WriteAllText(sentinel, "browser-data-unchanged");
            var repo = new ProxyRepository(root); var dbPath = Path.Combine(root, ProxyRepository.DatabaseFileName);
            using var firstLease = new ProfileLease(firstPath);
            using var secondLease = new ProfileLease(BrowserPolicy.ProfilePath(root, second.Slug));
            Check(repo.Get(first, firstLease) is null && !File.Exists(dbPath), "legacy profile starts without creating proxy database");
            repo.EnsureRuntimeReady(first, firstLease); count++;
            Reject(() => repo.Get(first, secondLease), "wrong profile lease rejected");
            var saved = repo.Save(first, firstLease, http, 0);
            Check(saved.ProfileId == first.Id && saved.Id != Guid.Empty && saved.Revision == 1 &&
                new ProxyRepository(root).Get(first, firstLease) == saved, "configuration and GUID persist");
            Check(repo.Get(second, secondLease) is null, "configuration isolated by profile GUID");
            repo.EnsureRuntimeReady(second, secondLease); count++;
            Reject(() => repo.EnsureRuntimeReady(first, firstLease), "configured proxy blocks unsupported runtime");
            Reject(() => repo.Save(first, firstLease, http, 0), "stale proxy revision rejected");
            Check(repo.Get(first, firstLease) == saved, "stale update leaves current configuration unchanged");
            var movedDb = Path.Combine(testRoot, "held.sqlite3"); File.Move(dbPath, movedDb);
            Reject(() => repo.EnsureRuntimeReady(first, firstLease), "guard blocks launch when configuration database is missing");
            Reject(() => repo.Save(first, firstLease, direct, 0), "missing guarded database cannot be replaced as a new configuration");
            File.Move(movedDb, dbPath);
            var explicitDirect = repo.Save(first, firstLease, direct, saved.Revision);
            Check(explicitDirect.Id == saved.Id && explicitDirect.Revision == 2 && !File.Exists(Path.Combine(firstPath, ProxyRepository.RequiredMarkerName)),
                "explicit direct update removes marker after commit");
            repo.EnsureRuntimeReady(first, firstLease); count++;
            Check(File.ReadAllText(sentinel) == "browser-data-unchanged", "proxy changes preserve browser data");

            // A marker left by interruption cannot be interpreted as direct.
            File.WriteAllText(Path.Combine(firstPath, ProxyRepository.RequiredMarkerName), "interrupted");
            Reject(() => repo.EnsureRuntimeReady(first, firstLease), "interrupted direct transition blocks startup");
            explicitDirect = repo.Save(first, firstLease, direct, explicitDirect.Revision);
            repo.EnsureRuntimeReady(first, firstLease); count++;
            var revisedProfile = profiles.Update(first with { Notes = "changed" }, first.Revision);
            Reject(() => repo.Save(first, firstLease, direct, explicitDirect.Revision), "stale profile snapshot rejected");
            first = revisedProfile;

            // Two stale views cannot both win even in the same manager process.
            var wins = 0; var conflicts = 0;
            var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                try { repo.Save(first, firstLease, direct, explicitDirect.Revision); Interlocked.Increment(ref wins); }
                catch (IOException) { Interlocked.Increment(ref conflicts); }
            })).ToArray();
            Task.WaitAll(tasks);
            Check(wins == 1 && conflicts == 1, "concurrent updates enforce revision");

            var current = repo.Get(first, firstLease)!;
            if (OperatingSystem.IsWindows())
            {
                var vault = new ProxyCredentialVault(root);
                var credentials = new ProxyCredentials("fixture-user", "fixture-password-for-testing");
                Check(!credentials.ToString().Contains(credentials.Username, StringComparison.Ordinal) &&
                    !credentials.ToString().Contains(credentials.Password, StringComparison.Ordinal), "credentials redact ToString");
                var service = new ProxyConfigurationService(repo, vault);
                var auth = http with { AuthenticationRequired = true };
                var firstResult = service.Save(first, firstLease, auth, current.Revision, credentials);
                var firstId = firstResult.Configuration.Settings.CredentialId!.Value;
                var reread = new ProxyCredentialVault(root).Read(first.Id, firstId);
                Check(reread.Username == credentials.Username && reread.Password == credentials.Password, "DPAPI credential persists across vault instances");
                var blob = Path.Combine(root, ProxyCredentialVault.DirectoryName, first.Id.ToString("N"), firstId.ToString("N") + ".bin");
                var bytes = File.ReadAllBytes(blob);
                Check(!Encoding.UTF8.GetString(bytes).Contains(credentials.Username, StringComparison.Ordinal) &&
                    !Encoding.UTF8.GetString(bytes).Contains(credentials.Password, StringComparison.Ordinal) &&
                    !Encoding.UTF8.GetString(File.ReadAllBytes(dbPath)).Contains(credentials.Password, StringComparison.Ordinal), "plaintext excluded from ciphertext and SQLite");
                Reject(() => vault.Read(second.Id, firstId), "credential reference cannot cross profile identities");
                var otherDirectory = Path.Combine(root, ProxyCredentialVault.DirectoryName, second.Id.ToString("N")); Directory.CreateDirectory(otherDirectory);
                var copiedBlob = Path.Combine(otherDirectory, firstId.ToString("N") + ".bin"); File.Copy(blob, copiedBlob);
                Reject(() => vault.Read(second.Id, firstId), "copied ciphertext cannot be replayed under a different profile");
                vault.Delete(second.Id, firstId);
                Reject(() => service.Save(second, secondLease, auth with { CredentialId = firstId }, 0), "cross-profile credential assignment rejected");
                Reject(() => service.Save(first, firstLease, auth, current.Revision, credentials), "stale credential replacement rejected");
                Check(vault.Read(first.Id, firstId).Password == credentials.Password, "stale replacement preserves old credential");
                var replacement = new ProxyCredentials("fixture-user-2", "fixture-password-2");
                var replaced = service.Save(first, firstLease, auth, firstResult.Configuration.Revision, replacement);
                var secondId = replaced.Configuration.Settings.CredentialId!.Value;
                Check(secondId != firstId && !File.Exists(blob) && vault.Read(first.Id, secondId).Username == replacement.Username,
                    "credential replacement commits new secret before removing old");
                var export = Path.Combine(testRoot, "export.json");
                new ProfileTransfer(root, profiles, new ProfileTemplateRepository(root)).Export(export, [first], [], false, false, false);
                var exportText = File.ReadAllText(export);
                Check(!exportText.Contains(replacement.Username, StringComparison.Ordinal) && !exportText.Contains(replacement.Password, StringComparison.Ordinal) &&
                    !exportText.Contains(secondId.ToString("D"), StringComparison.Ordinal), "metadata export excludes credentials and references");
                var backup = new ProfileBackupService(root, profiles).Create(first, false, false);
                var backupDir = Path.Combine(root, "manual-backups", "backup-" + backup.Manifest.Content.BackupId.ToString("D"));
                Check(Directory.GetFiles(backupDir).All(p => !Encoding.UTF8.GetString(File.ReadAllBytes(p)).Contains(secondId.ToString("D"), StringComparison.Ordinal)),
                    "metadata backup excludes proxy credentials and references");
                var copy = new ProfileDuplicationService(root, profiles).DuplicateClean(first, "proxy-copy", "Clean copy", false, false, false);
                using (var copyLease = new ProfileLease(BrowserPolicy.ProfilePath(root, copy.Slug)))
                    Check(repo.Get(copy, copyLease) is null, "clean duplicate does not copy proxy configuration");
                Reject(() => vault.Read(copy.Id, secondId), "clean duplicate does not receive protected credentials");
                var disabled = service.Save(first, firstLease, direct, replaced.Configuration.Revision);
                Reject(() => vault.Read(first.Id, secondId), "explicit direct mode removes old credential");
                Check(disabled.CleanupWarning is null, "credential removal completed");
                var brokenId = vault.Store(first.Id, credentials);
                var brokenPath = Path.Combine(root, ProxyCredentialVault.DirectoryName, first.Id.ToString("N"), brokenId.ToString("N") + ".bin");
                File.WriteAllText(brokenPath, "corrupt ciphertext");
                Reject(() => vault.Read(first.Id, brokenId), "corrupt credential rejected");
                Check(File.ReadAllText(brokenPath) == "corrupt ciphertext", "corrupt credential retained for diagnosis");
                vault.Delete(first.Id, brokenId);
            }

            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=99"; command.ExecuteNonQuery();
            }
            Reject(() => repo.EnsureRuntimeReady(first, firstLease), "future database version blocks startup");
            Check(File.Exists(dbPath), "unsupported database preserved");
        }
        finally { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); }
        Console.WriteLine($"PASS: {count} proxy configuration and credential checks");
    }
}
