using System.Text.Json;

namespace AegisBrowser.SecurityLab;

public sealed record SecurityDecision(string Provider, string Scenario, string Decision, string RequestId, string Reason)
{
    public bool IsValidAllow => Provider == "AegisMock" && Scenario == "security-decisions" && Decision == "allow"
        && Guid.TryParseExact(RequestId, "N", out _) && Reason == "configured-test-fixture";
}

public interface ISecurityDecisionProvider
{
    Task<SecurityDecision> EvaluateAsync(CancellationToken cancellationToken);
}

// Intentionally has no URL, credentials or network dependency.
public sealed class MockSecurityDecisionProvider : ISecurityDecisionProvider
{
    public Task<SecurityDecision> EvaluateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SecurityDecision("AegisMock", "security-decisions", "allow", Guid.NewGuid().ToString("N"), "configured-test-fixture"));
    }
}

public sealed record EvaluationEvent(DateTimeOffset Time, string Outcome, string? RequestId);
public sealed record LabAssertion(string Name, bool Passed);
public sealed record SecurityTestReport(string Scenario, string MockProvider, string? Decision, string? RequestId,
    DateTimeOffset Started, DateTimeOffset Ended, IReadOnlyList<LabAssertion> Assertions, bool Passed, string? Error)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
}
