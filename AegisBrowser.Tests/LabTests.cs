using AegisBrowser.Lab;
using AegisBrowser.Core;
using System.Net;

internal static class LabTests
{
    internal static async Task Run()
    {
        int count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); count++; }
        await using var server = await LabServer.StartAsync();
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, CookieContainer = new() };
        using var client = new HttpClient(handler) { BaseAddress = new Uri(server.Origin), Timeout = TimeSpan.FromSeconds(10) };
        async Task<int> Get(string path) { using var r = await client.GetAsync(path); return (int)r.StatusCode; }
        async Task<int> Post(string path) { using var r = await client.PostAsync(path, null); return (int)r.StatusCode; }
        Check(new Uri(server.Origin).Host == "127.0.0.1", "server loopback binding");
        Check(!server.Contains("http://example.com/") && !server.Contains("http://127.0.0.1:1/"), "lab endpoint containment");
        Check(await Get("") == 200, "HTML fixture");
        Check(await Get("redirect") == 302 && await Get("redirect-final") == 302, "redirect chain");
        Check(await Get("protected") == 403 && await Post("security") == 401, "unauthenticated access denied");
        Check(await Post("login") == 200, "mock login");
        foreach (var decision in Enum.GetValues<SecurityDecision>())
        {
            server.Security.Decision = decision;
            Check(await Post("security") == (decision == SecurityDecision.Allow ? 200 : decision == SecurityDecision.Unavailable ? 503 : 403), "decision response " + decision);
            Check(await Get("protected") == (decision == SecurityDecision.Allow ? 200 : 403), "enforced decision " + decision);
        }
        server.Security.Decision = SecurityDecision.Allow;
        Check(await Get("protected") == 403, "failed check never restores prior grant");
        Check(await Get("unavailable") == 503, "controlled failure");
        Check(await Post("security") == 200 && await Get("protected") == 200, "allow establishes current session grant");
        Check(await Post("reset") == 204 && await Get("protected") == 403, "HTTP reset revokes access");
        using (var forged = new HttpRequestMessage(HttpMethod.Post, "login"))
        {
            forged.Headers.Add("Origin", "https://untrusted.invalid");
            using var denied = await client.SendAsync(forged);
            Check(denied.StatusCode == HttpStatusCode.Forbidden, "foreign-origin mutation denied");
        }
        using (var forged = new HttpRequestMessage(HttpMethod.Get, "protected"))
        {
            forged.Headers.Host = "rebound.invalid";
            using var denied = await client.SendAsync(forged);
            Check(denied.StatusCode == HttpStatusCode.Forbidden, "foreign Host denied");
        }
        using var other = new HttpClient(new HttpClientHandler { UseProxy = false });
        Check((int)(await other.GetAsync(server.Origin + "protected")).StatusCode == 403, "session isolation");
        try { server.Security.Decision = (SecurityDecision)999; throw new Exception("unknown decision accepted"); }
        catch (ArgumentOutOfRangeException) { count++; }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await server.Security.EvaluateAsync(cancel.Token); throw new Exception("canceled check succeeded"); }
        catch (OperationCanceledException) { count++; }
        var report = new LabReport();
        report.Add(LabEventKind.Request, "https://user:password@private.example/secret?token=abc#secret", 200, "password=secret");
        var json = report.Export(LabProfile.Baseline);
        Check(!json.Contains("password") && !json.Contains("token") && !json.Contains("private.example") && !json.Contains("secret"), "report allowlist redaction");
        for (int i=0;i<1100;i++) report.Add(LabEventKind.Request, server.Origin, 0, "Allowed");
        Check(report.Snapshot().Length == 1000, "bounded report memory");
        var paths = Enum.GetValues<LabProfile>().Select(p => LabProfiles.PathFor("root", p)).ToArray();
        Check(paths.Distinct().Count() == 4 && paths.All(p => p.Contains("LabProfiles")), "separate lab profile roots");
        Check(LabProfiles.Configuration(LabProfile.StrictPrivacy).Mode == PrivacyMode.Strict && LabProfiles.Configuration(LabProfile.CustomRules, "||localhost^").UserRules == "||localhost^", "lab privacy profiles");
        Console.WriteLine($"PASS: {count} authorized lab checks");
    }
}
