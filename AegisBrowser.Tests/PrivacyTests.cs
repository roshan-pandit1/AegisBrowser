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
        Console.WriteLine($"PASS: {count} privacy checks");
    }
}
