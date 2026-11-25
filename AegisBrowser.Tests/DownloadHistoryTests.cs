using AegisBrowser.Core;

internal static class DownloadHistoryTests
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); count++; }
        var root = Path.Combine(Path.GetTempPath(), "aegis-download-history-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first"); var second = Path.Combine(root, "second");
        try
        {
            using var leaseA = new ProfileLease(first);
            using var leaseB = new ProfileLease(second);
            var a = new DownloadHistoryStore(first, leaseA);
            var b = new DownloadHistoryStore(second, leaseB);
            Check(a.Load().Count == 0 && b.Load().Count == 0, "new histories are empty");
            var destination = Path.Combine(root, "report.txt");
            File.WriteAllText(destination, "fixture");
            var now = DateTimeOffset.UtcNow;
            var record = new DownloadHistoryEntry("report.txt", "example.test", destination, 7, 7, DownloadFinalStatus.Completed, now, now);
            a.Save([record]);
            Check(new DownloadHistoryStore(first, leaseA).Load().Single() == record, "history survives reload");
            Check(b.Load().Count == 0, "profile histories remain separate");
            var raw = File.ReadAllText(Path.Combine(first, "download-history.json"));
            Check(!raw.Contains("https://") && !raw.Contains("Authorization") && !raw.Contains("Cookie"), "history contains no URL or request headers");
            try { a.Save([record with { Hostname = "user:secret@example.test" }]); throw new Exception("accepted userinfo"); }
            catch (IOException) { count++; }
            Check(a.Load().Single() == record, "invalid save preserves history");
            a.Clear();
            Check(a.Load().Count == 0 && File.ReadAllText(destination) == "fixture", "clear leaves downloaded file");
            var history = Path.Combine(first, "download-history.json");
            File.WriteAllText(history, "not-json");
            try { _ = a.Load(); throw new Exception("accepted corrupt history"); } catch (IOException) { count++; }
            try { a.Save([record]); throw new Exception("overwrote corrupt history"); } catch (IOException) { count++; }
            Check(File.ReadAllText(history) == "not-json", "corrupt history remains intact");
            leaseA.Dispose();
            try { _ = a.Load(); throw new Exception("read without lease"); } catch (IOException) { count++; }
            try { _ = new DownloadHistoryStore(first, leaseB); throw new Exception("accepted wrong lease"); } catch (IOException) { count++; }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"PASS: {count} download history checks");
    }
}
