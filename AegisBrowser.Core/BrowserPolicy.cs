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
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
        && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);

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
