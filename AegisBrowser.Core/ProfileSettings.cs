namespace AegisBrowser.Core;

/// <summary>Only explicitly selected Aegis settings; never Chromium data.</summary>
public sealed record ProfileSettingsSnapshot(PrivacyConfiguration? Privacy, SearchSettings? Search);

public static class ProfileSettings
{
    public static ProfileSettingsSnapshot ReadClosed(string root, string slug, bool privacy, bool search)
    {
        slug = BrowserPolicy.ReadProfile(["--profile=" + slug]);
        ProfileRepository.CheckSafeProfilePath(root, slug);
        var path = BrowserPolicy.ProfilePath(root, slug);
        if (!Directory.Exists(path)) throw new IOException("The source profile directory does not exist.");
        if (!privacy && !search) return new(null, null);
        // The lease prevents a cooperating browser from changing settings during capture.
        using var lease = new ProfileLease(path);
        var privacyFile = Path.Combine(path, "privacy.json");
        var searchFile = Path.Combine(path, "search.json");
        var value = new ProfileSettingsSnapshot(privacy && IsRegularFile(privacyFile) ? new PrivacySettingsStore(path).Load() : null,
            search && IsRegularFile(searchFile) ? new SearchSettingsStore(path).Load() : null);
        Validate(value);
        return value;
    }

    public static void Validate(ProfileSettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Privacy is not null)
        {
            PrivacySettingsStore.Validate(settings.Privacy);
            _ = new RequestFilter(settings.Privacy);
        }
        if (settings.Search is not null) _ = SearchEngines.Get(settings.Search.EngineId);
    }

    internal static void Write(string directory, ProfileSettingsSnapshot settings)
    {
        Validate(settings);
        if (settings.Privacy is not null) new PrivacySettingsStore(directory).Save(settings.Privacy);
        if (settings.Search is not null) new SearchSettingsStore(directory).Save(settings.Search);
    }

    private static bool IsRegularFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("Linked or non-file settings paths are not supported.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
