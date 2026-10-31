using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AegisBrowser.Core;

/// <summary>A portable endpoint preset, with no profile identity or credential fields.</summary>
public sealed record PortableProxyPreset(int Version, DateTimeOffset ExportedUtc, string Name, string Description,
    ProxyProtocol Protocol, string Host, int Port, string Label)
{
    public override string ToString() => $"Portable proxy preset (version {Version})";
}

public sealed record PortableProxyPresetEnvelope(PortableProxyPreset Content, string Sha256)
{
    public override string ToString() => "Portable proxy preset envelope";
}

public interface IProxyPresetTransfer
{
    void Export(string destination, ProxyPreset expected);
    PortableProxyPreset Preview(string source);
    ProxyPreset Import(PortableProxyPreset preview);
}

/// <summary>Transfers inactive, non-secret endpoint presets; never changes a profile.</summary>
public sealed class ProxyPresetTransfer(string root, IProxyPresetRepository repository) : IProxyPresetTransfer
{
    public const int CurrentVersion = 1;
    private const int MaxBytes = 16_384;
    private static readonly JsonSerializerOptions StrictJson = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    private readonly string rootPath = Path.GetFullPath(root);

    public void Export(string destination, ProxyPreset expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ProxyPresetRepository.Validate(expected);
        var current = repository.List().SingleOrDefault(item => item.Id == expected.Id);
        if (current is null || current != expected) throw new IOException("Proxy preset changed; refresh before exporting.");
        var settings = current.Settings;
        var content = new PortableProxyPreset(CurrentVersion, DateTimeOffset.UtcNow, current.Name, current.Description,
            settings.Protocol, settings.Host, settings.Port, settings.Label);
        Validate(content);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PortableProxyPresetEnvelope(content, Hash(content)),
            new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > MaxBytes) throw new IOException("The proxy preset export exceeds the size limit.");
        RejectLinkedPath(rootPath);
        var target = Path.GetFullPath(destination);
        var relative = Path.GetRelativePath(rootPath, target);
        if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            throw new IOException("Choose an export location outside the profile storage directory.");
        var directory = Path.GetDirectoryName(target) ?? throw new IOException("An export directory is required.");
        RejectLinkedPath(directory); RejectLinkedPath(target);
        var temporary = Path.Combine(directory, ".aegis-proxy-preset-" + Guid.NewGuid().ToString("N") + ".tmp");
        var owns = false;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { owns = true; file.Write(bytes); file.Flush(true); }
            RejectLinkedPath(directory); RejectLinkedPath(target);
            File.Move(temporary, target); // Never replace an existing file.
        }
        finally
        {
            if (owns)
            {
                RejectLinkedPath(directory); RejectLinkedPath(temporary);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    public PortableProxyPreset Preview(string source)
    {
        var path = Path.GetFullPath(source); RejectLinkedPath(path);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 1 or > MaxBytes) throw new IOException("The proxy preset file is empty or too large.");
            var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            ProfileTransfer.CheckDuplicateProperties(json.RootElement);
            var envelope = JsonSerializer.Deserialize<PortableProxyPresetEnvelope>(bytes, StrictJson) ?? throw new IOException("The proxy preset file is empty.");
            Validate(envelope.Content);
            byte[] expected;
            try { expected = Convert.FromHexString(envelope.Sha256); }
            catch (Exception ex) when (ex is FormatException or ArgumentNullException) { throw new IOException("The proxy preset checksum is invalid.", ex); }
            var actual = Convert.FromHexString(Hash(envelope.Content));
            if (expected.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(expected, actual))
                throw new IOException("The proxy preset checksum does not match.");
            return envelope.Content;
        }
        catch (JsonException ex) { throw new IOException("The proxy preset is malformed or contains unsupported fields.", ex); }
    }

    public ProxyPreset Import(PortableProxyPreset preview)
    {
        Validate(preview);
        var settings = new ProxyDraft(preview.Protocol, preview.Host, preview.Port, false, null, preview.Label, true);
        // Repository.Create performs duplicate-name checks and allocates a new ID.
        return repository.Create(preview.Name, preview.Description, settings);
    }

    private static void Validate(PortableProxyPreset content)
    {
        if (content is null || content.Version != CurrentVersion || content.ExportedUtc == default || content.ExportedUtc.Offset != TimeSpan.Zero)
            throw new IOException("The proxy preset version or timestamp is unsupported.");
        try
        {
            var draft = new ProxyDraft(content.Protocol, content.Host, content.Port, false, null, content.Label, true);
            ProxyPresetRepository.Validate(new ProxyPreset(Guid.NewGuid(), ProxyPresetRepository.CurrentVersion, 1,
                content.Name, content.Description, draft, content.ExportedUtc, content.ExportedUtc));
        }
        catch (ArgumentException ex) { throw new IOException("The proxy preset contains invalid endpoint or label data.", ex); }
    }

    private static string Hash(PortableProxyPreset content) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content))).ToLowerInvariant();

    private static void RejectLinkedPath(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            ProxyRepository.RejectLink(current);
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
            current = parent;
        }
    }
}
