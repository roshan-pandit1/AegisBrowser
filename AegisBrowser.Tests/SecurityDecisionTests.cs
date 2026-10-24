using AegisBrowser.SecurityLab;
using System.Net;
using System.Net.Http.Json;

internal static class SecurityDecisionTests
{
    internal static async Task Run()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); count++; }
        await using var server = await SecurityDecisionServer.StartAsync();
        var cookies = new CookieContainer();
        using var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies, UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = server.Origin };
        using var other = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = server.Origin };
        async Task<int> Get(HttpClient c, string path) { using var r = await c.GetAsync(path); return (int)r.StatusCode; }
        async Task<SecurityDecision?> Evaluate() { using var r = await client.PostAsync(SecurityDecisionServer.EvaluatePath, null); Check(r.StatusCode == HttpStatusCode.OK, "evaluation HTTP success"); return await r.Content.ReadFromJsonAsync<SecurityDecision>(); }
        Check(server.Origin.Host == "127.0.0.1" && server.Origin.Port > 0, "automatic loopback binding");
        Check(await Get(client, SecurityDecisionServer.PagePath) == 200, "local fixture page");
        using var badHost = new HttpRequestMessage(HttpMethod.Get, server.PageUrl); badHost.Headers.Host = "foreign.example";
        using var badHostResult = await client.SendAsync(badHost); Check(badHostResult.StatusCode == HttpStatusCode.Forbidden, "foreign Host rejected");
        using var foreign = new HttpRequestMessage(HttpMethod.Post, new Uri(server.Origin, SecurityDecisionServer.EvaluatePath)); foreign.Headers.Add("Origin", "https://foreign.example");
        using var foreignResult = await client.SendAsync(foreign); Check(foreignResult.StatusCode == HttpStatusCode.Forbidden, "cross-origin evaluation rejected");
        Check(await Get(client, SecurityDecisionServer.ProtectedPath) == 403, "unauthenticated denied");
        Check(await Get(client, SecurityDecisionServer.ProtectedPath + "?decision=allow") == 403, "forged Allow denied");
        var first = await Evaluate();
        Check(first?.Provider == "AegisMock", "mock provider");
        Check(first?.Decision == "allow" && first.IsValidAllow, "valid Allow contract");
        Check(await Get(client, SecurityDecisionServer.ProtectedPath) == 200, "evaluated session granted");
        Check(await Get(other, SecurityDecisionServer.ProtectedPath) == 403, "other session denied");
        var second = await Evaluate(); Check(first!.RequestId != second!.RequestId, "unique request IDs");
        var secret = cookies.GetCookies(server.Origin)["aegis-mock-session"]!.Value;
        Check(secret.Length == 64, "unpredictable session identifier size");
        using var reset = await client.PostAsync(SecurityDecisionServer.ResetPath, null);
        Check(reset.StatusCode == HttpStatusCode.NoContent && await Get(client, SecurityDecisionServer.ProtectedPath) == 403, "reset revoked grant");
        using var replay = new HttpRequestMessage(HttpMethod.Get, new Uri(server.Origin, SecurityDecisionServer.ProtectedPath)); replay.Headers.Add("Cookie", "aegis-mock-session=" + secret);
        using var replayResult = await other.SendAsync(replay); Check(replayResult.StatusCode == HttpStatusCode.Forbidden, "reset secret cannot be replayed");
        var report = await new SecurityTestRunner().RunAsync(server);
        Check(report.Passed && report.Assertions.Count == 9 && report.Assertions.All(a => a.Passed), "runner passes only complete workflow");
        Check(report.Started <= report.Ended && report.RequestId is not null, "report timestamps and ID");
        Check(!report.ToJson().Contains(secret) && !report.ToJson().Contains("aegis-mock-session"), "report excludes session secrets");
        Check(server.Evaluations.All(e => e.Outcome == "allow" && e.RequestId is not null), "sanitized evaluation events");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var canceled = await new SecurityTestRunner().RunAsync(server, cancel.Token);
        Check(!canceled.Passed && canceled.Error == "Canceled", "canceled workflow fails");
        var provider = new SwitchableProvider();
        await using var failing = await SecurityDecisionServer.StartAsync(provider);
        using var failingClient = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = failing.Origin };
        using var initial = await failingClient.PostAsync(SecurityDecisionServer.EvaluatePath, null);
        Check(await Get(failingClient, SecurityDecisionServer.ProtectedPath) == 200, "failure fixture initially authorized");
        provider.Fail = true;
        using var failed = await failingClient.PostAsync(SecurityDecisionServer.EvaluatePath, null);
        Check(failed.StatusCode == HttpStatusCode.ServiceUnavailable && await Get(failingClient, SecurityDecisionServer.ProtectedPath) == 403, "server error revokes previous grant");
        var failedReport = await new SecurityTestRunner().RunAsync(failing);
        Check(!failedReport.Passed && failedReport.Assertions.Any(a => !a.Passed), "server failure cannot pass runner");
        Check(!failedReport.ToJson().Contains("secret-provider-token") && failing.Evaluations.All(e => e.Outcome is "allow" or "unavailable"), "exception text redacted");
        await using var invalid = await SecurityDecisionServer.StartAsync(new InvalidProvider());
        var invalidReport = await new SecurityTestRunner().RunAsync(invalid);
        Check(!invalidReport.Passed && invalid.Evaluations.Single().RequestId is null, "invalid provider response cannot authorize or leak ID");
        count += await LifecycleChecks();
        Console.WriteLine($"PASS: {count} mock Allow checks");
    }
    private static async Task<int> LifecycleChecks()
    {
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); count++; }
        static HttpClient Client(SecurityDecisionServer server) => new(new HttpClientHandler {
            UseProxy = false, AllowAutoRedirect = false, CookieContainer = new CookieContainer()
        }) { BaseAddress = server.Origin, Timeout = TimeSpan.FromSeconds(10) };
        static async Task<int> Get(HttpClient client) {
            using var response = await client.GetAsync(SecurityDecisionServer.ProtectedPath);
            return (int)response.StatusCode;
        }
        var clock = new ManualClock();
        await using (var expiring = await SecurityDecisionServer.StartAsync(timeProvider: clock))
        using (var client = Client(expiring))
        {
            using var evaluation = await client.PostAsync(SecurityDecisionServer.EvaluatePath, null);
            Check(evaluation.StatusCode == HttpStatusCode.OK && await Get(client) == 200, "expiration fixture authorized");
            clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromTicks(1));
            Check(await Get(client) == 200, "grant valid immediately before expiry");
            clock.Advance(TimeSpan.FromTicks(1));
            Check(await Get(client) == 403, "grant denied at exact expiration boundary");
        }
        var provider = new GatedProvider();
        await using var server = await SecurityDecisionServer.StartAsync(provider);
        using var session = Client(server);
        async Task Seed() {
            using var response = await session.PostAsync(SecurityDecisionServer.EvaluatePath, null);
            Check(response.StatusCode == HttpStatusCode.OK && await Get(session) == 200, "concurrency fixture authorized");
        }
        await Seed();
        var older = provider.HoldNext();
        var first = session.PostAsync(SecurityDecisionServer.EvaluatePath, null);
        await older.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var newer = provider.HoldNext();
        var second = session.PostAsync(SecurityDecisionServer.EvaluatePath, null);
        await newer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            older.Release.TrySetResult();
            using var stale = await first;
            Check(stale.StatusCode == HttpStatusCode.ServiceUnavailable && await Get(session) == 403,
                "superseded evaluation cannot authorize while newer evaluation is pending");
            newer.Release.TrySetResult();
            using var current = await second;
            Check(current.StatusCode == HttpStatusCode.OK && await Get(session) == 200,
                "latest evaluation may authorize its session");
        }
        finally { older.Release.TrySetResult(); newer.Release.TrySetResult(); }
        foreach (var resetAll in new[] { false, true })
        {
            await Seed();
            var gate = provider.HoldNext();
            var pending = session.PostAsync(SecurityDecisionServer.EvaluatePath, null);
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                if (resetAll) server.ResetAllSessions();
                else {
                    using var reset = await session.PostAsync(SecurityDecisionServer.ResetPath, null);
                    Check(reset.StatusCode == HttpStatusCode.NoContent, "reset endpoint succeeds during evaluation");
                }
                gate.Release.TrySetResult();
                using var response = await pending;
                Check(response.StatusCode == HttpStatusCode.ServiceUnavailable && await Get(session) == 403,
                    resetAll ? "reset all revokes in-flight evaluation" : "session reset revokes in-flight evaluation");
            }
            finally { gate.Release.TrySetResult(); }
        }
        return count;
    }
    private sealed class ManualClock : TimeProvider
    {
        private long ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }
    private sealed class GatedProvider : ISecurityDecisionProvider
    {
        internal sealed class Gate
        {
            internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private readonly System.Collections.Concurrent.ConcurrentQueue<Gate> gates = new();
        internal Gate HoldNext() { var gate = new Gate(); gates.Enqueue(gate); return gate; }
        public async Task<SecurityDecision> EvaluateAsync(CancellationToken token)
        {
            if (gates.TryDequeue(out var gate)) {
                gate.Started.TrySetResult();
                await gate.Release.Task.WaitAsync(token);
            }
            return await new MockSecurityDecisionProvider().EvaluateAsync(token);
        }
    }
    private sealed class SwitchableProvider : ISecurityDecisionProvider
    {
        public bool Fail;
        public Task<SecurityDecision> EvaluateAsync(CancellationToken token) => Fail ? throw new Exception("secret-provider-token") : new MockSecurityDecisionProvider().EvaluateAsync(token);
    }
    private sealed class InvalidProvider : ISecurityDecisionProvider
    {
        public Task<SecurityDecision> EvaluateAsync(CancellationToken token) => Task.FromResult(new SecurityDecision("AegisMock", "security-decisions", "allow", "secret-invalid-id", "configured-test-fixture"));
    }
}
