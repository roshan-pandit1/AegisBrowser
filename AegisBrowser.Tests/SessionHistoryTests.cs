using AegisBrowser.Core;

internal static class SessionHistoryTests
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool condition, string description) { if (!condition) throw new Exception("FAIL: " + description); count++; }
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var history = new SessionHistoryService(() => time, 2);
        var notifications = 0;
        history.Changed += (_, _) => { _ = history.Snapshot(); notifications++; };
        var tab = Guid.NewGuid();
        var first = history.BeginNavigation(tab);
        Check(history.CompleteNavigation(tab, first, "https://user:secret@Example.COM:443/a?token=secret#fragment", 200), "record a successful navigation");
        var entry = history.Snapshot().Single();
        Check(entry.Url == "https://example.com/a" && entry.Title == "example.com", "strip credentials, query and fragment");
        Check(entry.VisitedUtc == time && entry.TabId == tab, "clock and tab identity retained");
        Check(!history.CompleteNavigation(tab, first, "https://example.com/a", 200), "duplicate completion ignored");
        Check(history.UpdateTitle(tab, first, "  A\u202eB\u0001 "), "title update accepted");
        Check(history.Snapshot().Single().Title == "AB", "title controls and bidi removed");
        Check(history.Snapshot("ab").Count == 1 && history.Snapshot("EXAMPLE.COM").Count == 1 && history.Snapshot("missing").Count == 0, "bounded case-insensitive search");
        var second = history.BeginNavigation(tab);
        Check(!history.UpdateTitle(tab, first, "late"), "old navigation cannot change title");
        Check(!history.CompleteNavigation(tab, first, "https://example.com/old", 200), "old navigation cannot complete");
        Check(!history.CompleteNavigation(tab, second, "https://example.com/error", 404), "HTTP error excluded");
        Check(!history.CompleteNavigation(tab, second, "https://example.com/failure", -1), "failed load excluded");
        time = time.AddMinutes(1);
        var secondGood = history.BeginNavigation(tab);
        Check(history.CompleteNavigation(tab, secondGood, "https://example.com/b", 200), "second visit recorded");
        var reload = history.BeginNavigation(tab, reload: true);
        Check(!history.CompleteNavigation(tab, reload, "https://example.com/b", 200) && history.Snapshot().Count == 2, "reload does not record or update time");
        var delayedKind = history.BeginNavigation(tab);
        Check(history.SetReload(tab, delayedKind, true) && !history.CompleteNavigation(tab, delayedKind, "https://example.com/b", 200), "transition callback can mark a pending reload");
        var failed = history.BeginNavigation(tab);
        history.FailNavigation(tab, failed);
        Check(!history.CompleteNavigation(tab, failed, "https://example.com/failure", 200), "failed navigation cannot later record a visit");
        time = time.AddMinutes(1);
        var third = history.BeginNavigation(tab);
        Check(history.CompleteNavigation(tab, third, "http://example.net/c", 302), "third visit recorded");
        Check(history.Snapshot().Count == 2 && history.Snapshot().Last().Url.EndsWith("/b", StringComparison.Ordinal), "oldest entry evicted at limit");
        history.SetPaused(true);
        Check(!history.UpdateTitle(tab, third, "paused title"), "paused history does not update titles");
        var duringPause = history.BeginNavigation(tab);
        history.SetPaused(false);
        Check(!history.CompleteNavigation(tab, duringPause, "https://example.com/paused", 200), "resuming cannot backfill a paused navigation");
        var crossingPause = history.BeginNavigation(tab);
        history.SetPaused(true); history.SetPaused(false);
        Check(!history.CompleteNavigation(tab, crossingPause, "https://example.com/crossing", 200), "pause invalidates in-flight navigation");
        var newest = history.Snapshot().First().Id;
        Check(history.Remove([newest]) == 1 && history.Snapshot().Count == 1, "remove by identity");
        history.CloseTab(tab);
        Check(history.Snapshot().Count == 1, "tab close preserves existing history");
        history.Clear();
        Check(history.Snapshot().Count == 0 && notifications >= 8, "clear and change notifications");

        foreach (var value in new[] { "about:blank", "file:///x", "javascript:alert(1)", "data:text/plain,x", "blob:https://example.com/x", "chrome://settings", "view-source:https://example.com", "https://aegis-diagnostics.invalid/", "https://example.com/\n" })
            Check(!SessionHistoryService.TrySanitizeUrl(value, out _), "unsafe scheme or address rejected");
        var prefix = "https://example.com/";
        Check(SessionHistoryService.TrySanitizeUrl(prefix + new string('x', SessionHistoryService.MaxUrlLength - prefix.Length), out _), "maximum URL length accepted");
        Check(!SessionHistoryService.TrySanitizeUrl(prefix + new string('x', SessionHistoryService.MaxUrlLength - prefix.Length + 1), out _), "oversized URL rejected");
        Check(SessionHistoryService.TrySanitizeUrl("HTTPS://例え.テスト/path?q=1#x", out var idn) && !idn.Contains('?') && !idn.Contains('#'), "IDN address sanitized");
        Check(SessionHistoryService.SanitizeTitle(new string('a', 256)).Length == 256, "maximum title length accepted");
        Check(SessionHistoryService.SanitizeTitle(new string('a', 257)).Length == 256, "long title bounded");
        Check(SessionHistoryService.SanitizeTitle(new string('a', 255) + "😀").Length == 255, "title truncation preserves surrogate pairs");
        Check(SessionHistoryService.SanitizeTitle(new string('\u0001', 2048) + "tail") == "", "title processing has a bounded input scan");
        Check(SessionHistoryService.SanitizeTitle("<b>text</b>") == "<b>text</b>", "markup-like title remains plain text");

        var concurrent = new SessionHistoryService(entryLimit: 1000);
        Parallel.For(0, 1200, _ =>
        {
            var owner = Guid.NewGuid();
            var navigation = concurrent.BeginNavigation(owner);
            if (!concurrent.CompleteNavigation(owner, navigation, "https://example.org/fixture", 200)) throw new Exception("Concurrent visit was lost.");
            concurrent.CloseTab(owner);
        });
        Check(concurrent.Snapshot().Count == 1000, "concurrent writers respect the entry limit");
        Console.WriteLine($"PASS: {count} session history checks");
    }
}
