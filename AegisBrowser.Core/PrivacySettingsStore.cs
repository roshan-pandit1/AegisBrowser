using System.Text.Json;

namespace AegisBrowser.Core;

/// <summary>Profile-local persistence. Corrupt input is preserved for manual repair.</summary>
public sealed class PrivacySettingsStore(string profileDirectory)
{
    private readonly string path = Path.Combine(profileDirectory, "privacy.json");
    private bool loadFailed;
    public PrivacyConfiguration Load()
    {
        try
        {
            if (!File.Exists(path)) return new();
            if (new FileInfo(path).Length > 64_000_000) throw new InvalidDataException("Privacy settings exceed 64 MB.");
            var configuration = JsonSerializer.Deserialize<PrivacyConfiguration>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Privacy settings are empty.");
            Validate(configuration);
            return configuration;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            loadFailed = true;
            throw new IOException($"Could not load {path}. Defaults are active. The original file is preserved; repair or rename it and restart before saving settings.", ex);
        }
    }
    internal static void Validate(PrivacyConfiguration configuration)
    {
        if (configuration.FilterList is null || configuration.UserRules is null || configuration.BlockedDomains is null
            || configuration.AllowedDomains is null || configuration.SiteExceptions is null)
            throw new ArgumentException("Privacy settings cannot contain null collections or text.");
        if (configuration.BlockedDomains.Length + configuration.AllowedDomains.Length + configuration.SiteExceptions.Length > 20_000)
            throw new ArgumentException("Too many domain rules (maximum 20,000).");
        if (configuration.FilterList.Length + configuration.UserRules.Length > 8_000_000)
            throw new ArgumentException("Combined lists exceed 8 million characters.");
        if (configuration.BlockedDomains.Concat(configuration.AllowedDomains).Concat(configuration.SiteExceptions).Any(d => d is null))
            throw new ArgumentException("Domain rules cannot be null.");
        if (!Enum.IsDefined(configuration.Mode)) throw new ArgumentException("Unknown privacy mode.");
        foreach (var domain in configuration.BlockedDomains.Concat(configuration.AllowedDomains).Concat(configuration.SiteExceptions))
            NetworkFilterList.ValidateDomain(domain);
    }
    public void Save(PrivacyConfiguration configuration)
    {
        if (loadFailed) throw new IOException("The original privacy settings could not be loaded. Repair or rename privacy.json and restart before saving.");
        Validate(configuration);
        Directory.CreateDirectory(profileDirectory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, configuration, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
