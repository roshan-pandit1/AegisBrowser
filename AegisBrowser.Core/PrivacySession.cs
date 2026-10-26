namespace AegisBrowser.Core;

public sealed record RequestObservation(long Number, DateTimeOffset Time, string Site, string Destination,
    string Type, string Party, string Result, string Rule, FilterFallback Fallback = FilterFallback.None);
public sealed record InspectorSnapshot(long Allowed, long Blocked, RequestObservation[] Requests,
    long BudgetFallbacks = 0, long UnknownSites = 0, long UnknownResources = 0, long InvalidInputs = 0);

/// <summary>One profile's in-memory policy and bounded, origin-only inspection history.</summary>
public sealed class PrivacySession
{
    private sealed record PolicyState(PrivacyConfiguration Configuration, RequestFilter Filter);
    private PolicyState state;
    private static PrivacyConfiguration Copy(PrivacyConfiguration config) => config with
    { BlockedDomains = config.BlockedDomains.ToArray(), AllowedDomains = config.AllowedDomains.ToArray(), SiteExceptions = config.SiteExceptions.ToArray() };
    private readonly object gate = new();
    private readonly Queue<RequestObservation> history = new();
    private long allowed, blocked, sequence, budgetFallbacks, unknownSites, unknownResources, invalidInputs;
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
    public async Task ApplyAsync(PrivacyConfiguration configuration, Action<PrivacyConfiguration>? persist = null,
        CancellationToken cancellationToken = default)
    {
        PrivacySettingsStore.Validate(configuration);
        configuration = Copy(configuration);
        var next = await Task.Run(() => new PolicyState(configuration, new(configuration)), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Resume on the caller's UI context: closing the settings form cancels before publication.
        persist?.Invoke(configuration);
        Volatile.Write(ref state, next);
    }
    public FilterDecision Evaluate(NetworkRequest request)
    {
        // Pausing must not run an expensive list scan just to discard its result.
        var decision = Paused
            ? new FilterDecision(false, SiteClassifier.Default.IsThirdParty(request.Url, request.TopUrl), "Temporarily paused")
            : Volatile.Read(ref state).Filter.Evaluate(request);
        lock (gate)
        {
            if (decision.Blocked) blocked++; else allowed++;
            if (decision.Fallback.HasFlag(FilterFallback.BudgetExceeded)) budgetFallbacks++;
            if (decision.Fallback.HasFlag(FilterFallback.UnknownSite)) unknownSites++;
            if (decision.Fallback.HasFlag(FilterFallback.UnknownResource)) unknownResources++;
            if (decision.Fallback.HasFlag(FilterFallback.InvalidInput)) invalidInputs++;
            history.Enqueue(new(++sequence, DateTimeOffset.Now, Origin(request.TopUrl), Origin(request.Url), request.Type,
                decision.ThirdParty is true ? "Third-party" : decision.ThirdParty is false ? "First-party" : "Unknown",
                decision.Blocked ? "Blocked" : "Allowed", decision.Rule, decision.Fallback));
            while (history.Count > 500) history.Dequeue();
        }
        return decision;
    }
    private static string Origin(string? url)
    {
        var host = SiteClassifier.Host(url);
        if (host is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "(internal / unknown)";
        return new UriBuilder(uri.Scheme, host, uri.IsDefaultPort ? -1 : uri.Port).Uri.GetLeftPart(UriPartial.Authority);
    }
    public InspectorSnapshot Snapshot()
    {
        lock (gate) return new(allowed, blocked, history.ToArray(), budgetFallbacks, unknownSites, unknownResources, invalidInputs);
    }
    public void ClearHistory()
    {
        lock (gate) { allowed = blocked = budgetFallbacks = unknownSites = unknownResources = invalidInputs = 0; history.Clear(); }
    }
}
