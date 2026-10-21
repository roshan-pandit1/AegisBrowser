namespace AegisBrowser.Core;

public sealed record RequestObservation(long Number, DateTimeOffset Time, string Site, string Destination,
    string Type, string Party, string Result, string Rule);
public sealed record InspectorSnapshot(long Allowed, long Blocked, RequestObservation[] Requests);

/// <summary>One profile's in-memory policy and bounded, origin-only inspection history.</summary>
public sealed class PrivacySession
{
    private RequestFilter filter;
    private readonly object gate = new();
    private readonly Queue<RequestObservation> history = new();
    private long allowed, blocked, sequence;
    private bool paused;
    public bool Paused { get => Volatile.Read(ref paused); set => Volatile.Write(ref paused, value); }
    public PrivacyConfiguration Configuration { get; private set; }
    public IReadOnlyList<string> Unsupported => Volatile.Read(ref filter).Unsupported;
    public int RuleCount => Volatile.Read(ref filter).RuleCount;
    public PrivacySession(PrivacyConfiguration? configuration = null)
    {
        Configuration = configuration ?? new();
        filter = new(Configuration);
    }
    public void Apply(PrivacyConfiguration configuration)
    {
        var next = new RequestFilter(configuration); // Build fully before publication to CEF IO.
        Configuration = configuration;
        Volatile.Write(ref filter, next);
    }
    public FilterDecision Evaluate(NetworkRequest request)
    {
        var decision = Volatile.Read(ref filter).Evaluate(request);
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
        && uri.Scheme is "http" or "https" ? uri.GetLeftPart(UriPartial.Authority) : "(internal / unknown)";
    public InspectorSnapshot Snapshot() { lock (gate) return new(allowed, blocked, history.ToArray()); }
    public void ClearHistory() { lock (gate) { allowed = blocked = 0; history.Clear(); } }
}
