using AegisBrowser.Core;

internal static class ProfileCatalogTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL profile catalog: " + name);
            count++;
        }

        var root = Path.Combine(Path.GetTempPath(), "aegis-profile-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new ProfileCatalog(root);
            Check(catalog.List().Count == 0 && !Directory.Exists(root), "listing does not create storage");
            Check(catalog.Create("Work_1") == "work_1", "name canonicalized");
            var path = BrowserPolicy.ProfilePath(root, "work_1");
            Check(Directory.Exists(path), "existing profile directory format retained");
            Check(catalog.List().SequenceEqual(["work_1"]), "list created profile");
            File.WriteAllText(Path.Combine(path, "sentinel"), "must survive");
            var rejected = false;
            try { catalog.Create("WORK_1"); } catch (IOException) { rejected = true; }
            Check(rejected && File.ReadAllText(Path.Combine(path, "sentinel")) == "must survive",
                "duplicates rejected without destroying existing data");

            foreach (var invalid in new[] { "", "../escape", "a/b", "a.b", "with space", new string('x', 41) })
            {
                rejected = false;
                try { catalog.Create(invalid); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "reject invalid name: " + invalid);
            }
            Directory.CreateDirectory(Path.Combine(root, "profile-invalid.name"));
            Directory.CreateDirectory(Path.Combine(root, ".creating-incomplete"));
            Check(catalog.List().SequenceEqual(["work_1"]), "ignore invalid and temporary directories");
            Check(!Directory.Exists(Path.Combine(root, "escape")), "traversal does not create profile");
            using var lease = new ProfileLease(path);
            Check(File.Exists(Path.Combine(path, "sentinel")), "profile data survives manager enumeration");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Console.WriteLine($"PASS: {count} profile catalog checks");
    }
}
