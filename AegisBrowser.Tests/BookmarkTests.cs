using AegisBrowser.Core;

internal static class BookmarkTests
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); count++; }
        void Reject(Action action, string name)
        {
            try { action(); throw new Exception("FAIL: " + name); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { count++; }
        }
        var root = Path.Combine(Path.GetTempPath(), "aegis-bookmarks-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var lease = new ProfileLease(root);
        if (OperatingSystem.IsWindows())
        {
            var moved = root + "-moved";
            var renameBlocked = false;
            using var first = BookmarkDirectoryObservation.Open(root, lease);
            using var second = BookmarkDirectoryObservation.Open(root, lease);
            Check(first.DirectoryIdentity == first.ReadHeldIdentity(), "held directory identity remains stable");
            Check(first.DirectoryIdentity == second.DirectoryIdentity, "two handles observe the same directory identity");
            Check(first.FinalPath.EndsWith(Path.GetFileName(root), StringComparison.OrdinalIgnoreCase), "final handle path names the fixture profile");
            Reject(() => BookmarkDirectoryObservation.Open(Path.GetTempPath(), lease), "mismatched lease rejects directory observation");
            try { Directory.Move(root, moved); }
            catch (IOException) { renameBlocked = true; }
            finally { if (Directory.Exists(moved)) Directory.Move(moved, root); }
            Check(renameBlocked, "list-access handles block fixture profile rename");
            Check(first.DirectoryIdentity == first.ReadHeldIdentity(), "directory identity survives blocked rename");
            Console.WriteLine($"BOOKMARK_DIRECTORY_OBSERVATION: volume={first.DirectoryIdentity.VolumeSerial:X} id={first.DirectoryIdentity.High:X16}{first.DirectoryIdentity.Low:X16} final={first.FinalPath} held-rename-blocked={renameBlocked}");
        }
        var store = new BookmarkStore(root, lease);
        Check(store.Snapshot().Items.Count == 0, "new store empty");
        Check(BookmarkStore.NormalizeUrl("HTTPS://Example.COM:443/a#fragment") == "https://example.com/a", "normalize URL");
        foreach (var url in new[] { "javascript:alert(1)", "file:///test", "about:blank", "https://user:pass@example.com", "https://aegis-diagnostics.invalid/", "https://example.com/\n", "https://example.com/" + new string('a', 2049) })
            Reject(() => BookmarkStore.NormalizeUrl(url), "reject unsafe address");
        Check(BookmarkStore.CleanText("  a\u202eb\u0001  ") == "ab", "remove unsafe title controls");
        Reject(() => BookmarkStore.CleanText(new string('x', 257)), "text limit");
        var now = DateTimeOffset.UtcNow;
        var entry = new BookmarkEntry(Guid.NewGuid(), "Example", "https://example.com/a", null, now, now, 0);
        var doc = store.Snapshot(); doc.Items.Add(entry); store.Save(doc);
        Check(new BookmarkStore(root, lease).Snapshot().Items.Single() == entry, "persist and reload");
        var stale = store.Snapshot();
        var otherWindow = new BookmarkStore(root, lease);
        var otherWindowSnapshot = otherWindow.Snapshot();
        var changed = 0; store.Changed += (_, _) => changed++;
        doc = store.Snapshot(); doc.Items.Add(entry with { Id = Guid.NewGuid() });
        Reject(() => store.Save(doc), "reject duplicate");
        Check(store.Snapshot().Items.Count == 1 && changed == 0, "failed save preserves memory");
        doc.Items.RemoveAt(1); doc.Items[0] = entry with { Title = "Edited" }; store.Save(doc);
        Reject(() => otherWindow.Save(otherWindowSnapshot), "another store cannot overwrite a newer save");
        Reject(() => store.Save(stale), "stale snapshot cannot overwrite a newer save");
        Check(new BookmarkStore(root, lease).Snapshot().Items.Single().Title == "Edited", "stale write preserves current file");
        Check(changed == 1 && File.Exists(Path.Combine(root, "bookmarks.json.bak")), "save publishes backup");
        var export = Path.Combine(root, "export.json"); store.Export(export);
        Check(store.ReadImport(export).Items.Single().Title == "Edited", "JSON interchange round trip");
        Reject(() => store.Export(export), "export never replaces existing file");
        Reject(() => store.Export(Path.Combine(Path.GetTempPath(), "outside.json")), "export confined to profile");
        lease.Dispose();
        Reject(() => store.Save(store.Snapshot()), "released lease rejects save");
        if (OperatingSystem.IsWindows()) Reject(() => BookmarkDirectoryObservation.Open(root, lease), "released lease rejects directory observation");
        using var lease2 = new ProfileLease(root);
        File.WriteAllText(Path.Combine(root, "bookmarks.json"), "{bad");
        Reject(() => _ = new BookmarkStore(root, lease2), "corrupt file blocks load");
        Check(Directory.GetFiles(root, "bookmarks.json.*.corrupt").Length == 1, "corrupt original preserved");
        File.Delete(Path.Combine(root, "bookmarks.json.bak"));
        Reject(() => _ = new BookmarkStore(root, lease2), "quarantined data prevents silent empty restart");
        Console.WriteLine($"PASS: {count} bookmark store checks");
    }
}
