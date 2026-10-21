using System.Text.RegularExpressions;

namespace AegisBrowser.Core;

public sealed record NetworkRequest(string Url, string? TopUrl, string Type, bool MainFrame = false);
public sealed record FilterDecision(bool Blocked, bool? ThirdParty, string Rule);

/// <summary>Supported ABP network syntax only. Unsupported options reject the entire rule.</summary>
public sealed class NetworkFilterList
{
    private sealed record Rule(string Text, bool Allow, Regex Pattern, bool? ThirdParty,
        string[] Domains, string[] Types, bool MatchCase);
    private readonly Rule[] rules;
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
    }
    private static bool TryParse(string text, out Rule? rule)
    {
        rule = null;
        if (text.Length > 2048 || text.Contains('#') || text.StartsWith('/') || text.Contains("$redirect")) return false;
        var allow = text.StartsWith("@@");
        var parts = (allow ? text[2..] : text).Split('$');
        if (parts.Length > 2 || parts[0].Length == 0) return false;
        bool? thirdParty = null; bool matchCase = false;
        var domains = Array.Empty<string>(); var types = new List<string>();
        if (parts.Length == 2)
        foreach (var option in parts[1].Split(','))
        {
            if (option == "third-party") thirdParty = true;
            else if (option == "~third-party") thirdParty = false;
            else if (option == "match-case") matchCase = true;
            else if (option.StartsWith("domain="))
            {
                try
                {
                    domains = option[7..].Split('|').Select(d => (d.StartsWith('~') ? "~" : "") + ValidateDomain(d.TrimStart('~'))).ToArray();
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
        var expression = prefix + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\^", @"(?:[^a-zA-Z0-9_\-.%]|$)") + suffix;
        try
        {
            var regex = new Regex(expression, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking
                | (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(50));
            rule = new(text, allow, regex, thirdParty, domains, types.ToArray(), matchCase);
            return true;
        }
        catch (ArgumentException) { return false; }
    }
    public static string ValidateDomain(string domain)
    {
        domain = SiteClassifier.NormalizeHost(domain.Trim());
        if (Uri.CheckHostName(domain) == UriHostNameType.Unknown || domain.Contains(':')) throw new ArgumentException("Enter a hostname without scheme, path or port.");
        return domain;
    }
    public string? Match(NetworkRequest request, bool? thirdParty, bool allow)
    {
        var top = SiteClassifier.Host(request.TopUrl);
        foreach (var rule in rules)
        {
            if (rule.Allow != allow || (rule.ThirdParty.HasValue && thirdParty != rule.ThirdParty)) continue;
            if (rule.Domains.Length > 0)
            {
                if (top is null || rule.Domains.Any(d => d.StartsWith('~') && SiteClassifier.DomainMatches(top, d[1..]))) continue;
                var included = rule.Domains.Where(d => !d.StartsWith('~')).ToArray();
                if (included.Length > 0 && !included.Any(d => SiteClassifier.DomainMatches(top, d))) continue;
            }
            if (rule.Types.Contains("~" + request.Type)) continue;
            if (rule.Types.Any(t => !t.StartsWith('~')) && !rule.Types.Contains(request.Type)) continue;
            try { if (rule.Pattern.IsMatch(request.Url)) return rule.Text; }
            catch (RegexMatchTimeoutException) { /* An expensive rule must not stall CEF's IO thread. */ }
        }
        return null;
    }
}

public sealed class RequestFilter
{
    private readonly NetworkFilterList list;
    private readonly string[] blocks;
    private readonly string[] allows;
    public RequestFilter(NetworkFilterList list, IEnumerable<string>? blockedDomains = null, IEnumerable<string>? allowedDomains = null)
    {
        this.list = list;
        blocks = (blockedDomains ?? []).Select(NetworkFilterList.ValidateDomain).ToArray();
        allows = (allowedDomains ?? []).Select(NetworkFilterList.ValidateDomain).ToArray();
    }
    public FilterDecision Evaluate(NetworkRequest request)
    {
        var third = SiteClassifier.Default.IsThirdParty(request.Url, request.TopUrl);
        FilterDecision Result(bool block, string rule) => new(block, third, rule);
        var host = SiteClassifier.Host(request.Url);
        if (host is null || host == "aegis-diagnostics.invalid") return Result(false, "Internal / non-network resource");
        if (request.MainFrame) return Result(false, "Top-level navigation");
        if (allows.FirstOrDefault(d => SiteClassifier.DomainMatches(host, d)) is { } allowed) return Result(false, "Allow domain: " + allowed);
        if (list.Match(request, third, true) is { } exception) return Result(false, exception);
        if (blocks.FirstOrDefault(d => SiteClassifier.DomainMatches(host, d)) is { } blocked) return Result(true, "Block domain: " + blocked);
        if (list.Match(request, third, false) is { } match) return Result(true, match);
        return Result(false, "No matching rule");
    }
}
