using AegisBrowser.Core;

internal static class DownloadFileNameTests
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception("FAIL: " + message);
            count++;
        }
        foreach (var bad in new[] { "", ".", "..", "../secret.txt", "folder/../secret.txt", "folder\\..\\secret.txt",
            "secret.txt:stream", "file.", "file ", "CON", "con.txt", "PRN.log", "aux", "nul.bin", "COM1.txt", "com9",
            "LPT1", "lpt9.ext", "COM\u00b9.txt", "CONIN$", "CONOUT$.txt", "bad?.txt", "bad\"name", "bad\nname", new string('x', 181), "folder/" })
            Check(!DownloadFileNames.TrySanitize(bad, out _), "reject unsafe suggestion");
        Check(!DownloadFileNames.TrySanitize(null, out _), "reject missing suggestion");
        Check(DownloadFileNames.TrySanitize("folder/sub/report.txt", out var pathName) && pathName == "report.txt", "strip folder components");
        Check(DownloadFileNames.TrySanitize("report\u202Etxt.exe", out var bidi) && bidi == "reporttxt.exe", "remove bidirectional override");
        Check(DownloadFileNames.TrySanitize("日本語 café.txt", out var unicode) && unicode == "日本語 café.txt", "preserve ordinary Unicode");
        Check(DownloadFileNames.TrySanitize("COM10.txt", out _) && DownloadFileNames.TrySanitize(new string('x', 180), out _), "allow valid boundaries");
        var root = Path.Combine(Path.GetTempPath(), "aegis-download-policy-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "approved");
        Directory.CreateDirectory(folder);
        try
        {
            var destination = Path.Combine(folder, "report.txt");
            Check(DownloadDestinations.IsAvailable(folder, destination), "new file within approved directory");
            Check(!DownloadDestinations.IsAvailable(folder, Path.Combine(root, "outside.txt")), "reject another directory");
            Check(!DownloadDestinations.IsAvailable(folder, Path.Combine(folder, "..", "outside.txt")), "reject resolved traversal");
            Check(!DownloadDestinations.IsAvailable(folder, Path.Combine(folder, "CON.txt")), "reject reserved destination");
            Check(!DownloadDestinations.IsAvailable(folder, Path.Combine(folder, "bad.txt:stream")), "reject alternate stream");
            File.WriteAllText(destination, "sentinel");
            Check(!DownloadDestinations.IsAvailable(folder, destination), "reject existing file");
            Check(File.ReadAllText(destination) == "sentinel", "validation leaves existing file untouched");
            Check(!DownloadDestinations.IsAvailable(folder, Path.Combine(folder, new string('a', 181))), "reject overlong file name");
            var link = Path.Combine(root, "link");
            var linkCreated = false;
            try { Directory.CreateSymbolicLink(link, folder); linkCreated = true; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            { Console.WriteLine("SKIP: download destination reparse fixture unavailable"); }
            if (linkCreated)
            {
                Check(!DownloadDestinations.IsAvailable(link, Path.Combine(link, "new.txt")), "reject reparse directory");
                Directory.Delete(link);
            }
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS: {count} download filename checks");
    }
}
