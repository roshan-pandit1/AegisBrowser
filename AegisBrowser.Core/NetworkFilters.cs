using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AegisBrowser.Core;

public sealed record NetworkRequest(string Url, string? TopUrl, string Type, bool MainFrame = false, bool UnknownResource = false);
[Flags]
public enum FilterFallback { None = 0, BudgetExceeded = 1, UnknownSite = 2, UnknownResource = 4, InvalidInput = 8 }
public sealed record FilterDecision(bool Blocked, bool? ThirdParty, string Rule, FilterFallback Fallback = FilterFallback.None);

/// <summary>Supported ABP network syntax only. Unsupported options reject the entire rule.</summary>
public sealed class NetworkFilterList
{
    private sealed record Rule(string Text, bool Allow, Regex? Pattern, string? Host, bool? ThirdParty,
        string[] Domains, string[] Types, bool MatchCase);
    private readonly Rule[] rules;
    private readonly Rule[] generic;
    private readonly Dictionary<string, Rule[]> byHost;
    public IReadOnlyList<string> Unsupported { get; }
    public int RuleCount => rules.Length;
    private static readonly HashSet<string> ResourceTypes = new(["script", "image", "stylesheet", "font", "media", "xmlhttprequest", "subdocument", "other", "ping", "websocket"], StringComparer.Ordinal);
    public NetworkFilterList(string text)
    {
        if (text.Length > 8_000_000) throw new ArgumentException("Filter list exceeds 8 MB.");
        var accepted = new List<Rule>(); var unsupported = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('!') || line.StartsWith('[')) continue;
            if (accepted.Count + unsupported.Count >= 100_000) throw new ArgumentException("Too many filter rules.");
            if (!TryParse(line, out var rule)) unsupported.Add(line);
            else accepted.Add(rule!);
        }
        rules = accepted.ToArray(); Unsupported = unsupported.AsReadOnly();
        generic = rules.Where(r => r.Host is null).ToArray();
        byHost = rules.Where(r => r.Host is not null).GroupBy(r => r.Host!).ToDictionary(g => g.Key, g => g.ToArray());
    }
    private static bool TryParse(string text, out Rule? rule)
    {
        rule = null;
        if (text.Length > 2048 || text.Contains('#') || text.Contains("$redirect")) return false;
        var allow = text.StartsWith("@@");
        var parts = (allow ? text[2..] : text).Split('$');
        if (parts.Length > 2 || (parts[0].Length > 1 && parts[0].StartsWith('/') && parts[0].EndsWith('/'))) return false;
        if (parts[0].Length == 0) { if (parts.Length == 1) return false; parts[0] = "*"; }
        bool? thirdParty = null; bool matchCase = false;
        var seenOptions = new HashSet<string>(StringComparer.Ordinal);
        var domains = Array.Empty<string>(); var types = new List<string>();
        if (parts.Length == 2)
        foreach (var option in parts[1].Split(','))
        {
            var name = option.StartsWith("domain=") ? "domain" : option.TrimStart('~');
            if (option.StartsWith("~~") || !seenOptions.Add(name)) return false;
            if (option == "third-party") thirdParty = true;
            else if (option == "~third-party") thirdParty = false;
            else if (option == "match-case") matchCase = true;
            else if (option.StartsWith("domain="))
            {
                try
                {
                    domains = option[7..].Split('|').Select(d => {
                        if (d.StartsWith("~~")) throw new ArgumentException("Invalid domain negation.");
                        return (d.StartsWith('~') ? "~" : "") + ValidateDomain(d.StartsWith('~') ? d[1..] : d);
                    }).ToArray();
                }
                catch (ArgumentException) { return false; }
            }
            else if (ResourceTypes.Contains(option.TrimStart('~'))) types.Add(option);
            else return false;
        }
        var pattern = parts[0];
        var prefix = pattern.StartsWith("||") ? @"^https?://(?:[^/:]+\.)?" : pattern.StartsWith('|') ? "^" : "";
        if (pattern.StartsWith("||")) pattern = pattern[2..]; else if (pattern.StartsWith('|')) pattern = pattern[1..];
        var suffix = pattern.EndsWith('|') ? "$" : "";
        if (suffix.Length > 0) pattern = pattern[..^1];
        if (pattern.Length == 0 || pattern.Contains('|')) return false;
        if (prefix.StartsWith("^https", StringComparison.Ordinal))
        {
            var end = pattern.IndexOfAny(new[] { '^', '/', '?', ':' });
            var hostPart = end < 0 ? pattern : pattern[..end];
            if (!hostPart.Contains('*'))
            {
                try { pattern = ValidateDomain(hostPart) + (end < 0 ? "" : pattern[end..]); }
                catch (ArgumentException) { return false; }
            }
        }
        var expression = prefix + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\^", @"(?:[^a-zA-Z0-9_\-.%]|$)") + suffix;
        try
        {
            string? host = null;
            if (prefix.StartsWith("^https") && suffix.Length == 0 && pattern.EndsWith('^'))
            {
                try { host = ValidateDomain(pattern[..^1]); } catch (ArgumentException) { }
            }
            var regex = host is not null ? null : new Regex(expression, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking
                | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(50));
            rule = new(text, allow, regex, host, thirdParty, domains, types.ToArray(), matchCase);
            return true;
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; } // NonBacktracking rejects oversized automata.
    }
    public static string ValidateDomain(string domain)
    {
        domain = SiteClassifier.NormalizeHost(domain.Trim());
        if (Uri.CheckHostName(domain) == UriHostNameType.Unknown || domain.Contains(':')) throw new ArgumentException("Enter a hostname without scheme, path or port.");
        return domain;
    }
    public string? Match(NetworkRequest request, bool? thirdParty, bool allow) =>
        Match(request, thirdParty, allow, Stopwatch.GetTimestamp());
    internal string? Match(NetworkRequest request, bool? thirdParty, bool allow, long started)
    {
        var top = SiteClassifier.Host(request.TopUrl);
        var host = SiteClassifier.Host(request.Url);
        IEnumerable<Rule> Candidates()
        {
            if (host is not null)
            {
                var part = host;
                while (true)
                {
                    if (byHost.TryGetValue(part, out var matches)) foreach (var candidate in matches) yield return candidate;
                    var dot = part.IndexOf('.');
                    if (dot < 0) break;
                    part = part[(dot + 1)..];
                }
            }
            foreach (var candidate in generic) yield return candidate;
        }
        foreach (var rule in Candidates())
        {
            if (Stopwatch.GetElapsedTime(started).TotalMilliseconds > 25) throw new FilterBudgetExceededException();
            if (rule.Allow != allow || (rule.ThirdParty.HasValue && thirdParty != rule.ThirdParty)) continue;
            if (rule.Domains.Length > 0)
            {
                if (top is null || rule.Domains.Any(d => d.StartsWith('~') && SiteClassifier.DomainMatches(top, d[1..]))) continue;
                var included = rule.Domains.Where(d => !d.StartsWith('~')).ToArray();
                if (included.Length > 0 && !included.Any(d => SiteClassifier.DomainMatches(top, d))) continue;
            }
            if (rule.Types.Contains("~" + request.Type)) continue;
            if (rule.Types.Any(t => !t.StartsWith('~')) && !rule.Types.Contains(request.Type)) continue;
            if (rule.Host is not null ? host is not null && SiteClassifier.DomainMatches(host, rule.Host)
                : rule.Pattern!.IsMatch(request.Url)) return rule.Text;
        }
        return null;
    }
}

