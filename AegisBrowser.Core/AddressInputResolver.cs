using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace AegisBrowser.Core;

public sealed record SearchEngine(string Id, string Name, string Template)
{
    public string Search(string terms) => Template.Replace("{searchTerms}", Uri.EscapeDataString(terms));
    public override string ToString() => Name;
}

public static class SearchEngines
{
    public const string DefaultId = "duckduckgo";
    public static IReadOnlyList<SearchEngine> All { get; } = Array.AsReadOnly(new[] {
        new SearchEngine(DefaultId, "DuckDuckGo", "https://duckduckgo.com/?q={searchTerms}"),
        new SearchEngine("google", "Google", "https://www.google.com/search?q={searchTerms}"),
        new SearchEngine("bing", "Bing", "https://www.bing.com/search?q={searchTerms}"),
        new SearchEngine("brave", "Brave Search", "https://search.brave.com/search?q={searchTerms}")
    });
    public static SearchEngine Get(string id) => All.FirstOrDefault(e => e.Id == id)
        ?? throw new ArgumentException("Unknown search engine.");
}

public enum AddressInputKind { Empty, Navigation, Search, Rejected }
public sealed record AddressResolution(AddressInputKind Kind, string? Url = null);

// Pure resolution only: no DNS, network, suggestions or prefetch while typing.
public static class AddressInputResolver
{
    public static AddressResolution Resolve(string? input, string engineId = SearchEngines.DefaultId)
    {
        var engine = SearchEngines.Get(engineId);
        var value = input?.Trim() ?? "";
        if (value.Length == 0) return new(AddressInputKind.Empty);
        if (value.Any(char.IsControl) || value.Contains('\\')) return new(AddressInputKind.Rejected);
        if (value.Equals("aegis:diagnostics", StringComparison.OrdinalIgnoreCase))
            return new(AddressInputKind.Navigation, BrowserPolicy.DiagnosticsUrl);
        if (value.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            return new(AddressInputKind.Navigation, "about:blank");
        if (value.StartsWith("https:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("http:", StringComparison.OrdinalIgnoreCase))
            return Navigate(value);

        // Recognize host:port before scheme detection. Unbracketed IPv6 is a
        // host only; an IPv6 port must use [address]:port to avoid ambiguity.
        if (IPAddress.TryParse(value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return Navigate((IPAddress.IsLoopback(ip) ? "http://[" : "https://[") + value + "]");
        var authority = value.Split('/', '?', '#')[0];
        if (!authority.Any(char.IsWhiteSpace) && !authority.Contains('@') &&
            Uri.TryCreate("https://" + value, UriKind.Absolute, out var candidate) && IsHost(candidate))
        {
            var local = candidate.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || candidate.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(candidate.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
            return Navigate((local ? "http://" : "https://") + value);
        }
        if (Regex.IsMatch(value, @"^[a-zA-Z][a-zA-Z0-9+.-]*:") || value.StartsWith("//")
            || value.StartsWith('[') || (!value.Any(char.IsWhiteSpace) && value.Contains('@')))
            return new(AddressInputKind.Rejected);
        return new(AddressInputKind.Search, engine.Search(value));
    }

    private static bool IsHost(Uri uri)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6) return true;
        var host = uri.IdnHost.TrimEnd('.');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (host.Contains('.') && ValidDns(host));
    }

    private static bool ValidDns(string host) => host.Length <= 253 && host.Split('.').All(label =>
        label.Length is > 0 and <= 63 && char.IsAsciiLetterOrDigit(label[0]) && char.IsAsciiLetterOrDigit(label[^1])
        && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));

    private static AddressResolution Navigate(string value)
    {
        try
        {
            if (!BrowserPolicy.IsWebUrl(value)) return new(AddressInputKind.Rejected);
            var uri = new Uri(value);
            if (uri.HostNameType == UriHostNameType.Dns && !ValidDns(uri.IdnHost.TrimEnd('.')))
                return new(AddressInputKind.Rejected);
            // Uri canonicalizes Unicode paths and spaces, preserving valid % escapes.
            var builder = new UriBuilder(uri) { Host = uri.IdnHost };
            return new(AddressInputKind.Navigation, builder.Uri.AbsoluteUri);
        }
        catch (ArgumentException) { return new(AddressInputKind.Rejected); }
        catch (UriFormatException) { return new(AddressInputKind.Rejected); }
    }
}
