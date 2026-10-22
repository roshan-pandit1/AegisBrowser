using AegisBrowser.Core;

internal static class PrivacyTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL privacy: " + name); count++; }
        var sites = SiteClassifier.Default;
        Check(sites.IsThirdParty("https://cdn.example.co.uk", "http://www.example.co.uk") == false, "co.uk siblings");
        Check(sites.IsThirdParty("https://alice.github.io", "https://bob.github.io") == true, "private suffix isolation");
        Check(sites.Site("a.b.ck") == "a.b.ck", "PSL wildcard");
        Check(sites.Site("a.www.ck") == "www.ck", "PSL exception");
        Check(sites.IsThirdParty("http://127.0.0.1:80", "http://127.0.0.1:90") == false, "IP ports");
        Check(sites.IsThirdParty("https://xn--bcher-kva.de", "https://bücher.de") == false, "IDN equivalent");
        Check(sites.IsThirdParty("https://a.test", "about:blank") is null, "unknown initiator");
        var list = new NetworkFilterList("! test\n||tracker.test^$third-party\n@@||tracker.test/essential^\n||cdn.test^$script,domain=example.com|~private.example.com\n||unsafe.test^$redirect=noopjs\nexample.com##.ad");
        Check(list.RuleCount == 3 && list.Unsupported.Count == 2, "unsupported options are rejected");
        var filter = new RequestFilter(list, ["blocked.test"], ["safe.blocked.test"]);
        FilterDecision Test(string url, string top = "https://example.com", string type = "script", bool main = false) => filter.Evaluate(new(url, top, type, main));
        Check(Test("https://tracker.test/pixel").Blocked, "third party block");
        Check(!Test("https://tracker.test/pixel", "https://www.tracker.test").Blocked, "first party tracking exception");
        Check(!Test("https://tracker.test.evil.test/pixel").Blocked, "domain boundary");
        Check(!Test("https://tracker.test/essential").Blocked, "ABP exception precedence");
        Check(Test("https://cdn.test/a.js").Blocked, "resource and site option");
        Check(!Test("https://cdn.test/a.js", type: "image").Blocked, "resource exclusion");
        Check(!Test("https://cdn.test/a.js", "https://private.example.com").Blocked, "excluded document domain");
        Check(Test("https://sub.blocked.test").Blocked, "user domain subtree");
        Check(!Test("https://safe.blocked.test").Blocked, "user allow overrides block");
        Check(!Test("https://blocked.test/login", main: true).Blocked, "navigation preserved");
        Check(!Test(BrowserPolicy.DiagnosticsUrl).Blocked, "internal diagnostics");
        Check(!Test("data:text/plain,hello").Blocked, "nonnetwork resources");
        var config = new PrivacyConfiguration();
        var standard = new RequestFilter(config);
        var tracking = new NetworkRequest("https://google-analytics.com/collect", "https://example.com", "ping");
        Check(standard.Evaluate(tracking).Blocked, "Standard starter tracking list");
        var cdn = new NetworkRequest("https://cdn.test/app.js", "https://example.com", "script");
        Check(!standard.Evaluate(cdn).Blocked, "Standard permits unknown third parties");
        var strict = new RequestFilter(config with { Mode = PrivacyMode.Strict });
        Check(strict.Evaluate(cdn).Blocked, "Strict blocks third parties");
        Check(!strict.Evaluate(cdn with { Url = "https://cdn.example.com/a.js" }).Blocked, "Strict permits first party");
        Check(!strict.Evaluate(cdn with { MainFrame = true }).Blocked, "Strict permits top-level OAuth navigation");
        Check(!strict.Evaluate(cdn with { TopUrl = "about:blank" }).Blocked, "Strict unknown site fail open");
        var excepted = new RequestFilter(config with { Mode = PrivacyMode.Strict, SiteExceptions = ["example.com"] });
        Check(!excepted.Evaluate(cdn).Blocked, "site exception overrides Strict");
        Check(excepted.Evaluate(cdn with { TopUrl = "https://example.com.evil.test" }).Blocked, "site exception hostname boundary");
        Check(!new RequestFilter(config with { Mode = PrivacyMode.Strict, AllowedDomains = ["cdn.test"] }).Evaluate(cdn).Blocked, "Strict explicit resource exception");
        Check(!new RequestFilter(config with { Mode = PrivacyMode.Custom }).Evaluate(tracking).Blocked, "Custom list opt-in");
        Check(new RequestFilter(config with { Mode = PrivacyMode.Custom, UseListsInCustom = true }).Evaluate(tracking).Blocked, "Custom enabled list");
        Check(new RequestFilter(config with { Mode = PrivacyMode.Custom, UserRules = "||cdn.test^" }).Evaluate(cdn).Blocked, "Custom user rules");
        var session = new PrivacySession(config with { Mode = PrivacyMode.Strict });
        Check(session.Evaluate(cdn).Blocked, "session policy wired");
        session.Paused = true;
        Check(!session.Evaluate(cdn).Blocked, "temporary pause");
        session.Paused = false;
        Check(session.Evaluate(cdn).Blocked, "resume filtering");
        for (var i = 0; i < 600; i++) session.Evaluate(cdn with { Url = "https://cdn.test/secret?token=secret" });
        var snapshot = session.Snapshot();
        Check(snapshot.Blocked == 602 && snapshot.Allowed == 1 && snapshot.Requests.Length == 500, "bounded history and counters");
        Check(snapshot.Requests.All(r => !r.Destination.Contains("secret")), "inspector does not retain URL secrets");
        session.ClearHistory();
        Check(session.Snapshot().Allowed == 0 && session.Snapshot().Blocked == 0 && session.Snapshot().Requests.Length == 0, "clear inspector");
        session.Apply(config with { AllowedDomains = ["cdn.test"], Mode = PrivacyMode.Strict });
        Check(!session.Evaluate(cdn).Blocked, "live policy replacement");
        Check(sites.IsThirdParty("http://[::1]:80", "http://[0:0:0:0:0:0:0:1]:90") == false, "IPv6 normalization");
        session.Evaluate(cdn with { Url = "https://user:password@cdn.test/path?secret=1" });
        Check(!session.Snapshot().Requests.Last().Destination.Contains("password"), "inspector strips credentials");
        var folder = Path.Combine(Path.GetTempPath(), "aegis-privacy-" + Guid.NewGuid());
        try
        {
            var store = new PrivacySettingsStore(folder);
            Check(store.Load().Mode == PrivacyMode.Standard, "missing settings defaults");
            var saved = config with { Mode = PrivacyMode.Strict, SiteExceptions = ["example.com"], UserRules = "||tracker.test^" };
            store.Save(saved);
            var restored = new PrivacySettingsStore(folder).Load();
            Check(restored.Mode == PrivacyMode.Strict && restored.SiteExceptions.SequenceEqual(saved.SiteExceptions)
                && restored.UserRules == saved.UserRules, "settings round trip");
            Check(new PrivacySettingsStore(Path.Combine(folder, "other")).Load().SiteExceptions.Length == 0, "profile settings isolation");
            var fresh = new PrivacySession(restored);
            Check(!fresh.Paused && fresh.Snapshot().Requests.Length == 0, "pause and inspector never persist");
            var copy = fresh.Configuration; copy.SiteExceptions[0] = "evil.test";
            Check(fresh.Configuration.SiteExceptions[0] == "example.com", "configuration snapshots isolated");
            bool rejected = false;
            try { fresh.Apply(config, _ => throw new IOException("simulated disk failure")); } catch (IOException) { rejected = true; }
            Check(rejected && fresh.Configuration.Mode == PrivacyMode.Strict, "failed save preserves active policy");
            File.WriteAllText(Path.Combine(folder, "privacy.json"), "{broken");
            var corrupt = new PrivacySettingsStore(folder);
            rejected = false;
            try { corrupt.Load(); } catch (IOException) { rejected = true; }
            Check(rejected, "malformed settings reported");
            rejected = false;
            try { corrupt.Save(config); } catch (IOException) { rejected = true; }
            Check(rejected && File.ReadAllText(Path.Combine(folder, "privacy.json")) == "{broken", "malformed file preserved");
            File.WriteAllText(Path.Combine(folder, "privacy.json"), "{\"BlockedDomains\":null}");
            rejected = false;
            try { new PrivacySettingsStore(folder).Load(); } catch (IOException) { rejected = true; }
            Check(rejected, "null settings rejected safely");
            Parallel.For(0, 1000, i => { if (i % 50 == 0) fresh.Apply(config); fresh.Evaluate(cdn); });
            Check(fresh.Snapshot().Allowed + fresh.Snapshot().Blocked == 1000, "concurrent settings and request accounting");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        Check(!SiteClassifier.DomainMatches("127.0.0.1", "0.0.1"), "IP hosts are exact matches");
        var optionsOnly = new RequestFilter(new NetworkFilterList("$third-party,image"));
        Check(optionsOnly.Evaluate(cdn with { Type = "image" }).Blocked, "ABP options-only pattern");
        Check(new NetworkFilterList("@@/regex/\n||tracker.test^$unknown").Unsupported.Count == 2, "unsupported exception regex rejected");
        var large = new NetworkFilterList(string.Join('\n', Enumerable.Range(0, 10000).Select(i => "||tracker" + i + ".test^")));
        Check(new RequestFilter(large).Evaluate(cdn with { Url = "https://tracker9999.test/pixel" }).Blocked, "indexed large hostname list");
        Check(new RequestFilter(new NetworkFilterList("/blocked.png")).Evaluate(cdn with { Url = "https://cdn.test/blocked.png" }).Blocked,
            "path rule is not mistaken for regex literal");
        Console.WriteLine($"PASS: {count} privacy checks");
    }
}
