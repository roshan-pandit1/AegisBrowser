using System.Security.Cryptography;

namespace AegisBrowser.Lab;

// Session authorization is bounded and expires server-side, independently of client cookies.
public sealed class LabSessionRegistry(TimeProvider? clock = null)
{
    private sealed record Session(DateTimeOffset Expires, long GrantVersion = -1);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private bool closed;
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public string? Issue(string? previous = null)
    {
        lock (gate)
        {
            if (closed) return null;
            var now = time.GetUtcNow();
            foreach (var key in sessions.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
                sessions.Remove(key);
            if (previous is not null) sessions.Remove(previous);
            if (sessions.Count >= 100) return null;
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            sessions.Add(id, new(now + Lifetime));
            return id;
        }
    }
    public bool Exists(string? id)
    {
        lock (gate) return !closed && id is not null && sessions.TryGetValue(id, out var session) && session.Expires > time.GetUtcNow();
    }
    public bool Apply(string id, long version, SecurityDecision result, MockSecurityState current, bool canceled)
    {
        lock (gate)
        {
            if (closed || !sessions.TryGetValue(id, out var session) || session.Expires <= time.GetUtcNow()) return false;
            var allowed = !canceled && result == SecurityDecision.Allow && current.Decision == SecurityDecision.Allow && current.Version == version;
            sessions[id] = session with { GrantVersion = allowed ? version : -1 };
            return allowed;
        }
    }
    public bool IsAuthorized(string? id, MockSecurityState state)
    {
        lock (gate) return !closed && id is not null && sessions.TryGetValue(id, out var session)
            && session.Expires > time.GetUtcNow() && state.Decision == SecurityDecision.Allow && session.GrantVersion == state.Version;
    }
    public void Revoke(string? id) { if (id is not null) { lock (gate) sessions.Remove(id); } }
    public void Close() { lock (gate) { closed = true; sessions.Clear(); } }
    public void Clear() { lock (gate) sessions.Clear(); }
}
