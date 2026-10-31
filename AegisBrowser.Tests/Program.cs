using AegisBrowser.Core;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;

// A child process proves that the OS lease is exclusive beyond this process.
if (args.Length == 2 && args[0] == "--probe-lock")
{
    try { using var lease = new ProfileLease(args[1]); Environment.ExitCode = 2; }
    catch (IOException) { Environment.ExitCode = 0; }
    return;
}

if (args.Length == 2 && args[0] == "--dashboard-lease-child")
{
    using var lease = new ProfileLease(args[1]);
    Console.WriteLine("READY"); Console.Out.Flush();
    Environment.ExitCode = Console.ReadLine() == "fail" ? 7 : 0;
    return;
}

if (args.Length == 4 && args[0] == "--settings-apply-child")
{
    var repository = new ProfileRepository(args[1]);
    var templates = new ProfileTemplateRepository(args[1]);
    var profile = repository.GetBySlug(args[2]) ?? throw new Exception("Missing test profile.");
    var template = templates.List().Single(t => t.Id == Guid.Parse(args[3]));
    var service = new ProfileSettingsTransactions(args[1], repository, templates, () =>
    { Console.WriteLine("READY"); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite); });
    _ = service.Apply([profile], template, CancellationToken.None);
    return;
}

// Hold an uncommitted write until the parent kills this process to test SQLite recovery.
if (args.Length == 2 && args[0] == "--hold-metadata-transaction")
{
    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = args[1], Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
    connection.Open();
    using var transaction = connection.BeginTransaction();
    using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
        UPDATE profiles SET notes='uncommitted crash write';
        INSERT INTO profiles (id,slug,display_name,notes,metadata_version,revision,created_utc,updated_utc)
        VALUES ('10000000-0000-0000-0000-000000000001','interrupted','interrupted','',1,1,
                '2026-01-01T00:00:00.0000000+00:00','2026-01-01T00:00:00.0000000+00:00');
        """;
    command.ExecuteNonQuery();
    Console.WriteLine("READY");
    Console.Out.Flush();
    Thread.Sleep(Timeout.Infinite);
    return;
}

PrivacyTests.Run();
AddressTests.Run();
ProfileCatalogTests.Run();
ProfileRepositoryTests.Run();
ProfileDashboardTests.Run();
ProfileOperationsTests.Run();
ProfileBulkTests.Run();
ProfileTransferTests.Run();
ProfileBackupTests.Run();
ProfileSettingsTransactionTests.Run();
ProxyTests.Run();
ProxyAuthenticationTests.Run();
ProxyBulkTests.Run();
ProxyPresetTests.Run();
ProxyPresetTransferTests.Run();
ProxyConfigurationTransferTests.Run();
ProxyTransportTests.Run();
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
Reject(["--profile=Personal", "--disable-web-security"]);
Check(BrowserPolicy.ReadProfile(["--profile=" + new string('a', 40)]).Length == 40, "maximum profile length");
Check(BrowserPolicy.NormalizeAddress("HTTPS://EXAMPLE.COM/path?q=1#section") == "https://example.com/path?q=1#section", "URL query and fragment retained");
Check(BrowserPolicy.NormalizeAddress(" example.com/path ") == "https://example.com/path", "default HTTPS");
Check(BrowserPolicy.NormalizeAddress("localhost:8080") == "https://localhost:8080/", "local port");
Check(BrowserPolicy.NormalizeAddress("http://localhost:8080/") == "http://localhost:8080/", "explicit HTTP");
Check(BrowserPolicy.NormalizeAddress("aegis:diagnostics") == BrowserPolicy.DiagnosticsUrl, "diagnostics alias");
foreach (var url in new[] { "", "javascript:alert(1)", "file:///C:/secret", "data:text/html,test", "about:config", "https://user:pass@example.com", "https://", "two words", "ftp://example.com" })
    Check(BrowserPolicy.NormalizeAddress(url) is null, "blocked address: " + url);
foreach (var url in new[] { "file:///C:/secret", "javascript:alert(1)", "https://user:pass@example.com", "data:text/html,test" })
    Check(!BrowserPolicy.IsWebUrl(url), "navigation policy: " + url);
Check(BrowserPolicy.CanPromptPermission("https://example.com/a", "https://example.com/b"), "same secure origin prompt");
Check(BrowserPolicy.CanPromptPermission("https://example.com:443/a", "https://example.com/"), "default port origin equivalence");
Check(BrowserPolicy.CanPromptPermission("http://localhost:8765/a", "http://localhost:8765/"), "loopback prompt");
foreach (var pair in new[] {
    ("http://example.com", "http://example.com"),
    ("https://example.com", "https://example.com.evil.test"),
    ("https://example.com:444", "https://example.com"),
    ("https://example.com", "https://other.test"),
    ("data:text/html,test", "https://example.com"),
    ("https://user@example.com", "https://example.com"),
    (BrowserPolicy.DiagnosticsUrl, BrowserPolicy.DiagnosticsUrl) })
    Check(!BrowserPolicy.CanPromptPermission(pair.Item1, pair.Item2), "permission origin rejected: " + pair.Item1);
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
        var executable = Environment.ProcessPath ?? throw new Exception("Process path unavailable");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--probe-lock");
        start.ArgumentList.Add(a);
        using (var child = Process.Start(start) ?? throw new Exception("Could not start lease probe"))
        {
            if (!child.WaitForExit(15000)) { child.Kill(true); throw new Exception("Lease probe timed out"); }
            Check(child.ExitCode == 0, "cross-process profile lease");
        }
        File.WriteAllText(Path.Combine(a, "sentinel"), "persisted");
        Check(!File.Exists(Path.Combine(b, "sentinel")), "storage isolation");
    }
    using var reopened = new ProfileLease(a);
    Check(File.ReadAllText(Path.Combine(a, "sentinel")) == "persisted", "persistent profile data and released lease");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
Console.WriteLine($"PASS: {count} policy and profile checks");
