namespace AegisBrowser.Core;

public sealed record SessionHistoryEntry(Guid Id, Guid TabId, string Url, string Title, DateTimeOffset VisitedUtc);

/// <summary>Bounded in-memory navigation history. No storage or UI dependencies.</summary>
public sealed class SessionHistoryService
{
    public const int DefaultEntryLimit = 1000, MaxUrlLength = 2048, MaxTitleLength = 256;
    private sealed class Navigation(long generation, bool allowed, bool reload)
    {
        public long Generation = generation;
        public bool Allowed = allowed;
        public bool Reload = reload;
        public Guid? EntryId;
        public bool Completed;
    }

    private readonly object gate = new();
    private readonly List<SessionHistoryEntry> entries = [];
    private readonly Dictionary<Guid, Navigation> tabs = [];
    private readonly Func<DateTimeOffset> clock;
    private readonly int limit;
    private long generation;
    private bool paused;

    public SessionHistoryService(Func<DateTimeOffset>? clock = null, int entryLimit = DefaultEntryLimit)
    {
        if (entryLimit is < 1 or > DefaultEntryLimit) throw new ArgumentOutOfRangeException(nameof(entryLimit));
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        limit = entryLimit;
    }

    public event EventHandler? Changed;
    public bool IsPaused { get { lock (gate) return paused; } }

    public long BeginNavigation(Guid tabId, bool reload = false)
    {
        if (tabId == Guid.Empty) throw new ArgumentException("A tab identity is required.", nameof(tabId));
        lock (gate)
        {
            var id = checked(++generation);
            tabs[tabId] = new Navigation(id, !paused, reload);
            return id;
        }
    }

    public bool SetReload(Guid tabId, long navigationId, bool reload)
    {
        lock (gate)
        {
            if (!tabs.TryGetValue(tabId, out var nav) || nav.Generation != navigationId || nav.Completed) return false;
            nav.Reload = reload;
            return true;
        }
    }

    public void FailNavigation(Guid tabId, long navigationId)
    {
        lock (gate)
            if (tabs.TryGetValue(tabId, out var nav) && nav.Generation == navigationId) nav.Allowed = false;
    }

    public bool CompleteNavigation(Guid tabId, long navigationId, string? url, int httpStatus)
    {
        if (httpStatus is < 200 or >= 400 || !TrySanitizeUrl(url, out var normalized)) return false;
        var changed = false;
        lock (gate)
        {
            if (paused || !tabs.TryGetValue(tabId, out var nav) || nav.Generation != navigationId || !nav.Allowed || nav.Completed)
                return false;
            nav.Completed = true;
            if (nav.Reload) return false;
            var entry = new SessionHistoryEntry(Guid.NewGuid(), tabId, normalized, new Uri(normalized).Host, clock().ToUniversalTime());
            entries.Add(entry);
            nav.EntryId = entry.Id;
            if (entries.Count > limit) entries.RemoveAt(0);
            changed = true;
        }
        if (changed) NotifyChanged();
        return changed;
    }

    public bool UpdateTitle(Guid tabId, long navigationId, string? title)
    {
        var cleaned = SanitizeTitle(title);
        lock (gate)
        {
            if (paused || !tabs.TryGetValue(tabId, out var nav) || !nav.Allowed || nav.Generation != navigationId || nav.EntryId is not Guid id) return false;
            var index = entries.FindIndex(entry => entry.Id == id);
            if (index < 0) return false;
            var display = cleaned.Length == 0 ? new Uri(entries[index].Url).Host : cleaned;
            if (entries[index].Title == display) return false;
            entries[index] = entries[index] with { Title = display };
        }
        NotifyChanged();
        return true;
    }

    public void CloseTab(Guid tabId) { lock (gate) tabs.Remove(tabId); }

    public void SetPaused(bool value)
    {
        lock (gate)
        {
            if (paused == value) return;
            paused = value;
            if (value) foreach (var navigation in tabs.Values) navigation.Allowed = false;
        }
        NotifyChanged();
    }

    public IReadOnlyList<SessionHistoryEntry> Snapshot(string? search = null)
    {
        lock (gate)
        {
            var term = search ?? "";
            if (term.Length > MaxUrlLength) term = term[..MaxUrlLength];
            term = term.Trim();
            return entries.AsEnumerable().Reverse().Where(e => term.Length == 0 ||
                e.Title.Contains(term, StringComparison.OrdinalIgnoreCase) || e.Url.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
    }

    public int Remove(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var selection = ids.Take(DefaultEntryLimit + 1).ToHashSet();
        if (selection.Count > DefaultEntryLimit) throw new ArgumentException("Selection exceeds the history limit.", nameof(ids));
        int removed;
        lock (gate) removed = entries.RemoveAll(entry => selection.Contains(entry.Id));
        if (removed > 0) NotifyChanged();
        return removed;
    }

    public void Clear()
    {
        lock (gate)
        {
            if (entries.Count == 0) return;
            entries.Clear();
            foreach (var tab in tabs.Values) tab.EntryId = null;
        }
        NotifyChanged();
    }

    public static bool TrySanitizeUrl(string? value, out string result)
    {
        result = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxUrlLength || value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.Host.Length == 0 || uri.Host.Equals("aegis-diagnostics.invalid", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var builder = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" };
            var safe = builder.Uri.AbsoluteUri;
            if (safe.Length > MaxUrlLength) return false;
            result = safe;
            return true;
        }
        catch (UriFormatException) { return false; }
    }

    public static string SanitizeTitle(string? title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        var buffer = new System.Text.StringBuilder(Math.Min(title.Length, MaxTitleLength));
        var examined = Math.Min(title.Length, MaxUrlLength);
        for (var i = 0; i < examined; i++)
        {
            var c = title[i];
            if (char.IsControl(c) || c is '\u061c' or '\u200e' or '\u200f' or >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069') continue;
            if (char.IsHighSurrogate(c) && i + 1 < examined && char.IsLowSurrogate(title[i + 1]))
            {
                if (buffer.Length + 2 > MaxTitleLength) break;
                buffer.Append(c); buffer.Append(title[++i]);
                if (buffer.Length == MaxTitleLength) break;
                continue;
            }
            if (char.IsSurrogate(c)) continue;
            buffer.Append(c);
            if (buffer.Length == MaxTitleLength) break;
        }
        return buffer.ToString().Trim();
    }

    private void NotifyChanged()
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
            try { handler(this, EventArgs.Empty); } catch (Exception) { /* Observers cannot disrupt navigation. */ }
    }
}
