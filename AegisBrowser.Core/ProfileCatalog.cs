namespace AegisBrowser.Core;

/// <summary>
/// Lists and creates profile directories without changing existing Chromium caches.
/// The physical directory name remains compatible with BrowserPolicy.ProfilePath.
/// </summary>
public sealed class ProfileCatalog
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AegisBrowser", "Profiles");

    private readonly string root;

    public ProfileCatalog(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A profile root is required.", nameof(root));
        this.root = Path.GetFullPath(root);
    }

    public IReadOnlyList<string> List()
    {
        if (!Directory.Exists(root)) return Array.Empty<string>();
        var profiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(root, "profile-*", SearchOption.TopDirectoryOnly))
        {
            // Never advertise a linked directory as an ordinary locally-managed profile.
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            var folder = Path.GetFileName(directory);
            if (!folder.StartsWith("profile-", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var normalized = BrowserPolicy.ReadProfile(["--profile=" + folder["profile-".Length..]]);
                profiles.Add(normalized);
            }
            catch (ArgumentException) { /* Ignore unrelated or invalid directory names. */ }
        }
        return profiles.ToArray();
    }

    public string Create(string name)
    {
        var normalized = BrowserPolicy.ReadProfile(["--profile=" + name]);
        Directory.CreateDirectory(root);
        if (List().Contains(normalized, StringComparer.OrdinalIgnoreCase))
            throw new IOException("That browser profile already exists.");

        // Moving a newly-created directory is atomic on this volume, so a second
        // concurrent creator cannot silently succeed or overwrite a profile.
        var staging = Path.Combine(root, ".creating-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            Directory.Move(staging, BrowserPolicy.ProfilePath(root, normalized));
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging);
        }
        return normalized;
    }
}
