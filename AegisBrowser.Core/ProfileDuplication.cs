namespace AegisBrowser.Core;

public interface IProfileDuplicationService
{
    ProfileDefinition DuplicateClean(ProfileDefinition source, string destinationSlug, string displayName,
        bool copyNotes, bool copyPrivacy, bool copySearch);
}

/// <summary>Creates a fresh browser profile from metadata and explicitly selected app settings.</summary>
public sealed class ProfileDuplicationService(string root, ProfileRepository repository) : IProfileDuplicationService
{
    public ProfileDefinition DuplicateClean(ProfileDefinition source, string destinationSlug, string displayName,
        bool copyNotes, bool copyPrivacy, bool copySearch)
    {
        ArgumentNullException.ThrowIfNull(source);
        var settings = ProfileSettings.ReadClosed(root, source.Slug, copyPrivacy, copySearch);
        var draft = new ProfileDraft(destinationSlug, displayName, copyNotes ? source.Notes : "",
            source.Tags.ToArray(), source.Folders.ToArray());
        return repository.CreateConfigured(draft, settings, source);
    }
}
