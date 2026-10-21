using System.Globalization;
using System.Net;

namespace AegisBrowser.Core;

/// <summary>Scheme-independent registrable sites using ICANN and PRIVATE PSL rules.</summary>
public sealed class SiteClassifier
{
    private readonly HashSet<string> exact = new(StringComparer.Ordinal);
    private readonly HashSet<string> wildcards = new(StringComparer.Ordinal);
    private readonly HashSet<string> exceptions = new(StringComparer.Ordinal);
    public static SiteClassifier Default { get; } = Load();
    private static SiteClassifier Load()
    {
        using var stream = typeof(SiteClassifier).Assembly.GetManifestResourceStream("AegisBrowser.Core.public_suffix_list.dat")!;
        using var reader = new StreamReader(stream);
        return new(reader.ReadToEnd());
    }
    public SiteClassifier(string list)
    {
        foreach (var raw in list.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            var target = line.StartsWith('!') ? exceptions : line.StartsWith("*.") ? wildcards : exact;
            target.Add(NormalizeHost(line.TrimStart('!', '*', '.')));
        }
    }
    public static string NormalizeHost(string host)
    {
        host = host.TrimEnd('.');
        if (IPAddress.TryParse(host.Trim('[', ']'), out var address)) return address.ToString();
        return new IdnMapping().GetAscii(host.ToLowerInvariant()).ToLowerInvariant();
    }
    public static string? Host(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" ? NormalizeHost(uri.IdnHost) : null;
    public static bool DomainMatches(string host, string domain) => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
    public string Site(string host)
    {
        host = NormalizeHost(host);
        if (IPAddress.TryParse(host.Trim('[', ']'), out _) || !host.Contains('.')) return host;
        var labels = host.Split('.');
        var suffix = 1; // PSL implicit * rule
        for (var i = 0; i < labels.Length; i++)
        {
            var part = string.Join('.', labels[i..]);
            if (exceptions.Contains(part)) return string.Join('.', labels[Math.Max(0, i)..]);
            if (exact.Contains(part)) suffix = Math.Max(suffix, labels.Length - i);
            if (i > 0 && wildcards.Contains(part)) suffix = Math.Max(suffix, labels.Length - i + 1);
        }
        return string.Join('.', labels[Math.Max(0, labels.Length - suffix - 1)..]);
    }
    public bool? IsThirdParty(string requestUrl, string? topUrl)
    {
        var host = Host(requestUrl); var top = Host(topUrl);
        return host is null || top is null ? null : Site(host) != Site(top);
    }
}
