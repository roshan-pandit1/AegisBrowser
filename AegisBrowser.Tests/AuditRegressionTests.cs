using AegisBrowser.Core;
using AegisBrowser.Lab;

internal static class AuditRegressionTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    internal static void Run()
    {
        int count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL audit: " + name); count++; }
        var url = BrowserPolicy.DiagnosticsUrl;
        Check(BrowserPolicy.CanServeDiagnostics(url, "GET", true, true), "top-level diagnostics accepted");
        foreach (var diagnosticRequest in new[] {
            (url, "POST", true, true), (url, "GET", false, true), (url, "GET", true, false),
            (url + "other", "GET", true, true), (url + "?token=secret", "GET", true, true),
            ("https://aegis-diagnostics.invalid:444/", "GET", true, true),
            ("http://aegis-diagnostics.invalid/", "GET", true, true),
            ("https://aegis-diagnostics.invalid.evil.test/", "GET", true, true) })
            Check(!BrowserPolicy.CanServeDiagnostics(diagnosticRequest.Item1, diagnosticRequest.Item2, diagnosticRequest.Item3, diagnosticRequest.Item4), "diagnostics boundary");
        Check(!BrowserPolicy.IsWebUrl("https://example.com/\nsecret"), "control characters rejected");
        Check(!BrowserPolicy.IsWebUrl(@"https://example.com\secret"), "backslash canonicalization refused");
        Check(BrowserPolicy.CanPromptPermission("https://bücher.de/a", "https://xn--bcher-kva.de/b"), "IDN permission origins canonicalized");
        Check(!BrowserPolicy.CanPromptPermission("https://example.com", "https://other.test"), "navigation changes permission scope");
        Check(SiteClassifier.Host("https://\ud800.test/") is null, "invalid Unicode does not escape callback classification");
        foreach (var rule in new[] { "||tracker.test^$~~script", "||tracker.test^$third-party,~third-party",
            "||tracker.test^$script,script", "||tracker.test^$domain=~~example.com", "||tracker.test^$domain=", "/(a+)+$/" })
            Check(new NetworkFilterList(rule).RuleCount == 0, "ambiguous or unsupported rule rejected");
        var complex = new NetworkFilterList(string.Concat(Enumerable.Repeat("a*", 600)));
        Check(complex.RuleCount + complex.Unsupported.Count == 1, "complex pattern is handled without an escaping engine exception");
        var request = new NetworkRequest("https://xn--bcher-kva.de/pixel", "https://example.com", "script");
        Check(new RequestFilter(new NetworkFilterList("||bücher.de/pixel")).Evaluate(request).Blocked, "IDN URL pattern normalization");
        bool exhausted = false;
        try
        {
            new NetworkFilterList("/pixel").Match(request, true, false,
                System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency);
        }
        catch (FilterBudgetExceededException) { exhausted = true; }
        Check(exhausted, "shared expired scan budget refuses another rule evaluation");
        var canceledPolicy = new PrivacySession(new() { Mode = PrivacyMode.Strict });
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try { canceledPolicy.ApplyAsync(new(), cancellationToken: cancellation.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
        }
        Check(canceledPolicy.Configuration.Mode == PrivacyMode.Strict, "canceled compilation preserves active privacy policy");
        var classification = ResourceClassification.FromCefName("FutureCefResource");
        Check(classification.Type == "other" && classification.Unknown, "unknown CEF type remains visible");
        Check(!ResourceClassification.FromCefName("Script").Unknown, "known CEF script mapping");
        var session = new PrivacySession(new() { Mode = PrivacyMode.Strict });
        var unknown = session.Evaluate(request with { TopUrl = "about:blank", UnknownResource = true });
        Check(!unknown.Blocked && unknown.Fallback.HasFlag(FilterFallback.UnknownSite), "unknown site fail-open is explicit");
        Check(unknown.Fallback.HasFlag(FilterFallback.UnknownResource), "unknown type coverage flag");
        session.Evaluate(request with { Url = "https://example.com/" + new string('x', 65_537) });
        var snapshot = session.Snapshot();
        Check(snapshot.UnknownSites == 1 && snapshot.UnknownResources == 1 && snapshot.InvalidInputs == 1, "fallback counters aggregate");
        Check(snapshot.Requests.Last().Destination == "(internal / unknown)", "oversized input is not retained");
        session.ClearHistory();
        Check(session.Snapshot().UnknownSites == 0 && session.Snapshot().InvalidInputs == 0, "clear resets coverage counters");
        session.Paused = true;
        Check(session.Evaluate(request).Rule == "Temporarily paused", "paused state explicit");
        bool rejected = false;
        try { _ = new NetworkFilterList(new string('x', 8_000_001)); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "oversized list rejected");
        var importPath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(importPath, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("||tracker.test^")).ToArray());
            var imported = FilterListImport.Read(importPath);
            Check(imported.Text == "||tracker.test^" && imported.Sha256.Length == 64, "bounded UTF-8 import preserves BOM compatibility and reports digest");
            File.WriteAllBytes(importPath, new byte[] { 0xFF, 0xFE });
            bool invalid = false;
            try { FilterListImport.Read(importPath); } catch (System.Text.DecoderFallbackException) { invalid = true; }
            Check(invalid, "invalid UTF-8 import rejected");
            using (var large = new FileStream(importPath, FileMode.Create)) large.SetLength(FilterListImport.MaximumBytes + 1L);
            invalid = false;
            try { FilterListImport.Read(importPath); } catch (IOException) { invalid = true; }
            Check(invalid, "oversized imported file rejected");
        }
        finally { File.Delete(importPath); }
        var clock = new Clock();
        var sessions = new LabSessionRegistry(clock);
        var adapter = new MockSecurityAdapter { Decision = SecurityDecision.Allow };
        var first = sessions.Issue()!; var other = sessions.Issue()!;
        Check(first != other && first.Length == 64, "unpredictable independent session identifiers");
        Check(!sessions.IsAuthorized(first, adapter.Capture()), "new session has no grant");
        Check(sessions.Apply(first, adapter.Version, SecurityDecision.Allow, adapter.Capture(), false), "explicit mock allow grants existing session");
        Check(sessions.IsAuthorized(first, adapter.Capture()) && !sessions.IsAuthorized(other, adapter.Capture()), "authorization isolation");
        clock.Now += LabSessionRegistry.Lifetime;
        Check(!sessions.IsAuthorized(first, adapter.Capture()) && !sessions.Exists(first), "server-side expiration independent of cookies");
        first = sessions.Issue()!;
        sessions.Revoke(first);
        Check(!sessions.Apply(first, adapter.Version, SecurityDecision.Allow, adapter.Capture(), false), "in-flight evaluation cannot resurrect reset session");
        first = sessions.Issue()!;
        Check(!sessions.Apply(first, adapter.Version, SecurityDecision.Allow, adapter.Capture(), true), "canceled evaluation fails closed");
        var old = adapter.Capture(); adapter.Decision = SecurityDecision.Deny; adapter.Decision = SecurityDecision.Allow;
        Check(!sessions.Apply(first, old.Version, SecurityDecision.Allow, adapter.Capture(), false), "decision version invalidates stale grants");
        sessions.Close();
        Check(sessions.Issue() is null && !sessions.IsAuthorized(first, adapter.Capture()), "shutdown prevents new sessions and grants");
        sessions = new LabSessionRegistry(clock);
        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 150, _ => { if (sessions.Issue() is { } id) ids.Add(id); });
        Check(ids.Count == 100 && ids.Distinct().Count() == 100, "concurrent session creation respects capacity");
        clock.Now += LabSessionRegistry.Lifetime;
        Check(sessions.Issue() is not null, "expired capacity is reclaimed");
        Console.WriteLine($"PASS: {count} security audit regression checks");
    }
}
