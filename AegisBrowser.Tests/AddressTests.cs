using AegisBrowser.Core;

internal static class AddressTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); count++; }
        foreach (var query in new[] { "news", "best laptops 2026", "日本語 café", "a+b & c%20", "what is C#?" })
        {
            var result = AddressInputResolver.Resolve(query);
            Check(result.Kind == AddressInputKind.Search && result.Url == "https://duckduckgo.com/?q=" + Uri.EscapeDataString(query), "search " + query);
        }
        foreach (var (input, expected) in new[] {
            ("github.com", "https://github.com/"), ("https://example.com", "https://example.com/"),
            (" HTTP://example.com/a%20b?q=x%2By#part ", "http://example.com/a%20b?q=x%2By#part"),
            ("example.com/a b", "https://example.com/a%20b"),
            ("localhost:8080", "http://localhost:8080/"), ("localhost.:8080", "http://localhost.:8080/"),
            ("app.localhost:8080", "http://app.localhost:8080/"), ("[2001:db8::1]:8443", "https://[2001:db8::1]:8443/"), ("127.0.0.1:8080", "http://127.0.0.1:8080/"),
            ("192.168.1.1", "https://192.168.1.1/"), ("[::1]:8080", "http://[::1]:8080/"),
            ("::1", "http://[::1]/"), ("https://[2001:db8::1]:8443/a", "https://[2001:db8::1]:8443/a"),
            ("bücher.de", "https://xn--bcher-kva.de/"), ("aegis:diagnostics", BrowserPolicy.DiagnosticsUrl),
            ("about:blank", "about:blank"), ("http://intranet", "http://intranet/") })
            Check(AddressInputResolver.Resolve(input) == new AddressResolution(AddressInputKind.Navigation, expected), "navigate " + input);
        foreach (var input in new[] { "javascript:alert(1)", "data:text/html,x", "file:///c:/x", "ftp://example.com", "mailto:a@b.com", "about:config", "chrome://settings", "https://user:pass@example.com", "https://", "https://exam ple.com", "localhost:99999", "news\nother", "C:\\secret", "//example.com" })
            Check(AddressInputResolver.Resolve(input).Kind == AddressInputKind.Rejected, "reject " + input);
        foreach (var input in new string?[] { null, "", "  " }) Check(AddressInputResolver.Resolve(input).Kind == AddressInputKind.Empty, "empty input");
        foreach (var engine in SearchEngines.All)
        {
            var url = AddressInputResolver.Resolve("news", engine.Id).Url!;
            Check(url == engine.Template.Replace("{searchTerms}", "news") && new Uri(url).Scheme == "https", engine.Name);
        }
        var root = Path.Combine(Path.GetTempPath(), "aegis-search-" + Guid.NewGuid().ToString("N"));
        try
        {
            var a = Path.Combine(root, "a"); var b = Path.Combine(root, "b");
            var store = new SearchSettingsStore(a);
            Check(store.Load().EngineId == "duckduckgo", "default engine");
            store.Save(new("brave"));
            Check(new SearchSettingsStore(a).Load().EngineId == "brave", "reload selected engine");
            Check(new SearchSettingsStore(b).Load().EngineId == "duckduckgo", "profile separation");
            File.WriteAllText(Path.Combine(a, "search.json"), "broken");
            var bad = new SearchSettingsStore(a);
            try { bad.Load(); throw new Exception("accepted corrupt settings"); } catch (IOException) { count++; }
            try { bad.Save(new()); throw new Exception("overwrote corrupt settings"); } catch (IOException) { count++; }
            Check(File.ReadAllText(Path.Combine(a, "search.json")) == "broken", "preserve corrupt file");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"PASS: {count} address and search checks");
    }
}
