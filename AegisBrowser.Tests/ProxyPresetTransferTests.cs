using System.Security.Cryptography;
using System.Text.Json;
using AegisBrowser.Core;

internal static class ProxyPresetTransferTests
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL proxy preset transfer: " + name); checks++; }
        void Reject(Action action, string name)
        {
            try { action(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { checks++; return; }
            throw new Exception("FAIL proxy preset transfer (accepted): " + name);
        }
        string Envelope(PortableProxyPreset content)
        {
            var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content))).ToLowerInvariant();
            return JsonSerializer.Serialize(new PortableProxyPresetEnvelope(content, hash));
        }

        var workspace = Path.Combine(Path.GetTempPath(), "aegis-preset-transfer-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workspace);
            var root = Path.Combine(workspace, "profiles");
            var repo = new ProxyPresetRepository(root);
            var transfer = new ProxyPresetTransfer(root, repo);
            var draft = new ProxyDraft(ProxyProtocol.Http, "proxy.example", 8080, false, null, "endpoint", true);
            var original = repo.Create("Work", "Local fixture", draft);
            var output = Path.Combine(workspace, "preset.json");
            transfer.Export(output, original);
            var exported = File.ReadAllText(output);
            Check(!exported.Contains("Credential", StringComparison.OrdinalIgnoreCase) && !exported.Contains("Password", StringComparison.OrdinalIgnoreCase) &&
                !exported.Contains(original.Id.ToString("D"), StringComparison.OrdinalIgnoreCase), "export excludes identities and credential fields");
            var preview = transfer.Preview(output);
            Check(preview.Name == original.Name && preview.Description == original.Description && preview.Host == draft.Host && preview.Port == draft.Port,
                "preview round-trips the non-secret endpoint");
            var importRoot = Path.Combine(workspace, "imported");
            var importedRepo = new ProxyPresetRepository(importRoot);
            var importer = new ProxyPresetTransfer(importRoot, importedRepo);
            var imported = importer.Import(preview);
            Check(imported.Id != original.Id && imported.Settings == draft && imported.Revision == 1 && importedRepo.List().Single() == imported,
                "import creates a new inactive preset identity");
            Check(!File.Exists(Path.Combine(importRoot, "proxy-config.sqlite3")) && !Directory.EnumerateDirectories(importRoot, "profile-*").Any(),
                "import does not configure profiles or create browser storage");
            Reject(() => importer.Import(preview), "duplicate name rejected");
            Check(importedRepo.List().Single() == imported, "duplicate import leaves the existing preset intact");
            var raceRoot = Path.Combine(workspace, "concurrent");
            var raceRepo = new ProxyPresetRepository(raceRoot);
            var race = new ProxyPresetTransfer(raceRoot, raceRepo);
            var successes = 0; var conflicts = 0;
            Task.WaitAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            {
                try { race.Import(preview); Interlocked.Increment(ref successes); }
                catch (IOException) { Interlocked.Increment(ref conflicts); }
            })).ToArray());
            Check(successes == 1 && conflicts == 1 && raceRepo.List().Count == 1, "concurrent imports cannot both claim the same preset name");
            Reject(() => transfer.Export(output, original), "existing export is not overwritten");
            Check(File.ReadAllText(output) == exported, "existing export remains unchanged");
            Reject(() => transfer.Export(Path.Combine(root, "inside.json"), original), "export inside profile root rejected");

            var updated = repo.Update(original with { Name = "Updated" }, original.Revision);
            var stalePath = Path.Combine(workspace, "stale.json");
            Reject(() => transfer.Export(stalePath, original), "stale preset snapshot rejected");
            Check(!File.Exists(stalePath) && repo.List().Single() == updated, "stale export creates no file or mutation");

            var badPath = Path.Combine(workspace, "bad.json");
            var tampered = exported.Replace("proxy.example", "other.example", StringComparison.Ordinal);
            File.WriteAllText(badPath, tampered);
            Reject(() => transfer.Preview(badPath), "tampered checksum rejected");
            Check(File.ReadAllText(badPath) == tampered, "invalid import is preserved");
            File.WriteAllText(badPath, Envelope(preview with { Version = 99 }));
            Reject(() => transfer.Preview(badPath), "future version rejected");
            File.WriteAllText(badPath, Envelope(preview with { Host = "user:password@proxy.example" }));
            Reject(() => transfer.Preview(badPath), "credential-bearing host rejected even with a valid checksum");
            File.WriteAllText(badPath, Envelope(preview with { Protocol = (ProxyProtocol)99 }));
            Reject(() => transfer.Preview(badPath), "unsupported protocol rejected");
            File.WriteAllText(badPath, Envelope(preview with { Protocol = ProxyProtocol.Direct }));
            Reject(() => transfer.Preview(badPath), "direct mode is not imported as a proxy preset");
            File.WriteAllText(badPath, Envelope(preview with { Port = 0 }));
            Reject(() => transfer.Preview(badPath), "invalid port rejected");
            File.WriteAllText(badPath, "{\"Content\":{},\"content\":{},\"Sha256\":\"00\"}");
            Reject(() => transfer.Preview(badPath), "duplicate JSON fields rejected");
            File.WriteAllText(badPath, Envelope(preview).Replace("\"Content\":{", "\"Content\":{\"Password\":\"fixture\",", StringComparison.Ordinal));
            Reject(() => transfer.Preview(badPath), "unknown password field rejected");
            File.WriteAllText(badPath, new string('x', 16_385));
            Reject(() => transfer.Preview(badPath), "oversized input rejected");
            File.WriteAllText(badPath, "{ invalid");
            Reject(() => transfer.Preview(badPath), "malformed JSON rejected");
            Reject(() => importer.Import(preview with { Host = "../escape" }), "invalid in-memory preview rejected");
            Check(importedRepo.List().Single() == imported, "invalid imports leave existing presets unchanged");

            var outside = Path.Combine(workspace, "outside"); Directory.CreateDirectory(outside);
            var link = Path.Combine(workspace, "linked"); var linked = false;
            try { Directory.CreateSymbolicLink(link, outside); linked = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            { Console.WriteLine("SKIP: proxy preset transfer symlink fixture unavailable on this host"); }
            if (linked)
            {
                Reject(() => transfer.Export(Path.Combine(link, "preset.json"), updated), "linked export directory rejected");
                Check(!Directory.EnumerateFileSystemEntries(outside).Any(), "linked export target remains untouched");
                File.Copy(output, Path.Combine(outside, "input.json"));
                Reject(() => transfer.Preview(Path.Combine(link, "input.json")), "linked import ancestor rejected");
            }
        }
        finally { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); }
        Console.WriteLine($"PASS: {checks} proxy preset transfer checks");
    }
}
