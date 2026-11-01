using System.Text.Json;

namespace AegisBrowser.Core;

/// <summary>Portable required-proxy intent. Credentials and profile identities are never included.</summary>
public sealed record PortableProxyConfiguration(string Kind, int Version, DateTimeOffset ExportedUtc,
    ProxyProtocol Protocol, string Host, int Port, string Label, bool AuthenticationRequired)
{
    public override string ToString() => $"Portable proxy configuration (version {Version})";
}

public sealed record PortableProxyConfigurationEnvelope(PortableProxyConfiguration Content, string Sha256)
{
    public override string ToString() => "Portable proxy configuration envelope";
}

public interface IProxyConfigurationTransfer
{
    void Export(string destination, ProfileDefinition profile, ProxyConfiguration expected);
    PortableProxyConfiguration Preview(string source);
}

/// <summary>Exports a closed profile's saved intent and validates imports without applying them.</summary>
public sealed class ProxyConfigurationTransfer(string root, IProxyRepository repository) : IProxyConfigurationTransfer
{
    public const int CurrentVersion = 1;
    public const string Kind = "aegis-proxy-configuration";
    private readonly string rootPath = Path.GetFullPath(root);

    public void Export(string destination, ProfileDefinition profile, ProxyConfiguration expected)
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(expected);
        if (profile.Id != expected.ProfileId) throw new ArgumentException("The saved proxy belongs to a different profile.");
        ProfileRepository.CheckSafeProfilePath(rootPath, profile.Slug);
        var profilePath = BrowserPolicy.ProfilePath(rootPath, profile.Slug);
        if (!Directory.Exists(profilePath)) throw new IOException("The profile directory is missing.");
        PortableProxyConfiguration content;
        ProfileLease lease;
        try { lease = new ProfileLease(profilePath); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        { throw new IOException("Close this profile before exporting its proxy configuration.", ex); }
        using (lease)
        {
            var current = repository.Get(profile, lease);
            if (current is null || current != expected) throw new IOException("Proxy settings changed; reopen the editor before exporting.");
            var settings = current.Settings;
            if (settings.Protocol == ProxyProtocol.Direct) throw new IOException("Only saved proxy endpoints can be exported.");
            content = new(Kind, CurrentVersion, DateTimeOffset.UtcNow, settings.Protocol, settings.Host, settings.Port,
                settings.Label, settings.AuthenticationRequired);
            Validate(content);
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new PortableProxyConfigurationEnvelope(content, ProxyPortableFiles.Hash(content)),
            new JsonSerializerOptions { WriteIndented = true });
        ProxyPortableFiles.Publish(rootPath, destination, bytes, ".aegis-proxy-config-");
    }

    public PortableProxyConfiguration Preview(string source)
    {
        try
        {
            var bytes = ProxyPortableFiles.Read(source);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            ProfileTransfer.CheckDuplicateProperties(json.RootElement);
            var envelope = JsonSerializer.Deserialize<PortableProxyConfigurationEnvelope>(bytes, ProxyPortableFiles.StrictJson)
                ?? throw new IOException("The proxy configuration file is empty.");
            Validate(envelope.Content);
            ProxyPortableFiles.ValidateChecksum(envelope.Content, envelope.Sha256);
            return envelope.Content;
        }
        catch (JsonException ex) { throw new IOException("The proxy configuration is malformed or contains unsupported fields.", ex); }
    }

    private static void Validate(PortableProxyConfiguration content)
    {
        if (content is null || content.Kind != Kind || content.Version != CurrentVersion || content.ExportedUtc == default ||
            content.ExportedUtc.Offset != TimeSpan.Zero || content.Protocol == ProxyProtocol.Direct)
            throw new IOException("The proxy configuration kind, version or timestamp is unsupported.");
        try
        {
            ProxyValidation.Validate(new ProxyDraft(content.Protocol, content.Host, content.Port, content.AuthenticationRequired,
                content.AuthenticationRequired ? Guid.NewGuid() : null, content.Label, true));
            if (content.Protocol == ProxyProtocol.Socks5 && content.AuthenticationRequired)
                throw new ArgumentException("SOCKS5 authentication is unsupported.");
        }
        catch (ArgumentException ex) { throw new IOException("The proxy configuration contains invalid or unsupported endpoint data.", ex); }
    }
}
