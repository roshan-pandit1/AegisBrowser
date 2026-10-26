using AegisBrowser.Core;

internal static class NavigationDiagnosticsTests
{
    internal static void Run()
    {
        int count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL diagnostics: " + name); count++; }
        const string token = "syntheticParticipantSecret";
        var url = "https://user:password@example.test/survey/" + token + "?token=" + token + "#" + token;
        Check(DiagnosticUrl.Sanitize(url) == "https://example.test/survey/[redacted]", "userinfo/query/fragment/participant path removed");
        Check(!DiagnosticUrl.Sanitize("https://example.test/survey/%73yntheticParticipantSecret").Contains(token), "encoded participant path removed");
        Check(DiagnosticUrl.Sanitize("javascript:alert(1)") == "(redacted)", "unsupported scheme redacted");
        Check(DiagnosticUrl.Sanitize("https://example.test/\r\nsecret") == "(redacted)", "control characters redacted");
        Check(!DiagnosticUrl.Sanitize("http://192.168.1.10/survey").Contains("192.168"), "IP omitted");
        Check(!DiagnosticUrl.Sanitize("http://[2001:db8::1234]/survey").Contains("2001"), "IPv6 omitted");
        Check(!DiagnosticUrl.Sanitize("https://" + token + ".example.test/survey").Contains(token, StringComparison.OrdinalIgnoreCase), "opaque subdomain redacted");
        var diagnostics = new NavigationDiagnostics();
        diagnostics.Record(DiagnosticKind.Navigation, url);
        Check(diagnostics.Snapshot().Length == 0, "disabled by default");
        bool invoked = false;
        diagnostics.Observe(_ => invoked = true);
        Check(!invoked, "disabled collection does not evaluate diagnostic arguments");
        diagnostics.Enabled = true;
        diagnostics.Observe(_ => throw new IOException("secret URL https://secret.test/?token=secret"));
        Check(diagnostics.Snapshot().Length == 0, "diagnostic failure swallowed without exception text retention");
        var blocked = new FilterDecision(true, true, "||example.test/" + token);
        diagnostics.Record(DiagnosticKind.FilterBlocked, url, rule: NavigationDiagnostics.Category(blocked));
        diagnostics.Record(DiagnosticKind.Redirect, url, redirectTo: "https://other.test/status?Authorization=BearerSecret&Cookie=cookieSecret");
        var report = diagnostics.Export();
        Check(!report.Contains(token) && !report.Contains("password") && !report.Contains("BearerSecret") && !report.Contains("cookieSecret"), "report excludes synthetic credentials and rule literal");
        Check(diagnostics.Snapshot()[0].Rule == DiagnosticRule.ListRule, "blocked rule category retained");
        for (int i = 0; i < 1000; i++) diagnostics.Record(DiagnosticKind.ResourceComplete, url + new string('x', 10000));
        Check(diagnostics.Snapshot().Length == NavigationDiagnostics.Capacity, "bounded entry count");
        Check(diagnostics.Snapshot().All(e => e.Location.Length <= 512 && e.RedirectTo.Length <= 512), "bounded strings");
        diagnostics.Enabled = false; diagnostics.Record(DiagnosticKind.LoadError, url);
        Check(diagnostics.Snapshot().Length == 0, "disable clears history and rejects subsequent events");
        diagnostics.Enabled = true; diagnostics.Record(DiagnosticKind.ResourceComplete, null);
        Check(diagnostics.Snapshot().Single().Location == "(redacted)", "malformed diagnostic input nonthrowing");
        diagnostics.Clear(); Check(diagnostics.Snapshot().Length == 0, "explicit clear");
        const string testToken = "TEST_TOKEN_123";
        diagnostics.Enabled = true;
        Parallel.For(0, 1000, _ => diagnostics.Record(DiagnosticKind.Redirect,
            "https://example.test/survey/" + testToken + "?cookie=" + testToken,
            redirectTo: "https://example.test/auth/" + testToken + "#" + testToken));
        var frozen = diagnostics.StopForExport();
        Check(!diagnostics.Enabled && diagnostics.Snapshot().Length == 0, "stop-export atomically stops and clears live collection");
        diagnostics.Record(DiagnosticKind.ConsoleError, "https://example.test/" + testToken);
        var frozenJson = frozen.ToJson();
        Check(!frozenJson.Contains(testToken) && !frozenJson.Contains("cookie="), "synthetic redirect and participant secrets excluded");
        using (var parsed = System.Text.Json.JsonDocument.Parse(frozenJson))
            Check(parsed.RootElement.GetProperty("Entries").GetArrayLength() == 500, "export retains bounded snapshot after stop");
        Check(System.Text.Encoding.UTF8.GetByteCount(frozenJson) <= NavigationDiagnosticReport.MaximumJsonBytes, "serialized report bounded in bytes");
        bool oversizedRejected = false;
        var oversized = new NavigationDiagnosticReport([new(1, DateTimeOffset.UtcNow, DiagnosticKind.Navigation,
            0, 0, new string('x', NavigationDiagnosticReport.MaximumJsonBytes), "", 0, 0, 0, DiagnosticRule.None, 0)]);
        try { oversized.ToJson(); } catch (InvalidOperationException) { oversizedRejected = true; }
        Check(oversizedRejected, "export byte ceiling is enforced even for an invalid internal snapshot");
        var exportPath = Path.GetTempFileName();
        try
        {
            Check(frozen.TrySaveAsync(exportPath).GetAwaiter().GetResult(), "asynchronous local export succeeds");
            Check(File.ReadAllText(exportPath) == frozenJson, "export contains exactly reviewed snapshot");
            Check(!frozen.TrySaveAsync(Path.Combine(exportPath, "invalid-child.json")).GetAwaiter().GetResult(), "filesystem export failure returns safely");
            Check(diagnostics.Snapshot().Length == 0, "failed write never restarts collection");
        }
        finally { File.Delete(exportPath); }
        var filter = new RequestFilter(new NetworkFilterList("||example.test^\n@@|https://example.test/scripts/allowed.js|$script"));
        var request = new NetworkRequest("https://example.test/scripts/allowed.js", "https://site.test", "script");
        Check(!filter.Evaluate(request).Blocked, "existing explicit exact URL/type exception permits only matching request");
        Check(filter.Evaluate(request with { Url = "http://example.test/scripts/allowed.js" }).Blocked, "scheme not broadened");
        Check(filter.Evaluate(request with { Url = "https://example.test:444/scripts/allowed.js" }).Blocked, "port not broadened");
        Check(filter.Evaluate(request with { Url = "https://sub.example.test/scripts/allowed.js" }).Blocked, "subdomain not broadened");
        Check(filter.Evaluate(request with { Type = "image" }).Blocked, "resource rule not broadened");
        Check(!SiteClassifier.DomainMatches("unrelated-example.test", "example.test"), "unrelated domain not matched");
        var redirectFilter = new RequestFilter(new NetworkFilterList("||other.test^\n@@|https://example.test/scripts/allowed.js|$script"));
        Check(redirectFilter.Evaluate(request with { Url = "https://other.test/scripts/allowed.js" }).Blocked, "redirect destination evaluated independently");
        Check(!filter.Evaluate(request with { MainFrame = true, Url = "https://example.test/status?Security=1" }).Blocked, "provider result main document preserved");
        Console.WriteLine($"PASS: {count} navigation diagnostics checks");
    }
}
