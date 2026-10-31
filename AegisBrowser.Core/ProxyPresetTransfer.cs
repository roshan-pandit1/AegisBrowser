using System.Text.Json;

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
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PortableProxyPresetEnvelope(content, ProxyPortableFiles.Hash(content)),
            new JsonSerializerOptions { WriteIndented = true });
        ProxyPortableFiles.Publish(rootPath, destination, bytes, ".aegis-proxy-preset-");
    }

    public PortableProxyPreset Preview(string source)
    {
        try
        {
            var bytes = ProxyPortableFiles.Read(source);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            ProfileTransfer.CheckDuplicateProperties(json.RootElement);
            var envelope = JsonSerializer.Deserialize<PortableProxyPresetEnvelope>(bytes, ProxyPortableFiles.StrictJson) ?? throw new IOException("The proxy preset file is empty.");
            Validate(envelope.Content);
            ProxyPortableFiles.ValidateChecksum(envelope.Content, envelope.Sha256);
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

}
