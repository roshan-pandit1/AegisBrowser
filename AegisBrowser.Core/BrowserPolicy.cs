namespace AegisBrowser.Core;

public static class BrowserPolicy
{
    public const string DiagnosticsUrl = "https://aegis-diagnostics.invalid/";

    public static string ReadProfile(string[] args)
    {
        if (args.Length == 0) return "default";
        if (args.Length != 1 || !args[0].StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Usage: AegisBrowser.exe [--profile=NAME]");
        var name = args[0][10..];
        if (name.Length is < 1 or > 40 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException("Profile names require 1–40 ASCII letters, digits, hyphens or underscores.");
        return name.ToLowerInvariant();
    }

    public static string ProfilePath(string root, string profile) =>
        Path.Combine(root, "profile-" + ReadProfile(["--profile=" + profile]));

    public static bool IsWebUrl(string value) =>
        !string.IsNullOrEmpty(value) && !value.Any(char.IsControl) && !value.Contains('\\')
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
        && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);

    // Compare parsed origins, never suffixes or display strings; ports remain significant.
    public static string? PermissionOrigin(string value)
    {
        if (!IsWebUrl(value)) return null;
        var uri = new Uri(value);
        if (uri.Scheme != "https" && !uri.IsLoopback) return null;
        if (uri.Host.Equals("aegis-diagnostics.invalid", StringComparison.OrdinalIgnoreCase)) return null;
        var host = SiteClassifier.Host(value);
        return host is null ? null : new UriBuilder(uri.Scheme, host, uri.IsDefaultPort ? -1 : uri.Port).Uri.GetLeftPart(UriPartial.Authority);
    }

    public static bool CanPromptPermission(string requestingUrl, string topLevelUrl)
    {
        var origin = PermissionOrigin(requestingUrl);
        return origin is not null && origin == PermissionOrigin(topLevelUrl);
    }

    public static string DisplayOrigin(string url) =>
        IsWebUrl(url) ? new Uri(url).GetLeftPart(UriPartial.Authority) : "unavailable origin";

    public static bool CanServeDiagnostics(string url, string method, bool mainFrame, bool mainResource) =>
        mainFrame && mainResource && method == "GET" && IsWebUrl(url)
        && new Uri(url) is { Scheme: "https", IdnHost: "aegis-diagnostics.invalid", IsDefaultPort: true,
            AbsolutePath: "/", Query: "" };

    public static string? NormalizeAddress(string input)
    {
        input = input.Trim();
        if (input.Any(char.IsWhiteSpace) || input.Length == 0) return null;
        if (input.Equals("aegis:diagnostics", StringComparison.OrdinalIgnoreCase)) return DiagnosticsUrl;
        if (IsWebUrl(input)) return new Uri(input).AbsoluteUri;
        if (input.Contains("://") || input.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return null;
        return IsWebUrl("https://" + input) ? new Uri("https://" + input).AbsoluteUri : null;
    }
}

public sealed class ProfileLease : IDisposable
{
    private readonly FileStream stream;
    public ProfileLease(string path)
    {
        Directory.CreateDirectory(path);
        // The OS releases the exclusive handle even following a crash. Do not delete the lock file.
        stream = new FileStream(Path.Combine(path, ".profile.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }
    public void Dispose() => stream.Dispose();
}
