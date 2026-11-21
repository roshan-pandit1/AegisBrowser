using AegisBrowser.Core;
using CefSharp;

namespace AegisBrowser;

/// <summary>
/// Experimental, profile-bound Basic HTTP proxy authentication. This does not
/// configure Chromium, establish network containment, or permit profile startup.
/// Basic authentication to an HTTP proxy is not encrypted by this component.
/// CEF supplies no request or connection identity in this callback. This
/// session supports one explicitly isolated authentication attempt and makes
/// credentials available for at most one matching challenge. It cannot safely
/// attribute later challenges to a retry or to an unrelated request. General
/// concurrent or long-lived protected browsing remains unsupported.
/// </summary>
internal sealed class ProxyAuthenticationSession : IDisposable
{
    private readonly object sync = new();
    private readonly ProxyDraft settings;
    private readonly WeakReference<ProfileLease>? lease;
    private IDisposable? revocation;
    private ProxyCredentials? credentials;
    private bool credentialIssued;

    private ProxyAuthenticationSession(ProxyDraft settings, ProxyCredentials credentials, ProfileLease? lease = null)
    {
        ProxyValidation.Validate(settings);
        if (settings.Protocol != ProxyProtocol.Http || !settings.AuthenticationRequired)
            throw new NotSupportedException("Only scoped Basic HTTP proxy authentication is supported in this experiment.");
        this.settings = settings;
        this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        if (lease is not null)
        {
            this.lease = new WeakReference<ProfileLease>(lease);
            // The lease may revoke the session, but must not own it or be kept
            // alive solely by it. A live owner remains responsible for disposal.
            var weakSession = new WeakReference<ProxyAuthenticationSession>(this);
            revocation = lease.RegisterRevocation(() =>
            {
                if (weakSession.TryGetTarget(out var session)) session.Dispose();
            });
        }
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
        return new ProxyAuthenticationSession(draft, vault.Read(profile.Id, credentialId), lease);
    }

    // Used only by the disposable CEF fixture with synthetic credentials.
    internal static ProxyAuthenticationSession ForFixture(ProxyDraft draft, ProxyCredentials credentials) => new(draft, credentials);

    internal int Issued { get { lock (sync) return credentialIssued ? 1 : 0; } }

    // The callback is supplied on CEF's IO thread. It is completed and disposed
    // exactly once. Locking prevents Dispose from racing a successful response.
    internal bool Respond(bool isProxy, string? host, int port, string? scheme, IAuthCallback callback)
    {
        try
        {
            lock (sync)
            {
                if (callback.IsDisposed) return true;
                if (credentials is null ||
                    !ProxyAuthenticationScope.Allows(settings, isProxy, host, port, scheme))
                {
                    callback.Cancel();
                    return true;
                }
                void ContinueOnce()
                {
                    var oneShot = credentials!;
                    credentials = null;
                    credentialIssued = true;
                    callback.Continue(oneShot.Username, oneShot.Password);
                }
                // Keep the lease alive through the synchronous handoff and
                // serialize with lease release. CEF may still queue or cache
                // credentials after Continue; this cannot revoke that work.
                if (lease is null) ContinueOnce();
                else if (!lease.TryGetTarget(out var owner) || !owner.TryInvokeWhileHeld(ContinueOnce))
                {
                    credentials = null;
                    callback.Cancel();
                }
                return true;
            }
        }
        catch (ObjectDisposedException) { return true; }
        finally
        {
            // CEF can invalidate the callback during browser shutdown.
            // Keep cleanup separate so it cannot hide a different exception
            // from Continue or Cancel.
            try { callback.Dispose(); }
            catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        IDisposable? registration;
        lock (sync)
        {
            credentials = null;
            registration = revocation;
            revocation = null;
        }
        registration?.Dispose();
        // ProxyCredentials contains managed strings: dropping references does
        // not guarantee the runtime has erased existing copies from memory.
    }
}
