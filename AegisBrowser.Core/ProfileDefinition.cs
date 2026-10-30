namespace AegisBrowser.Core;

/// <summary>Application-owned metadata. Slug is the existing on-disk profile name.</summary>
public sealed record ProfileDefinition(
    Guid Id,
    string Slug,
    string DisplayName,
    string Notes,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Folders,
    int MetadataVersion,
    long Revision,
    DateTimeOffset MetadataCreatedUtc,
    DateTimeOffset UpdatedUtc);

public interface IProfileRepository
{
    ProfileDefinition? GetBySlug(string slug);
    IReadOnlyList<ProfileDefinition> List();
    ProfileDefinition Create(string slug);
    // The caller must hold the existing profile lease while adopting an existing directory.
    ProfileDefinition GetOrMigrate(string slug, ProfileLease lease);
    ProfileDefinition Update(ProfileDefinition candidate, long expectedRevision);
}
