using AegisBrowser.Core;
using System.Text.Json;

namespace AegisBrowser.Lab;

public enum LabProfile { Baseline, StrictPrivacy, CustomRules, FailureTesting }
public enum SecurityDecision { Allow, Challenge, Deny, Unavailable }
public enum LabEventKind { Navigation, Request, Redirect, Complete, NetworkFailure, TlsFailure, Authentication, Security, Execution }
public sealed record LabEvent(long Sequence, LabEventKind Kind, string Destination, int Status, string Outcome, long Milliseconds, ulong RequestId);

public sealed class LabReport
{
    private readonly object gate = new();
    private readonly Queue<LabEvent> events = new();
    private long sequence;
    public static string Destination(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "(unavailable)";
        // No path, user info, query, fragment or arbitrary hostnames in exports.
        var host = uri.Host is "127.0.0.1" or "localhost" ? uri.Host : "external-redacted";
        return $"{uri.Scheme}://{host}:{uri.Port}";
    }
    public void Add(LabEventKind kind, string? url, int status, string outcome, long milliseconds = 0, ulong requestId = 0)
    {
        // Outcomes are fixed labels/enums, never server text, rule text or exception messages.
        string[] labels = ["Allowed", "Blocked", "Redirect", "Success", "Failed", "Canceled", "Unknown", "Authenticated", "Unauthenticated", "Allow", "Challenge", "Deny", "Unavailable", "Started", "Finished", "NameNotResolved", "ConnectionRefused", "ConnectionClosed", "ConnectionReset", "ConnectionTimedOut", "TimedOut", "CertAuthorityInvalid", "CertDateInvalid", "CertCommonNameInvalid", "Aborted", "None"];
        if (!labels.Contains(outcome)) outcome = "Unknown";
        lock (gate)
        {
            events.Enqueue(new(++sequence, kind, Destination(url), status, outcome, Math.Max(0, milliseconds), requestId));
            while (events.Count > 1000) events.Dequeue();
        }
    }
    public LabEvent[] Snapshot() { lock (gate) return events.ToArray(); }
    public string Export(LabProfile profile) => JsonSerializer.Serialize(new { Schema = 1, Profile = profile.ToString(), Events = Snapshot() }, new JsonSerializerOptions { WriteIndented = true });
}

public static class LabProfiles
{
    public static LabProfile Parse(string name) => name switch {
        "baseline" => LabProfile.Baseline, "strict" => LabProfile.StrictPrivacy,
        "custom" => LabProfile.CustomRules, "failure" => LabProfile.FailureTesting,
        _ => throw new ArgumentException("Use --lab=baseline|strict|custom|failure") };
    public static string PathFor(string localData, LabProfile profile) => Path.Combine(localData, "AegisBrowser", "LabProfiles", profile.ToString());
    public static PrivacyConfiguration Configuration(LabProfile profile, string customRules = "") => new() {
        Mode = profile == LabProfile.StrictPrivacy ? PrivacyMode.Strict : profile == LabProfile.CustomRules ? PrivacyMode.Custom : PrivacyMode.Standard,
        UserRules = profile == LabProfile.CustomRules ? customRules : "" };
}

// Extension boundary only. No provider adapter is enabled without its official test
// documentation, explicit authorization and a reviewed endpoint allowlist.
public interface ISecurityTestAdapter
{
    Task<SecurityDecision> EvaluateAsync(CancellationToken cancellationToken);
}
public sealed class MockSecurityAdapter : ISecurityTestAdapter
{
    private long version;
    public long Version => Interlocked.Read(ref version);
    private int decision = (int)SecurityDecision.Deny;
    public SecurityDecision Decision { get => (SecurityDecision)Volatile.Read(ref decision); set {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        Volatile.Write(ref decision, (int)value); Interlocked.Increment(ref version); } }
    public Task<SecurityDecision> EvaluateAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Decision); }
}
