using AegisBrowser.Core;

internal static class ProxyPresetTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL proxy presets: " + name); count++; }
        void Reject(Action action, string name)
        {
            try { action(); } catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { count++; return; }
            throw new Exception("FAIL proxy presets (accepted): " + name);
        }
        var root = Path.Combine(Path.GetTempPath(), "aegis-proxy-presets-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new ProxyPresetRepository(root);
            Check(repository.List().Count == 0 && !Directory.Exists(root), "empty lookup does not create storage");
            var draft = new ProxyDraft(ProxyProtocol.Http, "proxy.example", 8080, false, null, "endpoint", true);
            var preset = repository.Create("Work", "Controlled endpoint", draft);
            Check(preset.Id != Guid.Empty && preset.Revision == 1 && new ProxyPresetRepository(root).List().Single() == preset,
                "preset persists without browser or credential data");
            Check(!preset.ToString().Contains(preset.Description, StringComparison.Ordinal), "preset string omits description");
            Reject(() => repository.Create("work", "", draft), "duplicate names rejected case-insensitively");
            Reject(() => repository.Create("Bad auth", "", draft with { AuthenticationRequired = true, CredentialId = Guid.NewGuid() }), "credential reference rejected");
            Reject(() => repository.Create("Bad direct", "", new ProxyDraft(ProxyProtocol.Direct, "", 0, false, null, "", false)), "direct mode rejected");
            Reject(() => repository.Create("Bad host", "", draft with { Host = "http://host" }), "invalid endpoint rejected");
            var saved = repository.Update(preset with { Name = "Renamed", Settings = draft with { Port = 8081 } }, preset.Revision);
            Check(saved.Revision == 2 && new ProxyPresetRepository(root).List().Single().Settings.Port == 8081, "updates persist with revision");
            Reject(() => repository.Update(preset with { Name = "Stale" }, preset.Revision), "stale update rejected");
            Reject(() => repository.Delete(preset.Id, preset.Revision), "stale delete rejected");
            Check(repository.List().Single() == saved, "stale changes preserve the current preset");

            var wins = 0; var conflicts = 0;
            var tasks = Enumerable.Range(0, 2).Select(i => Task.Run(() =>
            {
                try { new ProxyPresetRepository(root).Update(saved with { Description = "writer " + i }, saved.Revision); Interlocked.Increment(ref wins); }
                catch (IOException) { Interlocked.Increment(ref conflicts); }
            })).ToArray();
            Task.WaitAll(tasks);
            Check(wins == 1 && conflicts == 1, "concurrent preset updates cannot both succeed");
            var current = repository.List().Single();
            var path = Path.Combine(root, ProxyPresetRepository.DirectoryName, "proxy-preset-" + current.Id.ToString("D") + ".json");
            var original = File.ReadAllText(path);
            Check(!original.Contains("CredentialId\": \"", StringComparison.Ordinal) && !original.Contains("Password", StringComparison.OrdinalIgnoreCase),
                "preset file contains no credential value or password field");
            var future = original.Replace("\"Version\": 1", "\"Version\": 99", StringComparison.Ordinal);
            File.WriteAllText(path, future);
            Reject(() => repository.List(), "unsupported preset version rejected");
            Check(File.ReadAllText(path) == future, "unsupported preset preserved");
            File.WriteAllText(path, "{\"Version\":1,\"Version\":1}");
            Reject(() => repository.List(), "duplicate JSON properties rejected");
            Check(File.ReadAllText(path) == "{\"Version\":1,\"Version\":1}", "malformed preset preserved");
            File.WriteAllText(path, new string('x', 16_385));
            Reject(() => repository.List(), "oversized preset rejected");
            File.WriteAllText(path, original);
            repository.Delete(current.Id, current.Revision);
            Check(repository.List().Count == 0 && !File.Exists(path), "confirmed revision removes only the selected preset");

            var directoryEntry = Path.Combine(root, ProxyPresetRepository.DirectoryName, "proxy-preset-" + Guid.NewGuid().ToString("D") + ".json");
            Directory.CreateDirectory(directoryEntry);
            Reject(() => repository.List(), "directory masquerading as a preset is rejected");
            Directory.Delete(directoryEntry);

            var blockedRoot = Path.Combine(root, "not-a-directory"); File.WriteAllText(blockedRoot, "retain");
            Reject(() => new ProxyPresetRepository(blockedRoot).Create("Blocked", "", draft), "filesystem failure reported");
            Reject(() => new ProxyPresetRepository(blockedRoot).List(), "invalid preset root is not reported as empty");
            Check(File.ReadAllText(blockedRoot) == "retain", "filesystem failure preserves existing data");
            var linkedRoot = Path.Combine(root, "linked"); var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(linkedRoot); Directory.CreateDirectory(outside);
            var link = Path.Combine(linkedRoot, ProxyPresetRepository.DirectoryName); var linked = false;
            try { Directory.CreateSymbolicLink(link, outside); linked = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            { Console.WriteLine("SKIP: proxy preset symlink fixture unavailable on this host"); }
            if (linked)
            {
                Reject(() => new ProxyPresetRepository(linkedRoot).Create("Linked", "", draft), "linked preset directory rejected");
                Check(!Directory.EnumerateFileSystemEntries(outside).Any(), "linked target remains untouched");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"PASS: {count} proxy preset checks");
    }
}
