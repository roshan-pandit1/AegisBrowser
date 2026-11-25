namespace AegisBrowser.Core;

/// <summary>Conservative preflight only; this cannot reserve a CEF path atomically.</summary>
public static class DownloadDestinations
{
    public static bool IsAvailable(string? approvedDirectory, string? destination)
    {
        if (string.IsNullOrEmpty(approvedDirectory) || string.IsNullOrEmpty(destination)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(approvedDirectory) || !Path.IsPathFullyQualified(destination)) return false;
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(approvedDirectory));
            var target = Path.GetFullPath(destination);
            if (target.Length > 240 || !string.Equals(Path.GetDirectoryName(target), folder, StringComparison.OrdinalIgnoreCase)) return false;
            var fileName = Path.GetFileName(target);
            if (!DownloadFileNames.TrySanitize(fileName, out var safeName) || fileName != safeName) return false;
            for (var current = new DirectoryInfo(folder); current is not null; current = current.Parent)
            {
                var attrs = File.GetAttributes(current.FullName);
                if ((attrs & FileAttributes.Directory) == 0 || (attrs & FileAttributes.ReparsePoint) != 0) return false;
            }
            try { _ = File.GetAttributes(target); return false; }
            catch (FileNotFoundException) { return true; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }
}
