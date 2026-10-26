using System.Net;
using System.Text.Json;

namespace AegisBrowser.Core;

public static class DiagnosticUrl
{
    // Only these structural route words survive. Unknown segments may be respondent IDs.
    private static readonly HashSet<string> SafeSegments = new(StringComparer.OrdinalIgnoreCase)
    { "survey", "surveys", "api", "start", "end", "complete", "status", "security", "redirect", "login", "auth", "scripts", "static", "assets" };
    public static string Sanitize(string? value)
    {
        if (value is null || value.Length > 8192 || value.Any(char.IsControl) || value.Contains('\\')) return "(redacted)";
        try
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "(redacted)";
            var host = uri.IdnHost;
            if (IPAddress.TryParse(host.Trim('[', ']'), out _)) host = "[ip-redacted]";
            // Opaque/participant-specific subdomains are unnecessary for this report.
            else if (host.Split('.').Any(label => label.Length > 32 || label.Any(char.IsDigit))) host = "[host-redacted]";
            else
            {
                var site = SiteClassifier.Default.Site(host);
                if (host != site)
                {
                    var prefix = host[..^(site.Length + 1)];
                    if (prefix.Split('.').Any(label => label != "www" && !SafeSegments.Contains(label))) host = "[redacted]." + site;
                }
            }
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(8)
                .Select(segment => SafeSegments.Contains(segment) ? segment.ToLowerInvariant() : "[redacted]");
            var result = uri.Scheme + "://" + host + (uri.IsDefaultPort ? "" : ":" + uri.Port) + "/" + string.Join('/', segments);
            return result.Length <= 512 ? result : "(redacted)";
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException) { return "(redacted)"; }
    }
}

public enum DiagnosticKind { Navigation, MainDocumentLoaded, LoadError, ConsoleError, FilterAllowed, FilterBlocked, Redirect, ResourceComplete, CertificateRejected }
public enum DiagnosticRule { None, DomainBlock, StrictThirdParty, ListRule, Exception, Paused, Fallback }
public sealed record NavigationDiagnostic(long Sequence, DateTimeOffset Time, DiagnosticKind Kind, int Browser,
    ulong Request, string Location, string RedirectTo, int ResourceType, int Status, int Error, DiagnosticRule Rule, int Outcome);

/// <summary>Optional bounded in-memory observations. No headers, bodies or free-text messages accepted.</summary>
public sealed class NavigationDiagnostics
{
    public const int Capacity = 500;
    private readonly object gate = new();
    private readonly Queue<NavigationDiagnostic> entries = new();
    private bool enabled;
    private long sequence;
    public bool Enabled { get { lock (gate) return enabled; } set { lock (gate) { enabled = value; if (!value) entries.Clear(); } } }
    public void Clear() { lock (gate) entries.Clear(); }
    public NavigationDiagnostic[] Snapshot() { lock (gate) return entries.ToArray(); }
    public string Export() => new NavigationDiagnosticReport(Snapshot()).ToJson();
    public NavigationDiagnosticReport StopForExport()
    {
        lock (gate)
        {
            var report = new NavigationDiagnosticReport(entries.ToArray());
            enabled = false;
            entries.Clear();
            return report;
        }
    }
    public void Record(DiagnosticKind kind, string? url, int browser = 0, ulong request = 0, int resourceType = 0,
        int status = 0, int error = 0, DiagnosticRule rule = DiagnosticRule.None, string? redirectTo = null, int outcome = 0)
    {
        // Diagnostics must never alter a CEF callback's decision if collection fails.
        try
        {
            lock (gate)
            {
                if (!enabled) return;
                entries.Enqueue(new(++sequence, DateTimeOffset.UtcNow, kind, browser, request,
                    DiagnosticUrl.Sanitize(url), redirectTo is null ? "" : DiagnosticUrl.Sanitize(redirectTo), resourceType,
                    status is >= 100 and <= 599 ? status : 0, error, rule, outcome));
                while (entries.Count > Capacity) entries.Dequeue();
            }
        }
        catch (Exception) { /* Best-effort memory-only instrumentation, never a request policy. */ }
    }
    public void Observe(Action<NavigationDiagnostics> observation)
    {
        if (!Enabled) return;
        try { observation(this); }
        catch (Exception) { /* CEF object disposal or diagnostic failures cannot change browsing. */ }
    }
    public static DiagnosticRule Category(FilterDecision decision)
    {
        if (decision.Fallback != FilterFallback.None) return DiagnosticRule.Fallback;
        if (decision.Rule == "Temporarily paused") return DiagnosticRule.Paused;
        if (decision.Rule.StartsWith("Block domain: ", StringComparison.Ordinal)) return DiagnosticRule.DomainBlock;
        if (decision.Rule == "Strict: third-party request") return DiagnosticRule.StrictThirdParty;
        if (decision.Blocked) return DiagnosticRule.ListRule;
        if (decision.Rule.StartsWith("@@", StringComparison.Ordinal) || decision.Rule.StartsWith("Allow domain: ", StringComparison.Ordinal)
            || decision.Rule.StartsWith("Site exception: ", StringComparison.Ordinal)) return DiagnosticRule.Exception;
        return DiagnosticRule.None;
    }
}

/// <summary>An explicitly requested, immutable export snapshot; not live collection.</summary>
public sealed class NavigationDiagnosticReport
{
    public const int MaximumJsonBytes = 4 * 1024 * 1024;
    private readonly NavigationDiagnostic[] entries;
    internal NavigationDiagnosticReport(NavigationDiagnostic[] entries) => this.entries = entries;
    public string ToJson()
    {
        var json = JsonSerializer.Serialize(new { Scope = "Observed browser callbacks only; HTTP success is not survey acceptance", Entries = entries },
            new JsonSerializerOptions { WriteIndented = true });
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            throw new InvalidOperationException("Diagnostic report exceeds size limit.");
        return json;
    }
    public Task<bool> TrySaveAsync(string path) => Task.Run(async () =>
    {
        try
        {
            await File.WriteAllTextAsync(path, ToJson()).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException or InvalidOperationException)
        { return false; } // Do not retain or display exception text, URLs or filesystem paths.
    });
}
