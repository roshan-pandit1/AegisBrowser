namespace AegisBrowser.Core;

public sealed record ProxySaveResult(ProxyConfiguration Configuration, string? CleanupWarning);

/// <summary>Coordinates a revision-checked configuration change and protected credential replacement.</summary>
public sealed class ProxyConfigurationService(IProxyRepository repository, IProxyCredentialVault vault)
{
    public ProxySaveResult Save(ProfileDefinition profile, ProfileLease lease, ProxyDraft draft,
        long expectedRevision, ProxyCredentials? replacement = null)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var current = repository.Get(profile, lease);
        if ((current?.Revision ?? 0) != expectedRevision) throw new IOException("Proxy configuration changed; reload it before saving.");
        if (replacement is not null && !draft.AuthenticationRequired) throw new ArgumentException("Replacement credentials require proxy authentication.");
        // Validate the draft before creating a secret, substituting a temporary valid reference.
        ProxyValidation.Validate(replacement is null ? draft : draft with { CredentialId = Guid.NewGuid() });
        if (replacement is not null)
        {
            var created = vault.Store(profile.Id, replacement);
            draft = draft with { CredentialId = created };
        }
        else if (draft.AuthenticationRequired)
        {
            if (current is null || current.Settings.CredentialId != draft.CredentialId ||
                current.Settings.Protocol != draft.Protocol || current.Settings.Port != draft.Port ||
                !string.Equals(current.Settings.Host, draft.Host, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Enter credentials again when changing the proxy endpoint or credential reference.");
            // A reference from another profile, or a missing/corrupt secret, cannot be applied.
            _ = vault.Read(profile.Id, draft.CredentialId!.Value);
        }
        // On uncertain commit outcomes, retain the new encrypted file: it may already be referenced.
        var saved = repository.Save(profile, lease, draft, expectedRevision);
        var old = current?.Settings.CredentialId;
        if (old is Guid oldId && old != saved.Settings.CredentialId)
        {
            try { vault.Delete(profile.Id, oldId); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return new(saved, "The previous protected credential could not be removed; it remains in the local credential store."); }
        }
        return new(saved, null);
    }
}
