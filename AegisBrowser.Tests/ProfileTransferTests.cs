using System.IO.Compression;
using System.Text.Json;
using AegisBrowser.Core;

internal static class ProfileTransferTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL profile transfer: " + name); count++; }
        void Reject(Action action, string name)
        {
            try { action(); } catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { count++; return; }
            throw new Exception("FAIL profile transfer (accepted): " + name);
        }
        var testRoot = Path.Combine(Path.GetTempPath(), "aegis-transfer-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(testRoot, "profiles");
        try
        {
            var repository = new ProfileRepository(root); var templates = new ProfileTemplateRepository(root);
            var source = repository.Create(new ProfileDraft("alpha", "Alpha", "exported note", ["qa"], ["Team"]));
            var sourcePath = BrowserPolicy.ProfilePath(root, "alpha");
            Directory.CreateDirectory(Path.Combine(sourcePath, "Cache"));
            File.WriteAllText(Path.Combine(sourcePath, "Cache", "Cookies"), "secret-browser-sentinel");
            new SearchSettingsStore(sourcePath).Save(new SearchSettings("google"));
            var template = templates.Create("Template", "Description", ["qa"], ["Team"], new(null, new SearchSettings("bing")));
            var transfer = new ProfileTransfer(root, repository, templates);
            var json = Path.Combine(testRoot, "export.json");
            var zip = Path.Combine(testRoot, "export.zip");
            transfer.Export(json, [source], [template], false, true, false);
            transfer.Export(zip, [source], [template], false, true, true);
            Check(!File.ReadAllText(json).Contains("secret-browser-sentinel", StringComparison.Ordinal), "export excludes browser storage");
            var preview = transfer.Preview(json); var zipPreview = transfer.Preview(zip);
            Check(preview.Profiles.Single().DestinationSlug == "alpha-copy" && zipPreview.Profiles.Single().Source.SourceId == source.Id &&
                preview.Profiles.Single().Source.Settings.Search?.EngineId == "google", "JSON and ZIP preview round trip");
            var imported = transfer.Import(preview, CancellationToken.None);
            var copy = repository.GetBySlug("alpha-copy")!;
            Check(imported.All(r => r.Outcome == ProfileOperationOutcome.Succeeded) && copy.Id != source.Id && copy.Notes == "exported note" &&
                new SearchSettingsStore(BrowserPolicy.ProfilePath(root, copy.Slug)).Load().EngineId == "google" &&
                !Directory.Exists(Path.Combine(BrowserPolicy.ProfilePath(root, copy.Slug), "Cache")), "import creates fresh identity and excludes cache");
            Check(templates.List().Count == 2 && templates.List().Any(t => t.Id != template.Id && t.Name == "Template (imported)"), "template import allocates new identity");
            Reject(() => transfer.Export(json, [source], [], false, false, false), "existing export file not replaced");
            Check(transfer.Preview(json).Profiles.Length == 1, "existing export remains intact");
            Reject(() => transfer.Export(Path.Combine(sourcePath, "unsafe.json"), [source], [], false, false, false), "export inside profile root rejected");

            var collisionPreview = transfer.Preview(json);
            var collisionSlug = collisionPreview.Profiles.Single().DestinationSlug;
            var collision = repository.Create(collisionSlug);
            File.WriteAllText(Path.Combine(BrowserPolicy.ProfilePath(root, collisionSlug), "sentinel"), "preserve");
            var collisionResult = transfer.Import(collisionPreview with { Templates = [] }, CancellationToken.None);
            Check(collisionResult.Single().Outcome == ProfileOperationOutcome.Failed && repository.GetBySlug(collisionSlug)!.Id == collision.Id &&
                File.ReadAllText(Path.Combine(BrowserPolicy.ProfilePath(root, collisionSlug), "sentinel")) == "preserve", "preview collision never overwrites existing profile");
            var canceledPreview = transfer.Preview(json);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var canceled = transfer.Import(canceledPreview, cancellation.Token);
            Check(canceled.All(r => r.Outcome == ProfileOperationOutcome.Canceled) && repository.GetBySlug(canceledPreview.Profiles[0].DestinationSlug) is null,
                "cancellation leaves unstarted imports unchanged");

            var badJson = Path.Combine(testRoot, "bad.json"); File.WriteAllText(badJson, "{ not json");
            Reject(() => transfer.Preview(badJson), "malformed JSON rejected");
            var duplicateFields = Path.Combine(testRoot, "duplicate.json"); File.WriteAllText(duplicateFields, "{\"Content\":{},\"Content\":{},\"Sha256\":\"\"}");
            Reject(() => transfer.Preview(duplicateFields), "duplicate JSON fields rejected");
            var envelope = JsonSerializer.Deserialize<ProfileExportEnvelope>(File.ReadAllText(json))!;
            var badChecksum = Path.Combine(testRoot, "checksum.json"); File.WriteAllText(badChecksum, JsonSerializer.Serialize(envelope with { Sha256 = new string('0', 64) }));
            Reject(() => transfer.Preview(badChecksum), "checksum mismatch rejected");
            var future = Path.Combine(testRoot, "future.json"); File.WriteAllText(future, JsonSerializer.Serialize(envelope with { Content = envelope.Content with { FormatVersion = 99 } }));
            Reject(() => transfer.Preview(future), "future version rejected");
            var duplicateItems = Path.Combine(testRoot, "duplicates.json");
            File.WriteAllText(duplicateItems, JsonSerializer.Serialize(envelope with { Content = envelope.Content with { Profiles = [envelope.Content.Profiles[0], envelope.Content.Profiles[0]] } }));
            Reject(() => transfer.Preview(duplicateItems), "duplicate identities rejected");
            var slip = Path.Combine(testRoot, "slip.zip");
            using (var file = File.Create(slip)) using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("../escape.json").Open())) writer.Write("{}");
            Reject(() => transfer.Preview(slip), "ZIP traversal rejected");
            Check(!File.Exists(Path.Combine(testRoot, "escape.json")), "ZIP is never extracted");
            var corruptZip = Path.Combine(testRoot, "corrupt.zip"); File.WriteAllBytes(corruptZip, [(byte)'P', (byte)'K', 3, 4, 1, 2]);
            Reject(() => transfer.Preview(corruptZip), "corrupt ZIP rejected");
            var oversized = Path.Combine(testRoot, "oversized.json");
            using (var file = File.Create(oversized)) file.SetLength(ProfileTransfer.MaxBytes + 1L);
            Reject(() => transfer.Preview(oversized), "oversized input rejected");
        }
        finally { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); }
        Console.WriteLine($"PASS: {count} profile transfer checks");
    }
}
