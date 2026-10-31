namespace AegisBrowser.Core;

public interface IProxySavedDiagnostics
{
    Task<ProxyTransportResult> TestAsync(ProfileDefinition profile, ProxyConfiguration expected, string endpoint, CancellationToken cancellation);
}

/// <summary>Tests a saved revision while holding the existing closed-profile lease.</summary>
public sealed class ProxySavedDiagnostics(string root, IProxyRepository repository, IProxyCredentialVault vault, IProxyTransportTester tester)
    : IProxySavedDiagnostics
{
    private readonly string rootPath = Path.GetFullPath(root);

    public async Task<ProxyTransportResult> TestAsync(ProfileDefinition profile, ProxyConfiguration expected, string endpoint, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(expected);
        _ = ProxyTransportTester.ValidateEndpoint(endpoint);
        cancellation.ThrowIfCancellationRequested();
        if (expected.ProfileId != profile.Id) throw new ArgumentException("The saved proxy belongs to a different profile.");
        ProfileRepository.CheckSafeProfilePath(rootPath, profile.Slug);
        var path = BrowserPolicy.ProfilePath(rootPath, profile.Slug);
        if (!Directory.Exists(path)) throw new IOException("The profile directory is missing.");
        ProfileLease lease;
        try { lease = new ProfileLease(path); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        { throw new IOException("Close this profile before testing its saved proxy draft.", ex); }
        using (lease)
        {
            cancellation.ThrowIfCancellationRequested();
            var current = repository.Get(profile, lease);
            if (current is null || current != expected) throw new IOException("Proxy settings changed; reopen the editor before testing.");
            ProxyCredentials? credentials = null;
            if (current.Settings.AuthenticationRequired && current.Settings.Protocol != ProxyProtocol.Socks5)
            {
                try { credentials = vault.Read(profile.Id, current.Settings.CredentialId!.Value); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return new(current.Settings.Protocol, ProxyTransportOutcome.MissingCredentials, DateTimeOffset.UtcNow, TimeSpan.Zero,
                        null, "The saved proxy credentials are missing or inaccessible.");
                }
            }
            cancellation.ThrowIfCancellationRequested();
            return await tester.TestAsync(current.Settings, credentials, endpoint, cancellation).ConfigureAwait(false);
        }
    }
}
