using System.Text.Json;
using System.Text.Json.Serialization;

namespace AegisBrowser.Core;

/// <summary>Reusable endpoint settings without credentials or browser data.</summary>
public sealed record ProxyPreset(Guid Id, int Version, long Revision, string Name, string Description,
    ProxyDraft Settings, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc)
{
    public override string ToString() => $"Proxy preset {Id:D} (revision {Revision})";
}

public interface IProxyPresetRepository
{
    IReadOnlyList<ProxyPreset> List();
    ProxyPreset Create(string name, string description, ProxyDraft settings);
    ProxyPreset Update(ProxyPreset candidate, long expectedRevision);
    void Delete(Guid id, long expectedRevision);
}

/// <summary>Versioned, revision-checked app-owned presets; no credential references.</summary>
public sealed class ProxyPresetRepository : IProxyPresetRepository
{
    public const int CurrentVersion = 1;
    public const string DirectoryName = "proxy-presets";
    private const int MaxBytes = 16_384;
    private const int MaxPresets = 1000;
    private static readonly JsonSerializerOptions StrictJson = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16, WriteIndented = true };
    private readonly string root;
    private string DirectoryPath => Path.Combine(root, DirectoryName);

    public ProxyPresetRepository(string root) => this.root = Path.GetFullPath(root);

    public IReadOnlyList<ProxyPreset> List()
    {
        ProxyRepository.RejectLink(root); ProxyRepository.RejectLink(DirectoryPath);
        if (ProxyRepository.Exists(root) && !Directory.Exists(root)) throw new IOException("Proxy preset root is not a directory.");
        if (ProxyRepository.Exists(DirectoryPath) && !Directory.Exists(DirectoryPath)) throw new IOException("Proxy preset storage is not a directory.");
        if (!Directory.Exists(DirectoryPath)) return [];
        return WithLock(false, ReadAll);
    }

    public ProxyPreset Create(string name, string description, ProxyDraft settings)
    {
        var now = DateTimeOffset.UtcNow;
        var candidate = new ProxyPreset(Guid.NewGuid(), CurrentVersion, 1, name, description, settings, now, now);
        Validate(candidate);
        return WithLock(true, () =>
        {
            var existing = ReadAll();
            if (existing.Count >= MaxPresets) throw new IOException("The proxy preset limit has been reached.");
            EnsureUniqueName(existing, candidate);
            Write(candidate, false);
            return candidate;
        });
    }

    public ProxyPreset Update(ProxyPreset candidate, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(candidate); Validate(candidate);
        if (expectedRevision != candidate.Revision || expectedRevision is < 1 or long.MaxValue)
            throw new ArgumentException("An exact current preset revision is required.");
        return WithLock(false, () =>
        {
            var all = ReadAll();
            var current = all.SingleOrDefault(item => item.Id == candidate.Id) ?? throw new IOException("Proxy preset was not found.");
            if (current.Revision != expectedRevision || current.CreatedUtc != candidate.CreatedUtc)
                throw new IOException("Proxy preset changed; reload it before saving.");
            EnsureUniqueName(all, candidate);
            var now = DateTimeOffset.UtcNow; if (now < current.UpdatedUtc) now = current.UpdatedUtc;
            var updated = candidate with { Revision = checked(expectedRevision + 1), UpdatedUtc = now };
            Validate(updated); Write(updated, true); return updated;
        });
    }

    public void Delete(Guid id, long expectedRevision)
    {
        if (id == Guid.Empty || expectedRevision < 1) throw new ArgumentException("A current preset identity and revision are required.");
        WithLock(false, () =>
        {
            var current = ReadAll().SingleOrDefault(item => item.Id == id) ?? throw new IOException("Proxy preset was not found.");
            if (current.Revision != expectedRevision) throw new IOException("Proxy preset changed; reload it before deleting.");
            var path = PathFor(id); ProxyRepository.RejectLink(path); File.Delete(path); return 0;
        });
    }

    public static void Validate(ProxyPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.Id == Guid.Empty || preset.Version != CurrentVersion || preset.Revision is < 1 or long.MaxValue ||
            preset.CreatedUtc == default || preset.CreatedUtc.Offset != TimeSpan.Zero || preset.UpdatedUtc.Offset != TimeSpan.Zero || preset.UpdatedUtc < preset.CreatedUtc)
            throw new ArgumentException("Invalid proxy preset identity, version or timestamps.");
        if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 120 || preset.Name != preset.Name.Trim() || preset.Name.Any(char.IsControl) ||
            preset.Description is null || preset.Description.Length > 1000 || preset.Description.Contains('\0'))
            throw new ArgumentException("Invalid proxy preset name or description.");
        ProxyValidation.Validate(preset.Settings);
        if (preset.Settings.Protocol == ProxyProtocol.Direct || preset.Settings.AuthenticationRequired || preset.Settings.CredentialId is not null)
            throw new ArgumentException("Proxy presets require a non-secret proxy endpoint and cannot contain credentials.");
    }

    private IReadOnlyList<ProxyPreset> ReadAll()
    {
        ProxyRepository.RejectLink(DirectoryPath);
        var paths = Directory.EnumerateFileSystemEntries(DirectoryPath, "proxy-preset-*.json").Take(MaxPresets + 1).ToArray();
        if (paths.Length > MaxPresets) throw new IOException("Too many proxy presets; existing files were preserved.");
        var presets = new List<ProxyPreset>(paths.Length); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            ProxyRepository.RejectLink(path);
            if (Directory.Exists(path)) throw new IOException("Proxy preset path is a directory; existing files were preserved.");
            var fileName = Path.GetFileName(path);
            var identifier = fileName["proxy-preset-".Length..^".json".Length];
            if (!Guid.TryParseExact(identifier, "D", out var id) || id == Guid.Empty || fileName != "proxy-preset-" + id.ToString("D") + ".json")
                throw new IOException("Proxy preset file name is invalid; existing files were preserved.");
            var preset = Read(path);
            if (preset.Id != id || !names.Add(preset.Name)) throw new IOException("Proxy preset identity or name is duplicated; existing files were preserved.");
            presets.Add(preset);
        }
        return presets.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Id).ToArray();
    }

    private static ProxyPreset Read(string path)
    {
        try
        {
            ProxyRepository.RejectLink(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 1 or > MaxBytes) throw new IOException("Proxy preset is empty or too large; the file was preserved.");
            var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            RejectDuplicateProperties(document.RootElement);
            var preset = JsonSerializer.Deserialize<ProxyPreset>(bytes, StrictJson) ?? throw new IOException("Proxy preset is empty.");
            Validate(preset); return preset;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or OverflowException)
        { throw new IOException("Proxy preset is invalid or unsupported; the file was preserved.", ex); }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new IOException("Proxy preset contains duplicate fields; the file was preserved.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }

    private void Write(ProxyPreset preset, bool replace)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(preset, StrictJson);
        if (bytes.Length > MaxBytes) throw new IOException("Proxy preset exceeds the size limit.");
        var destination = PathFor(preset.Id); ProxyRepository.RejectLink(destination);
        var temporary = Path.Combine(DirectoryPath, ".proxy-preset-" + Guid.NewGuid().ToString("N") + ".tmp");
        var owns = false;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { owns = true; file.Write(bytes); file.Flush(true); }
            File.Move(temporary, destination, replace);
        }
        finally { if (owns && File.Exists(temporary)) File.Delete(temporary); }
    }

    private T WithLock<T>(bool create, Func<T> operation)
    {
        ProxyRepository.RejectLink(root); ProxyRepository.RejectLink(DirectoryPath);
        if (create) { Directory.CreateDirectory(DirectoryPath); ProxyRepository.RejectLink(root); ProxyRepository.RejectLink(DirectoryPath); }
        if (!Directory.Exists(DirectoryPath)) throw new IOException("Proxy preset storage does not exist.");
        var path = Path.Combine(DirectoryPath, ".proxy-presets.lock"); ProxyRepository.RejectLink(path);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        FileStream gate;
        while (true)
        {
            try { gate = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && timer.ElapsedMilliseconds < 10_000)
            { Thread.Sleep(50); ProxyRepository.RejectLink(path); }
        }
        using (gate) return operation();
    }

    private static void EnsureUniqueName(IReadOnlyList<ProxyPreset> existing, ProxyPreset candidate)
    {
        if (existing.Any(item => item.Id != candidate.Id && string.Equals(item.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("A proxy preset with that name already exists.");
    }

    private string PathFor(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A nonempty preset identity is required.");
        return Path.Combine(DirectoryPath, "proxy-preset-" + id.ToString("D") + ".json");
    }
}
