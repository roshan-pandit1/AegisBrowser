namespace AegisBrowser.Core;

public sealed record ProxyBulkPreview(ProfileDefinition Profile, ProxyConfiguration? Configuration,
    ProfileOperationOutcome Outcome, string Message)
{
    public override string ToString() => $"Proxy preview for {Profile.Slug}: {Outcome}";
}

/// <summary>Applies non-secret saved proxy intent independently to closed profiles.</summary>
public sealed class ProxyBulkDrafts(IProxyConfigurationEditor editor)
{
    public IReadOnlyList<ProxyBulkPreview> Preview(IReadOnlyList<ProfileDefinition> profiles, CancellationToken cancellation)
    {
        ValidateProfiles(profiles);
        var results = new List<ProxyBulkPreview>(profiles.Count);
        foreach (var profile in profiles)
        {
            if (cancellation.IsCancellationRequested)
            { results.Add(new(profile, null, ProfileOperationOutcome.Canceled, "Not previewed.")); continue; }
            try
            {
                var current = editor.Load(profile);
                results.Add(new(profile, current, ProfileOperationOutcome.Succeeded,
                    current?.Settings.AuthenticationRequired == true ? "Contains credentials; edit individually." : "Ready."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { results.Add(new(profile, null, ProfileOperationOutcome.Failed, ex.Message)); }
        }
        return results;
    }

    public IReadOnlyList<ProfileOperationResult> Apply(IReadOnlyList<ProxyBulkPreview> previews, ProxyDraft draft,
        CancellationToken cancellation, IProgress<ProfileOperationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(previews);
        ValidateProfiles(previews.Select(item => item?.Profile!).ToArray());
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.AuthenticationRequired || draft.CredentialId is not null)
            throw new ArgumentException("Bulk proxy drafts cannot include or share credentials.");
        ProxyValidation.Validate(draft);
        var results = new List<ProfileOperationResult>(previews.Count);
        foreach (var preview in previews)
        {
            var profile = preview.Profile;
            if (cancellation.IsCancellationRequested || preview.Outcome == ProfileOperationOutcome.Canceled)
                results.Add(new(profile.Id, profile.Slug, ProfileOperationOutcome.Canceled, "Not started."));
            else if (preview.Outcome != ProfileOperationOutcome.Succeeded)
                results.Add(new(profile.Id, profile.Slug, ProfileOperationOutcome.Failed, preview.Message));
            else
            {
                try
                {
                    var current = editor.Load(profile);
                    if (current != preview.Configuration) throw new IOException("Proxy settings changed after preview; refresh and retry.");
                    if (current?.Settings.AuthenticationRequired == true)
                        throw new IOException("This profile has protected credentials. Edit it individually to avoid deleting or sharing them.");
                    var saved = editor.Save(profile, draft, current?.Revision ?? 0, null);
                    results.Add(new(profile.Id, profile.Slug, ProfileOperationOutcome.Succeeded,
                        saved.CleanupWarning ?? (draft.Protocol == ProxyProtocol.Direct
                            ? "Saved browser-default networking." : "Saved draft; profile launch is blocked.")));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                { results.Add(new(profile.Id, profile.Slug, ProfileOperationOutcome.Failed, ex.Message)); }
            }
            progress?.Report(new(results.Count, previews.Count));
        }
        return results;
    }

    private static void ValidateProfiles(IReadOnlyList<ProfileDefinition> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count is < 1 or > 1000 || profiles.Any(profile => profile is null || profile.Id == Guid.Empty) ||
            profiles.Select(profile => profile.Id).Distinct().Count() != profiles.Count)
            throw new ArgumentException("Select 1–1000 distinct registered profiles.");
        foreach (var profile in profiles)
            if (BrowserPolicy.ReadProfile(["--profile=" + profile.Slug]) != profile.Slug)
                throw new ArgumentException("Profile slugs must be canonical.");
    }
}
