namespace AegisBrowser.Core;

public sealed record RequestObservation(long Number, DateTimeOffset Time, string Site, string Destination,
    string Type, string Party, string Result, string Rule);
public sealed record InspectorSnapshot(long Allowed, long Blocked, RequestObservation[] Requests);

/// <summary>One profile's in-memory policy and bounded, origin-only inspection history.</summary>
public sealed class PrivacySession
{
    private sealed record PolicyState(PrivacyConfiguration Configuration, RequestFilter Filter);
    private PolicyState state;
    private static PrivacyConfiguration Copy(PrivacyConfiguration config) => config with
    { BlockedDomains = config.BlockedDomains.ToArray(), AllowedDomains = config.AllowedDomains.ToArray(), SiteExceptions = config.SiteExceptions.ToArray() };
    private readonly object gate = new();
    private readonly Queue<RequestObservation> history = new();
    private long allowed, blocked, sequence;
    private bool paused;
    public bool Paused { get => Volatile.Read(ref paused); set => Volatile.Write(ref paused, value); }
    public PrivacyConfiguration Configuration => Copy(Volatile.Read(ref state).Configuration);
    public IReadOnlyList<string> Unsupported => Volatile.Read(ref state).Filter.Unsupported;
    public int RuleCount => Volatile.Read(ref state).Filter.RuleCount;
    public PrivacySession(PrivacyConfiguration? configuration = null)
    {
        var config = configuration ?? new();
        PrivacySettingsStore.Validate(config);
        config = Copy(config);
        state = new(config, new(config));
    }
    public void Apply(PrivacyConfiguration configuration, Action<PrivacyConfiguration>? persist = null)
    {
        PrivacySettingsStore.Validate(configuration);
        configuration = Copy(configuration);
        var next = new PolicyState(configuration, new(configuration));
        persist?.Invoke(configuration); // A failed save leaves the active policy unchanged.
        Volatile.Write(ref state, next);
    }
    public FilterDecision Evaluate(NetworkRequest request)
    {
        var decision = Volatile.Read(ref state).Filter.Evaluate(request);
        if (Paused) decision = decision with { Blocked = false, Rule = "Temporarily paused" };
        lock (gate)
        {
            if (decision.Blocked) blocked++; else allowed++;
            history.Enqueue(new(++sequence, DateTimeOffset.Now, Origin(request.TopUrl), Origin(request.Url), request.Type,
                decision.ThirdParty is true ? "Third-party" : decision.ThirdParty is false ? "First-party" : "Unknown",
                decision.Blocked ? "Blocked" : "Allowed", decision.Rule));
            while (history.Count > 500) history.Dequeue();
        }
        return decision;
    }
    private static string Origin(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" ? new UriBuilder(uri.Scheme, uri.IdnHost, uri.IsDefaultPort ? -1 : uri.Port).Uri.GetLeftPart(UriPartial.Authority) : "(internal / unknown)";
    public InspectorSnapshot Snapshot() { lock (gate) return new(allowed, blocked, history.ToArray()); }
    public void ClearHistory() { lock (gate) { allowed = blocked = 0; history.Clear(); } }
}
