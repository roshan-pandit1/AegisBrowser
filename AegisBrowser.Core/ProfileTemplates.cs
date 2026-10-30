using System.Text.Json;

namespace AegisBrowser.Core;

public sealed record ProfileTemplate(Guid Id, int SchemaVersion, long Revision, string Name, string Description,
    IReadOnlyList<string> Tags, IReadOnlyList<string> Folders, ProfileSettingsSnapshot Settings,
    DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);

/// <summary>Application-owned, versioned templates. No Chromium storage is included.</summary>
public sealed class ProfileTemplateRepository(string profileRoot)
{
    public const int CurrentVersion = 1;
    public const int MaxBytes = 16_000_000;
    private readonly string root = Path.GetFullPath(profileRoot);
    private string DirectoryPath => Path.Combine(root, "templates");

    public IReadOnlyList<ProfileTemplate> List()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        return WithLock(() => Directory.EnumerateFiles(DirectoryPath, "template-*.json")
            .Select(Read).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Id).ToArray());
    }

    public ProfileTemplate Create(string name, string description, IReadOnlyList<string> tags,
        IReadOnlyList<string> folders, ProfileSettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(tags); ArgumentNullException.ThrowIfNull(folders);
        var now = DateTimeOffset.UtcNow;
        var template = new ProfileTemplate(Guid.NewGuid(), CurrentVersion, 1, name, description,
            tags.ToArray(), folders.ToArray(), settings, now, now);
        Validate(template);
        return WithLock(() => { Write(template, false); return template; });
    }

    public ProfileTemplate Update(ProfileTemplate candidate, long expectedRevision)
    {
        Validate(candidate);
        if (candidate.Revision != expectedRevision || expectedRevision < 1 || expectedRevision == long.MaxValue)
            throw new ArgumentException("An exact template revision is required.");
        return WithLock(() =>
        {
            var current = Read(PathFor(candidate.Id));
            if (current.Revision != expectedRevision || current.CreatedUtc != candidate.CreatedUtc)
                throw new IOException("The template changed; reload it before saving.");
            var now = DateTimeOffset.UtcNow;
            var updated = candidate with { Revision = expectedRevision + 1, UpdatedUtc = now < current.UpdatedUtc ? current.UpdatedUtc : now };
            Write(updated, true);
            return updated;
        });
    }

    public void Delete(Guid id, long expectedRevision) => WithLock(() =>
    {
        var path = PathFor(id);
        var current = Read(path);
        if (current.Revision != expectedRevision) throw new IOException("The template changed; reload it before deleting.");
        CheckNotLink(path);
        File.Delete(path);
        return 0;
    });

    public static void Validate(ProfileTemplate t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (t.Id == Guid.Empty || t.SchemaVersion != CurrentVersion || t.Revision < 1 || t.Revision == long.MaxValue ||
            t.CreatedUtc == default || t.CreatedUtc.Offset != TimeSpan.Zero || t.UpdatedUtc.Offset != TimeSpan.Zero || t.UpdatedUtc < t.CreatedUtc)
            throw new ArgumentException("Invalid template identity, version or timestamps.");
        if (string.IsNullOrWhiteSpace(t.Name) || t.Name.Length > 120 || t.Name != t.Name.Trim() || t.Name.Any(char.IsControl))
            throw new ArgumentException("Template names require 1–120 characters without surrounding whitespace.");
        if (t.Description is null || t.Description.Length > 16_000 || t.Description.Contains('\0')) throw new ArgumentException("Invalid template description.");
        ValidateLabels(t.Tags); ValidateLabels(t.Folders); ProfileSettings.Validate(t.Settings);
    }

    private static void ValidateLabels(IReadOnlyList<string> labels)
    {
        if (labels is null || labels.Count > 100 || labels.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 80 || s != s.Trim() || s.Any(char.IsControl)) ||
            labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != labels.Count)
            throw new ArgumentException("Tags and folders must be unique nonempty names of at most 80 characters (100 maximum).");
    }

    private ProfileTemplate Read(string path)
    {
        CheckNotLink(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxBytes) throw new IOException("Template exceeds the size limit.");
            var template = JsonSerializer.Deserialize<ProfileTemplate>(stream) ?? throw new IOException("Template is empty.");
            Validate(template);
            if (!string.Equals(Path.GetFileName(path), "template-" + template.Id.ToString("D") + ".json", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Template identity does not match its file name.");
            return template;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        { throw new IOException("Template is invalid or uses an unsupported version; the original file was preserved.", ex); }
    }

    private void Write(ProfileTemplate template, bool replace)
    {
        Validate(template);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(template, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > MaxBytes) throw new IOException("Template exceeds the size limit.");
        var destination = PathFor(template.Id);
        CheckNotLink(destination);
        var temporary = Path.Combine(DirectoryPath, ".template-" + Guid.NewGuid().ToString("N") + ".tmp");
        var owns = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { owns = true; stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, destination, replace);
        }
        finally { if (owns && File.Exists(temporary)) File.Delete(temporary); }
    }

    private string PathFor(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A template ID is required.");
        return Path.Combine(DirectoryPath, "template-" + id.ToString("D") + ".json");
    }

    private T WithLock<T>(Func<T> operation)
    {
        CheckNotLink(root); Directory.CreateDirectory(root); CheckNotLink(root);
        CheckNotLink(DirectoryPath); Directory.CreateDirectory(DirectoryPath); CheckNotLink(DirectoryPath);
        var path = Path.Combine(DirectoryPath, ".templates.lock"); CheckNotLink(path);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        FileStream lease;
        while (true)
        {
            try { lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && timer.ElapsedMilliseconds < 10_000) { Thread.Sleep(50); }
        }
        using (lease) return operation();
    }

    private static void CheckNotLink(string path)
    {
        try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked template paths are not supported."); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}

public sealed class ProfileTemplateService(string root, ProfileRepository profiles, ProfileTemplateRepository templates)
{
    public ProfileTemplate SaveFromProfile(ProfileDefinition source, string name, string description, bool copyPrivacy, bool copySearch)
    {
        var settings = ProfileSettings.ReadClosed(root, source.Slug, copyPrivacy, copySearch);
        var current = profiles.GetBySlug(source.Slug);
        if (current is null || !ProfileRepository.MatchesSnapshot(current, source))
            throw new IOException("The source profile changed; reload it before saving a template.");
        return templates.Create(name, description, source.Tags, source.Folders, settings);
    }

    public ProfileDefinition CreateProfile(ProfileTemplate template, string slug, string displayName)
    {
        ProfileTemplateRepository.Validate(template);
        var current = templates.List().SingleOrDefault(t => t.Id == template.Id);
        if (current is null || current.Revision != template.Revision) throw new IOException("The template changed; reload it before use.");
        return profiles.CreateConfigured(new ProfileDraft(slug, displayName, "", template.Tags.ToArray(), template.Folders.ToArray()), template.Settings, null);
    }

    public ProfileTemplate Duplicate(ProfileTemplate source, string name)
    {
        var current = templates.List().SingleOrDefault(t => t.Id == source.Id);
        if (current is null || current.Revision != source.Revision) throw new IOException("The template changed; reload it before duplicating.");
        return templates.Create(name, source.Description, source.Tags, source.Folders, source.Settings);
    }
}