public sealed class RequestFilter
{
    private readonly NetworkFilterList list;
    private readonly string[] blocks;
    private readonly string[] allows;
    private readonly PrivacyMode mode;
    private readonly string[] siteExceptions = [];
    public IReadOnlyList<string> Unsupported => list.Unsupported;
    public int RuleCount => list.RuleCount;
    public RequestFilter(PrivacyConfiguration configuration)
        : this(new NetworkFilterList((configuration.Mode != PrivacyMode.Custom || configuration.UseListsInCustom
            ? configuration.FilterList + "\n" : "") + configuration.UserRules),
            configuration.BlockedDomains, configuration.AllowedDomains)
    {
        if (!Enum.IsDefined(configuration.Mode)) throw new ArgumentException("Unknown privacy mode.");
        mode = configuration.Mode;
        siteExceptions = configuration.SiteExceptions.Select(NetworkFilterList.ValidateDomain).ToArray();
    }
    public RequestFilter(NetworkFilterList list, IEnumerable<string>? blockedDomains = null, IEnumerable<string>? allowedDomains = null)
    {
        this.list = list;
        blocks = (blockedDomains ?? []).Select(NetworkFilterList.ValidateDomain).ToArray();
        allows = (allowedDomains ?? []).Select(NetworkFilterList.ValidateDomain).ToArray();
    }
    public FilterDecision Evaluate(NetworkRequest request)
    {
        if (string.IsNullOrEmpty(request.Url) || request.Url.Length > 65_536 || request.TopUrl?.Length > 65_536)
            return new(false, null, "Request input limit exceeded: allowed", FilterFallback.InvalidInput);
        var third = SiteClassifier.Default.IsThirdParty(request.Url, request.TopUrl);
        var coverage = request.UnknownResource ? FilterFallback.UnknownResource : FilterFallback.None;
        if (!request.MainFrame && third is null && SiteClassifier.Host(request.Url) is not null)
            coverage |= FilterFallback.UnknownSite;
        try
        {
            var result = EvaluateCore(request, Stopwatch.GetTimestamp());
            return result with { Fallback = result.Fallback | coverage };
        }
        catch (Exception ex) when (ex is FilterBudgetExceededException or RegexMatchTimeoutException)
        { return new(false, third, "Filter evaluation budget exceeded: allowed", coverage | FilterFallback.BudgetExceeded); }
        catch (ArgumentException)
        { return new(false, third, "Invalid request classification: allowed", coverage | FilterFallback.InvalidInput); }
    }
    private FilterDecision EvaluateCore(NetworkRequest request, long started)
    {
        var third = SiteClassifier.Default.IsThirdParty(request.Url, request.TopUrl);
        FilterDecision Result(bool block, string rule) => new(block, third, rule);
        var host = SiteClassifier.Host(request.Url);
        if (host is null || host == "aegis-diagnostics.invalid") return Result(false, "Internal / non-network resource");
        if (request.MainFrame) return Result(false, "Top-level navigation");
        var top = SiteClassifier.Host(request.TopUrl);
        if (top is not null && siteExceptions.FirstOrDefault(d => SiteClassifier.DomainMatches(top, d)) is { } site)
            return Result(false, "Site exception: " + site);
        if (allows.FirstOrDefault(d => SiteClassifier.DomainMatches(host, d)) is { } allowed) return Result(false, "Allow domain: " + allowed);
        if (list.Match(request, third, true, started) is { } exception) return Result(false, exception);
        if (blocks.FirstOrDefault(d => SiteClassifier.DomainMatches(host, d)) is { } blocked) return Result(true, "Block domain: " + blocked);
        if (list.Match(request, third, false, started) is { } match) return Result(true, match);
        if (mode == PrivacyMode.Strict && third == true) return Result(true, "Strict: third-party request");
        return Result(false, third is null ? "Unknown document site: allowed" : "No matching rule");
    }
}

internal sealed class FilterBudgetExceededException : Exception { }
