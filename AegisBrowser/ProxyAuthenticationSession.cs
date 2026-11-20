using AegisBrowser.Core;
using CefSharp;

namespace AegisBrowser;

/// <summary>
/// Experimental, profile-bound Basic HTTP proxy authentication. This does not
/// configure Chromium, establish network containment, or permit profile startup.
/// Basic authentication to an HTTP proxy is not encrypted by this component.
/// The conservative two-challenge budget is per session, not per request; it
/// can reject legitimate later or concurrent challenges and must be redesigned
/// before protected browsing is enabled.
/// </summary>
internal sealed class ProxyAuthenticationSession : IDisposable
{
    private const int MaxChallenges = 2;
    private readonly object sync = new();
    private readonly ProxyDraft settings;
    private ProxyCredentials? credentials;
    private int issued;

    private ProxyAuthenticationSession(ProxyDraft settings, ProxyCredentials credentials)
    {
        ProxyValidation.Validate(settings);
        if (settings.Protocol != ProxyProtocol.Http || !settings.AuthenticationRequired)
            throw new NotSupportedException("Only scoped Basic HTTP proxy authentication is supported in this experiment.");
        this.settings = settings;
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    // The repository verifies that this lease belongs to the selected profile.
    // The vault applies its existing profile/credential-ID DPAPI binding.
    internal static ProxyAuthenticationSession FromProfile(ProfileDefinition profile, ProfileLease lease,
        IProxyRepository repository, IProxyCredentialVault vault)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(vault);
        var configuration = repository.Get(profile, lease)
            ?? throw new IOException("A saved proxy configuration is required.");
        if (configuration.ProfileId != profile.Id)
            throw new IOException("The saved proxy belongs to a different profile.");
        var draft = configuration.Settings;
        ProxyValidation.Validate(draft);
        if (draft.Protocol != ProxyProtocol.Http || !draft.AuthenticationRequired || draft.CredentialId is not Guid credentialId)
            throw new NotSupportedException("This profile does not have supported HTTP proxy authentication.");
        return new ProxyAuthenticationSession(draft, vault.Read(profile.Id, credentialId));
    }

    // Used only by the disposable CEF fixture with synthetic credentials.
    internal static ProxyAuthenticationSession ForFixture(ProxyDraft draft, ProxyCredentials credentials) => new(draft, credentials);

    internal int Issued { get { lock (sync) return issued; } }

    // The callback is supplied on CEF's IO thread. It is completed and disposed
    // exactly once. Locking prevents Dispose from racing a successful response.
    internal bool Respond(bool isProxy, string? host, int port, string? scheme, IAuthCallback callback)
    {
        using (callback)
        {
            lock (sync)
            {
                try
                {
                    if (callback.IsDisposed) return true;
                    if (credentials is null || issued >= MaxChallenges ||
                        !ProxyAuthenticationScope.Allows(settings, isProxy, host, port, scheme))
                    {
                        callback.Cancel();
                        return true;
                    }
                    issued++;
                    callback.Continue(credentials.Username, credentials.Password);
                    return true;
                }
                catch (ObjectDisposedException) { return true; }
            }
        }
    }

    public void Dispose()
    {
        lock (sync) credentials = null;
        // ProxyCredentials contains managed strings: dropping references does
        // not guarantee the runtime has erased existing copies from memory.
    }
}
