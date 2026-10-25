using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AegisBrowser.SecurityLab;

public sealed class SecurityTestRunner
{
    public async Task<SecurityTestReport> RunAsync(SecurityDecisionServer server, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var assertions = new List<LabAssertion>();
        string? requestId = null, decision = null, error = null;
        void Assert(string name, bool ok) { assertions.Add(new(name, ok)); if (!ok) throw new InvalidDataException(); }
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = server.Origin, Timeout = TimeSpan.FromSeconds(10) };
        using var other = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = server.Origin, Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            server.ResetAllSessions();
            using var before = await client.GetAsync(SecurityDecisionServer.ProtectedPath, cancellationToken);
            Assert("Protected content denied before evaluation", before.StatusCode == HttpStatusCode.Forbidden);
            using var forged = await client.GetAsync(SecurityDecisionServer.ProtectedPath + "?decision=allow", cancellationToken);
            Assert("Forged client Allow cannot grant access", forged.StatusCode == HttpStatusCode.Forbidden);
            using var response = await client.PostAsync(SecurityDecisionServer.EvaluatePath, null, cancellationToken);
            Assert("Evaluation HTTP status", response.StatusCode == HttpStatusCode.OK);
            var result = await response.Content.ReadFromJsonAsync<SecurityDecision>(cancellationToken);
            Assert("Valid AegisMock Allow response", result?.IsValidAllow == true);
            requestId = result!.RequestId; decision = "allow";
            using var access = await client.GetAsync(SecurityDecisionServer.ProtectedPath, cancellationToken);
            Assert("Evaluated session receives protected content", access.StatusCode == HttpStatusCode.OK);
            using var body = JsonDocument.Parse(await access.Content.ReadAsStringAsync(cancellationToken));
            Assert("Protected fixture payload", body.RootElement.GetProperty("access").GetBoolean() && body.RootElement.GetProperty("fixture").GetString() == "protected-fixture");
            using var denied = await other.GetAsync(SecurityDecisionServer.ProtectedPath, cancellationToken);
            Assert("Other session remains unauthorized", denied.StatusCode == HttpStatusCode.Forbidden);
            using var reset = await client.PostAsync(SecurityDecisionServer.ResetPath, null, cancellationToken);
            Assert("Session reset accepted", reset.StatusCode == HttpStatusCode.NoContent);
            using var after = await client.GetAsync(SecurityDecisionServer.ProtectedPath, cancellationToken);
            Assert("Reset session remains denied", after.StatusCode == HttpStatusCode.Forbidden);
        }
        catch(OperationCanceledException) { error = cancellationToken.IsCancellationRequested ? "Canceled" : "Timeout"; }
        catch(HttpRequestException) { error = "ConnectionFailure"; }
        catch { error = "UnexpectedResponse"; }
        if (error is not null) { server.ResetAllSessions(); assertions.Add(new("Workflow completed", false)); }
        return new("Security Decisions", "AegisMock", decision, requestId, started, DateTimeOffset.UtcNow,
            assertions.ToArray(), error is null && assertions.Count == 9 && assertions.All(a => a.Passed), error);
    }
}
