namespace AegisBrowser.Core;

/// <summary>
/// Observational states for a future OS containment controller. A reported
/// state alone is not evidence of packet enforcement or permission to browse.
/// </summary>
public enum ProxyContainmentState
{
    NotConfigured,
    Installing,
    ActiveUnverified,
    Verified,
    Failed,
    Lost
}

/// <summary>
/// A policy scope must bind the selected profile, saved configuration revision,
/// and actual approved proxy endpoint. Implementations must separately identify
/// the browser and all network-capable subprocess generations with retained OS
/// handles; a PID or hostname alone is not an enforcement identity.
/// </summary>
public sealed record ProxyContainmentScope(Guid ProfileId, long ProxyRevision,
    Guid PolicyId, string ProxyHost, int ProxyPort);

/// <summary>
/// Contract for a future independently reviewed OS containment service. No
/// implementation is supplied. State is informational; even a provider's
/// Verified value is not itself authority to start protected browsing. Future
/// integration must establish scoped process coverage, IPv4/IPv6, TCP/UDP,
/// DNS and WebRTC enforcement, policy lifetime, loss handling and cleanup using
/// independently corroborated OS evidence before enabling any startup path.
/// </summary>
public interface IProxyContainmentLifetime : IDisposable
{
    ProxyContainmentScope Scope { get; }
    ProxyContainmentState State { get; }
    event EventHandler? PolicyLost;
    Task<ProxyContainmentState> RevalidateAsync(CancellationToken cancellation);
}

/// <summary>
/// Future installation boundary. Installation must complete before CEF starts;
/// success of this method is not proof that OS policy enforced any traffic.
/// The lifetime owns loss notification and cleanup across service or process
/// restarts. This interface has no implementation or startup integration.
/// </summary>
public interface IProxyContainmentController
{
    Task<IProxyContainmentLifetime> InstallAsync(ProxyContainmentScope scope, CancellationToken cancellation);
}

/// <summary>
/// Non-secret, preparation-only result. It is not an authorization token and
/// cannot start CEF, create a request context or permit protected browsing.
/// It has no containment provider or switch that can change that decision.
/// </summary>
public sealed class ProtectedProxyPreparation
{
    private ProtectedProxyPreparation(Guid profileId, long proxyRevision)
    {
        ProfileId = profileId;
        ProxyRevision = proxyRevision;
    }

    public Guid ProfileId { get; }
    public long ProxyRevision { get; }
    public ProxyContainmentState ContainmentState => ProxyContainmentState.NotConfigured;
    public bool CanStartProtectedBrowsing => false;
    public string BlockingReason => "Required proxy browsing is unavailable until OS network containment is independently verified.";

    /// <summary>
    /// Validates existing local configuration under the caller's live profile
    /// lease. No browser is initialized and no network or policy operation is
    /// performed. The caller owns the lease and must retain it for any future
    /// operation. Re-run preparation after any configuration or lease change.
    /// </summary>
    public static ProtectedProxyPreparation Prepare(string root, ProfileDefinition profile, ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(lease);
        var rootPath = Path.GetFullPath(root);
        var profilePath = BrowserPolicy.ProfilePath(rootPath, profile.Slug);
        ProfileRepository.CheckSafeProfilePath(rootPath, profile.Slug);
        if (!lease.IsHeldFor(profilePath)) throw new IOException("A matching live profile lease is required.");
        ProfileSettingsTransactions.EnsureNoPending(rootPath, profile.Slug);
        _ = new PrivacySettingsStore(profilePath, requireExistingDirectory: true).Load();

        var repository = new ProxyRepository(rootPath);
        var configuration = repository.Get(profile, lease)
            ?? throw new IOException("A saved required proxy configuration is needed.");
        if (configuration.ProfileId != profile.Id || configuration.Settings.Protocol == ProxyProtocol.Direct)
            throw new IOException("A saved required proxy configuration is needed.");
        // Validation and supported transport checks run before any CEF action.
        _ = ChromiumProxyPreference.Create(configuration.Settings);
        if (configuration.Settings.AuthenticationRequired)
        {
            var credentialId = configuration.Settings.CredentialId
                ?? throw new IOException("Required proxy credentials are unavailable.");
            // Validate profile-bound DPAPI access without retaining or returning
            // the secret. Managed strings cannot be guaranteed erased from memory.
            _ = new ProxyCredentialVault(rootPath).Read(profile.Id, credentialId);
        }
        // Revalidate the profile lease and saved configuration before publishing
        // even this non-authorizing result; neither check replaces containment.
        var after = repository.Get(profile, lease);
        if (after != configuration) throw new IOException("The proxy configuration changed during preparation.");
        return new ProtectedProxyPreparation(profile.Id, configuration.Revision);
    }
}
