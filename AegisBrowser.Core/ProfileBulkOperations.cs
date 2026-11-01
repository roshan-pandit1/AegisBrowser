namespace AegisBrowser.Core;

public enum ProfileBulkAction { AddTags, RemoveTags, AddFolders, RemoveFolders, DuplicateClean }
public enum ProfileOperationOutcome { Succeeded, Failed, Canceled }
public sealed record ProfileOperationResult(Guid Id, string Slug, ProfileOperationOutcome Outcome, string Message);
public sealed record ProfileOperationProgress(int Completed, int Total);

/// <summary>Independent, revision-checked profile operations with explicit per-profile results.</summary>
public sealed class ProfileBulkOperations(string root, ProfileRepository repository, IProfileDuplicationService duplication)
{
    public IReadOnlyList<ProfileOperationResult> Run(IReadOnlyList<ProfileDefinition> targets, ProfileBulkAction action,
        IReadOnlyList<string> labels, CancellationToken cancellationToken, IProgress<ProfileOperationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(targets); ArgumentNullException.ThrowIfNull(labels);
        if (targets.Count is < 1 or > 1000 || targets.Any(p => p is null || p.Id == Guid.Empty) ||
            targets.Select(p => p.Id).Distinct().Count() != targets.Count)
            throw new ArgumentException("Select 1–1000 distinct registered profiles.");
        if (!Enum.IsDefined(action)) throw new ArgumentException("Unknown bulk action.");
        if (action != ProfileBulkAction.DuplicateClean && (labels.Count is < 1 or > 100 ||
            labels.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 80 || s != s.Trim() || s.Any(char.IsControl)) ||
            labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != labels.Count))
            throw new ArgumentException("Enter 1–100 distinct labels of at most 80 characters.");

        var used = new HashSet<string>(new ProfileCatalog(root).List().Concat(repository.List().Select(p => p.Slug)), StringComparer.OrdinalIgnoreCase);
        var results = new List<ProfileOperationResult>(targets.Count);
        foreach (var source in targets)
        {
            if (cancellationToken.IsCancellationRequested)
                results.Add(new(source.Id, source.Slug, ProfileOperationOutcome.Canceled, "Not started."));
            else
            {
                try
                {
                    if (action == ProfileBulkAction.DuplicateClean)
                    {
                        var slug = AvailableSlug(source.Slug, used);
                        var name = source.DisplayName[..Math.Min(source.DisplayName.Length, 115)] + " copy";
                        var created = duplication.DuplicateClean(source, slug, name, false, false, false);
                        used.Add(created.Slug);
                        results.Add(new(source.Id, source.Slug, ProfileOperationOutcome.Succeeded, "Created " + created.Slug + "."));
                    }
                    else
                    {
                        var current = repository.GetBySlug(source.Slug);
                        if (current is null || !ProfileRepository.MatchesSnapshot(current, source)) throw new IOException("Profile changed; reload it before retrying.");
                        var updated = action switch
                        {
                            ProfileBulkAction.AddTags => current with { Tags = Add(current.Tags, labels) },
                            ProfileBulkAction.RemoveTags => current with { Tags = Remove(current.Tags, labels) },
                            ProfileBulkAction.AddFolders => current with { Folders = Add(current.Folders, labels) },
                            ProfileBulkAction.RemoveFolders => current with { Folders = Remove(current.Folders, labels) },
                            _ => throw new ArgumentException("Unknown bulk action.")
                        };
                        repository.Update(updated, current.Revision);
                        results.Add(new(source.Id, source.Slug, ProfileOperationOutcome.Succeeded, "Updated."));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                { results.Add(new(source.Id, source.Slug, ProfileOperationOutcome.Failed, ex.Message)); }
            }
            progress?.Report(new(results.Count, targets.Count));
        }
        return results;
    }

    private static string[] Add(IReadOnlyList<string> original, IReadOnlyList<string> labels) =>
        original.Concat(labels).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static string[] Remove(IReadOnlyList<string> original, IReadOnlyList<string> labels) =>
        original.Where(value => !labels.Contains(value, StringComparer.OrdinalIgnoreCase)).ToArray();

    public static string AvailableSlug(string source, ISet<string> used)
    {
        source = BrowserPolicy.ReadProfile(["--profile=" + source]);
        for (var i = 1; i <= 10_000; i++)
        {
            var suffix = i == 1 ? "-copy" : "-copy-" + i;
            var candidate = source[..Math.Min(source.Length, 40 - suffix.Length)] + suffix;
            if (!used.Contains(candidate)) return candidate;
        }
        throw new IOException("No available duplicate slug was found.");
    }
}
