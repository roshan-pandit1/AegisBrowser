using AegisBrowser.Core;

var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    count++;
}
void Reject(string[] args)
{
    try { BrowserPolicy.ReadProfile(args); }
    catch (ArgumentException) { count++; return; }
    throw new Exception("Accepted invalid profile arguments: " + string.Join(' ', args));
}
Check(BrowserPolicy.ReadProfile([]) == "default", "default profile");
Check(BrowserPolicy.ReadProfile(["--profile=Personal"]) == "personal", "case normalization");
foreach (var name in new[] { "", "../escape", "a/b", "a\\b", "a:b", "a b", "a.b", "é", new string('a', 41) })
    Reject(["--profile=" + name]);
Reject(["--no-sandbox"]);
Reject(["--profile=a", "--profile=b"]);
Check(BrowserPolicy.NormalizeAddress(" example.com/path ") == "https://example.com/path", "default HTTPS");
Check(BrowserPolicy.NormalizeAddress("localhost:8080") == "https://localhost:8080/", "local port");
Check(BrowserPolicy.NormalizeAddress("http://localhost:8080/") == "http://localhost:8080/", "explicit HTTP");
Check(BrowserPolicy.NormalizeAddress("aegis:diagnostics") == BrowserPolicy.DiagnosticsUrl, "diagnostics alias");
foreach (var url in new[] { "", "javascript:alert(1)", "file:///C:/secret", "data:text/html,test", "about:config", "https://user:pass@example.com", "https://", "two words", "ftp://example.com" })
    Check(BrowserPolicy.NormalizeAddress(url) is null, "blocked address: " + url);
foreach (var url in new[] { "file:///C:/secret", "javascript:alert(1)", "https://user:pass@example.com", "data:text/html,test" })
    Check(!BrowserPolicy.IsWebUrl(url), "navigation policy: " + url);
var root = Path.Combine(Path.GetTempPath(), "aegis-tests-" + Guid.NewGuid());
try
{
    var a = BrowserPolicy.ProfilePath(root, "Personal");
    var b = BrowserPolicy.ProfilePath(root, "Research");
    Check(a != b, "distinct profile paths");
    Check(a == BrowserPolicy.ProfilePath(root, "PERSONAL"), "same profile case");
    Check(Path.GetFileName(BrowserPolicy.ProfilePath(root, "CON")) == "profile-con", "Windows device name safety");
    using (var first = new ProfileLease(a))
    {
        using var other = new ProfileLease(b);
        var rejected = false;
        try { using var duplicate = new ProfileLease(a); }
        catch (IOException) { rejected = true; }
        Check(rejected, "exclusive profile lease");
        File.WriteAllText(Path.Combine(a, "sentinel"), "persisted");
        Check(!File.Exists(Path.Combine(b, "sentinel")), "storage isolation");
    }
    using var reopened = new ProfileLease(a);
    Check(File.ReadAllText(Path.Combine(a, "sentinel")) == "persisted", "persistent profile data and released lease");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
Console.WriteLine($"PASS: {count} policy and profile checks");
