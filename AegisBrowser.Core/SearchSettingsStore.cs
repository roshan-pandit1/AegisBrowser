using System.Text.Json;

namespace AegisBrowser.Core;

public sealed record SearchSettings(string EngineId = SearchEngines.DefaultId);

// Same profile-local, atomic-replacement and corrupt-file preservation policy as privacy.json.
public sealed class SearchSettingsStore(string profileDirectory)
{
    private readonly string path = Path.Combine(profileDirectory, "search.json");
    private bool loadFailed;
    public SearchSettings Load()
    {
        try
        {
            if (!File.Exists(path)) return new();
            if (new FileInfo(path).Length > 4096) throw new IOException("Search settings exceed 4 KB.");
            var settings = JsonSerializer.Deserialize<SearchSettings>(File.ReadAllText(path))
                ?? throw new ArgumentException("Empty search settings.");
            _ = SearchEngines.Get(settings.EngineId);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            loadFailed = true;
            throw new IOException("Could not load search.json. DuckDuckGo is active. Repair or rename the original file and restart before saving.", ex);
        }
    }
    public void Save(SearchSettings settings)
    {
        if (loadFailed) throw new IOException("Repair or rename search.json and restart before saving.");
        _ = SearchEngines.Get(settings.EngineId);
        Directory.CreateDirectory(profileDirectory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings); stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
