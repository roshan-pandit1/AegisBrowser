namespace AegisBrowser.Core;

public interface IProxyConfigurationEditor
{
    ProxyConfiguration? Load(ProfileDefinition profile);
    ProxySaveResult Save(ProfileDefinition profile, ProxyDraft draft, long expectedRevision, ProxyCredentials? replacement);
}

/// <summary>Acquires the existing profile lease for each configuration operation.</summary>
public sealed class ProxyConfigurationEditor : IProxyConfigurationEditor
{
    private readonly string root;
    private readonly IProxyRepository repository;
    private readonly ProxyConfigurationService service;

    public ProxyConfigurationEditor(string root, IProxyRepository repository, IProxyCredentialVault vault)
    {
        this.root = Path.GetFullPath(root);
        this.repository = repository;
        service = new ProxyConfigurationService(repository, vault);
    }

    public ProxyConfiguration? Load(ProfileDefinition profile) => WithLease(profile, lease => repository.Get(profile, lease));

    public ProxySaveResult Save(ProfileDefinition profile, ProxyDraft draft, long expectedRevision, ProxyCredentials? replacement) =>
        WithLease(profile, lease => service.Save(profile, lease, draft, expectedRevision, replacement));

    private T WithLease<T>(ProfileDefinition profile, Func<ProfileLease, T> action)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ProfileRepository.CheckSafeProfilePath(root, profile.Slug);
        var path = BrowserPolicy.ProfilePath(root, profile.Slug);
        if (!Directory.Exists(path)) throw new IOException("The profile directory is missing. Restore it before changing proxy settings.");
        ProfileLease lease;
        try { lease = new ProfileLease(path); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        { throw new IOException("Close this profile before viewing or changing its proxy settings.", ex); }
        using (lease)
        return action(lease);
    }
}
