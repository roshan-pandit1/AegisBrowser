using System.Diagnostics;

namespace AegisBrowser;

// Hosted smoke-test diagnostics. Only fixed event names and sanitized values
// are emitted. The browser never logs address text or changes navigation.
internal sealed class PackageSmokeTrace(string file, string fixtureUrl)
{
    private readonly object gate = new();
    private int remaining = 64;

    internal static PackageSmokeTrace? Create(string profile)
    {
        if (!profile.StartsWith("smoke-", StringComparison.Ordinal) ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            return null;
        var root = Environment.GetEnvironmentVariable("RUNNER_TEMP");
        var directory = Environment.GetEnvironmentVariable("AEGIS_PACKAGE_SMOKE_TRACE");
        var url = Environment.GetEnvironmentVariable("AEGIS_PACKAGE_SMOKE_URL");
        if (root is null || directory is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Host != "127.0.0.1" || uri.AbsolutePath != "/rc1-package-https" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return null;
        try
        {
            root = Path.GetFullPath(root);
            directory = Path.GetFullPath(directory);
            if (!directory.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(directory).StartsWith("aegis-rc1-https-", StringComparison.Ordinal) ||
                 !string.Equals(Path.GetDirectoryName(directory), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                return null;
            var trace = new PackageSmokeTrace(Path.Combine(directory, "browser.trace"), uri.AbsoluteUri);
            trace.Record("observer_ready");
            return trace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return null; }
    }

    internal bool Matches(string? url) => string.Equals(url, fixtureUrl, StringComparison.Ordinal);

    internal void Record(string stage, string detail = "none")
    {
        lock (gate)
        {
            if (remaining <= 0) return;
            remaining--;
            try
            {
                File.AppendAllText(file, $"PACKAGE_HTTPS qpc={Stopwatch.GetTimestamp()} source=browser stage={stage} detail={detail}\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
