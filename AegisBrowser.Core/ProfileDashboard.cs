namespace AegisBrowser.Core;

public sealed record DashboardProfile(string Slug, ProfileDefinition? Metadata, ProfileStatus Status)
{
    public string DisplayName => Metadata?.DisplayName ?? Slug;
    public bool CanLaunch => !Status.IsActive && Status.Activity != ProfileActivity.Unknown &&
        (Status.Activity != ProfileActivity.Missing || (Metadata is null && Slug == "default"));
}

public enum DashboardStatusFilter { All, Running, Stopped }
public enum DashboardSort { Name, Updated }

/// <summary>Combines directory discovery and metadata without modifying either during listing.</summary>
public sealed class ProfileDashboard(string root, IProfileRepository repository, ProfileProcessSupervisor processes)
{
    private readonly ProfileCatalog catalog = new(root);

    public IReadOnlyList<DashboardProfile> Load()
    {
        var metadata = repository.List().ToDictionary(p => p.Slug, StringComparer.OrdinalIgnoreCase);
        var slugs = catalog.List().Concat(metadata.Keys).Append("default").Distinct(StringComparer.OrdinalIgnoreCase);
        return slugs.Select(slug => new DashboardProfile(slug, metadata.GetValueOrDefault(slug), processes.GetStatus(slug))).ToArray();
    }

    public ProfileDefinition Create(ProfileDraft draft) => repository.Create(draft);
    public ProfileDefinition Save(ProfileDefinition profile) => repository.Update(profile, profile.Revision);

    public ProfileDefinition PrepareEdit(string slug)
    {
        slug = BrowserPolicy.ReadProfile(["--profile=" + slug]);
        var existing = repository.GetBySlug(slug);
        if (existing is not null) return existing;
        ProfileRepository.CheckSafeProfilePath(root, slug);
        var path = BrowserPolicy.ProfilePath(root, slug);
        if (!Directory.Exists(path)) throw new IOException("Open this profile first to create its directory, then edit its metadata.");
        using var lease = new ProfileLease(path);
        return repository.GetOrMigrate(slug, lease);
    }

    public void Launch(DashboardProfile profile)
    {
        ProfileSettingsTransactions.EnsureNoPending(root, profile.Slug);
        // Recheck the actual filesystem and lease; the displayed row may be stale.
        var current = new DashboardProfile(profile.Slug, profile.Metadata, processes.GetStatus(profile.Slug));
        if (current.Status.Activity == ProfileActivity.Missing && repository.GetBySlug(profile.Slug) is not null)
            throw new IOException("The registered profile directory is missing. Restore it before launching.");
        if (!current.CanLaunch) throw new IOException(current.Status.Detail.Length > 0 ? current.Status.Detail : "This profile cannot be launched.");
        processes.Launch(profile.Slug);
    }

    public static IReadOnlyList<DashboardProfile> Filter(IEnumerable<DashboardProfile> profiles, string search,
        DashboardStatusFilter status, string? folder, DashboardSort sort)
    {
        search = search.Trim();
        var query = profiles.Where(p =>
            (search.Length == 0 || p.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             p.Slug.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             (p.Metadata?.Tags.Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase)) ?? false) ||
             (p.Metadata?.Folders.Any(f => f.Contains(search, StringComparison.OrdinalIgnoreCase)) ?? false)) &&
            (status == DashboardStatusFilter.All || (status == DashboardStatusFilter.Running && p.Status.IsActive) ||
             (status == DashboardStatusFilter.Stopped && p.Status.Activity is ProfileActivity.Stopped or ProfileActivity.Failed)) &&
            (string.IsNullOrEmpty(folder) || (p.Metadata?.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase) ?? false)));
        return (sort == DashboardSort.Updated
            ? query.OrderByDescending(p => p.Metadata?.UpdatedUtc).ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            : query.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
            .ThenBy(p => p.Slug, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
