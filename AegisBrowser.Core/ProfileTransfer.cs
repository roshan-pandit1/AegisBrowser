using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AegisBrowser.Core;

public sealed record ProfileExportItem(Guid SourceId, int MetadataVersion, string Slug, string DisplayName, string Notes,
    IReadOnlyList<string> Tags, IReadOnlyList<string> Folders, ProfileSettingsSnapshot Settings);
public sealed record ProfileExportContent(int FormatVersion, string ApplicationVersion, DateTimeOffset CreatedUtc,
    ProfileExportItem[] Profiles, ProfileTemplate[] Templates);
public sealed record ProfileExportEnvelope(ProfileExportContent Content, string Sha256);
public sealed record ProfileImportItem(ProfileExportItem Source, string DestinationSlug);
public sealed record ProfileImportPreview(ProfileImportItem[] Profiles, ProfileTemplate[] Templates);

/// <summary>Versioned metadata-only transfer. Imported identities are never reused.</summary>
public sealed class ProfileTransfer(string root, ProfileRepository profiles, ProfileTemplateRepository templates)
{
    public const int FormatVersion = 1;
    public const int MaxBytes = 16_000_000;
    private const string EntryName = "aegis-profile-export.json";
    private static readonly JsonSerializerOptions StrictJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32 };

    public void Export(string destination, IReadOnlyList<ProfileDefinition> selected, IReadOnlyList<ProfileTemplate> selectedTemplates,
        bool includePrivacy, bool includeSearch, bool zip)
    {
        ArgumentNullException.ThrowIfNull(selected); ArgumentNullException.ThrowIfNull(selectedTemplates);
        if (selected.Count + selectedTemplates.Count is < 1 or > 1000) throw new ArgumentException("Select 1–1000 profiles or templates.");
        var items = new List<ProfileExportItem>();
        foreach (var source in selected)
        {
            var current = profiles.GetBySlug(source.Slug);
            if (current is null || !ProfileRepository.MatchesSnapshot(current, source)) throw new IOException("A selected profile changed; refresh before export.");
            var settings = ProfileSettings.ReadClosed(root, source.Slug, includePrivacy, includeSearch);
            current = profiles.GetBySlug(source.Slug);
            if (current is null || !ProfileRepository.MatchesSnapshot(current, source)) throw new IOException("A selected profile changed; refresh before export.");
            items.Add(new(source.Id, source.MetadataVersion, source.Slug, source.DisplayName, source.Notes, source.Tags.ToArray(), source.Folders.ToArray(), settings));
        }
        var currentTemplates = templates.List().ToDictionary(t => t.Id);
        foreach (var template in selectedTemplates)
        {
            if (!currentTemplates.TryGetValue(template.Id, out var current) ||
                !JsonSerializer.SerializeToUtf8Bytes(current).SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(template)))
                throw new IOException("A selected template changed; refresh before export.");
        }
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var content = new ProfileExportContent(FormatVersion, version, DateTimeOffset.UtcNow, items.ToArray(), selectedTemplates.ToArray());
        Validate(content);
        var envelope = new ProfileExportEnvelope(content, Hash(content));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > MaxBytes) throw new IOException("Export exceeds the size limit.");
        Publish(destination, bytes, zip);
    }

    public ProfileImportPreview Preview(string source)
    {
        var bytes = ReadFile(source);
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            CheckDuplicateProperties(json.RootElement);
            var envelope = JsonSerializer.Deserialize<ProfileExportEnvelope>(bytes, StrictJson) ?? throw new IOException("The export is empty.");
            Validate(envelope.Content);
            byte[] expected;
            try { expected = Convert.FromHexString(envelope.Sha256); }
            catch (Exception ex) when (ex is FormatException or ArgumentNullException) { throw new IOException("The export checksum is invalid.", ex); }
            var actual = Convert.FromHexString(Hash(envelope.Content));
            if (expected.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(expected, actual)) throw new IOException("The export checksum does not match.");
            var used = new HashSet<string>(new ProfileCatalog(root).List().Concat(profiles.List().Select(p => p.Slug)), StringComparer.OrdinalIgnoreCase);
            var items = new List<ProfileImportItem>();
            foreach (var item in envelope.Content.Profiles)
            {
                var slug = ProfileBulkOperations.AvailableSlug(item.Slug, used); used.Add(slug);
                items.Add(new(item, slug));
            }
            return new(items.ToArray(), envelope.Content.Templates);
        }
        catch (JsonException ex) { throw new IOException("The export is malformed or contains unsupported fields.", ex); }
    }

    public IReadOnlyList<ProfileOperationResult> Import(ProfileImportPreview preview, CancellationToken cancellation,
        IProgress<ProfileOperationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Profiles is null || preview.Templates is null || preview.Profiles.Length + preview.Templates.Length is < 1 or > 1000)
            throw new ArgumentException("The import preview is empty or too large.");
        // Validate all input and destinations before making the first change.
        var content = new ProfileExportContent(FormatVersion, "preview", DateTimeOffset.UtcNow,
            preview.Profiles.Select(p => p.Source).ToArray(), preview.Templates);
        Validate(content);
        if (preview.Profiles.Select(p => p.DestinationSlug).Distinct(StringComparer.OrdinalIgnoreCase).Count() != preview.Profiles.Length)
            throw new ArgumentException("Import destinations must be distinct.");
        foreach (var item in preview.Profiles)
            ProfileRepository.ValidateDraft(new(item.DestinationSlug, item.Source.DisplayName, item.Source.Notes, item.Source.Tags, item.Source.Folders));

        var total = preview.Profiles.Length + preview.Templates.Length;
        var results = new List<ProfileOperationResult>(total);
        foreach (var item in preview.Profiles)
        {
            if (cancellation.IsCancellationRequested) results.Add(new(item.Source.SourceId, item.DestinationSlug, ProfileOperationOutcome.Canceled, "Not started."));
            else
            {
                try
                {
                    var source = item.Source;
                    var created = profiles.CreateConfigured(new(item.DestinationSlug, source.DisplayName, source.Notes, source.Tags, source.Folders), source.Settings, null);
                    results.Add(new(created.Id, created.Slug, ProfileOperationOutcome.Succeeded, "Profile imported with a new ID."));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                { results.Add(new(item.Source.SourceId, item.DestinationSlug, ProfileOperationOutcome.Failed, ex.Message)); }
            }
            progress?.Report(new(results.Count, total));
        }
        foreach (var item in preview.Templates)
        {
            if (cancellation.IsCancellationRequested) results.Add(new(item.Id, item.Name, ProfileOperationOutcome.Canceled, "Not started."));
            else
            {
                try
                {
                    var name = item.Name[..Math.Min(item.Name.Length, 109)] + " (imported)";
                    var created = templates.Create(name, item.Description, item.Tags, item.Folders, item.Settings);
                    results.Add(new(created.Id, created.Name, ProfileOperationOutcome.Succeeded, "Template imported with a new ID."));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                { results.Add(new(item.Id, item.Name, ProfileOperationOutcome.Failed, ex.Message)); }
            }
            progress?.Report(new(results.Count, total));
        }
        return results;
    }

    private static void Validate(ProfileExportContent content)
    {
        if (content is null || content.FormatVersion != FormatVersion || string.IsNullOrWhiteSpace(content.ApplicationVersion) || content.ApplicationVersion.Length > 80 ||
            content.CreatedUtc == default || content.CreatedUtc.Offset != TimeSpan.Zero || content.Profiles is null || content.Templates is null ||
            content.Profiles.Length + content.Templates.Length is < 1 or > 1000)
            throw new IOException("Unsupported or invalid export format.");
        if (content.Profiles.Any(p => p is null || p.SourceId == Guid.Empty || p.MetadataVersion != ProfileRepository.CurrentVersion) ||
            content.Profiles.Select(p => p.SourceId).Distinct().Count() != content.Profiles.Length ||
            content.Profiles.Select(p => p.Slug).Distinct(StringComparer.OrdinalIgnoreCase).Count() != content.Profiles.Length ||
            content.Templates.Any(t => t is null) || content.Templates.Select(t => t.Id).Distinct().Count() != content.Templates.Length)
            throw new IOException("Export contains duplicate or invalid identities.");
        try
        {
            foreach (var p in content.Profiles)
            { ProfileRepository.ValidateDraft(new(p.Slug, p.DisplayName, p.Notes, p.Tags, p.Folders)); ProfileSettings.Validate(p.Settings); }
            foreach (var t in content.Templates) ProfileTemplateRepository.Validate(t);
        }
        catch (ArgumentException ex) { throw new IOException("Export contains invalid profile or template data.", ex); }
    }

    private static string Hash(ProfileExportContent content) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content))).ToLowerInvariant();

    private void Publish(string destination, byte[] bytes, bool zip)
    {
        var target = Path.GetFullPath(destination);
        var relative = Path.GetRelativePath(Path.GetFullPath(root), target);
        if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            throw new IOException("Choose an export location outside the profile storage directory.");
        var directory = Path.GetDirectoryName(target) ?? throw new IOException("An export directory is required.");
        RejectLink(directory); RejectLink(target);
        var temporary = Path.Combine(directory, ".aegis-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        var owns = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                owns = true;
                if (zip)
                {
                    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
                    using (var output = archive.CreateEntry(EntryName, CompressionLevel.Optimal).Open()) output.Write(bytes);
                }
                else stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, target); // Existing files are never replaced.
        }
        finally { if (owns && File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[] ReadFile(string path)
    {
        RejectLink(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes) throw new IOException("Import file exceeds the size limit.");
        var header = new byte[4]; var count = stream.Read(header); stream.Position = 0;
        if (count >= 4 && header[0] == 'P' && header[1] == 'K')
        {
            try
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, true);
                if (archive.Entries.Count != 1) throw new IOException("The ZIP must contain exactly one export file.");
                var entry = archive.Entries[0];
                var mode = (entry.ExternalAttributes >> 16) & 0xF000;
                if (entry.FullName != EntryName || entry.Length > MaxBytes || mode == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The ZIP contains an unsafe or oversized entry.");
                using var input = entry.Open(); return ReadBounded(input);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException)
            { throw new IOException("The ZIP is corrupt or unsupported.", ex); }
        }
        return ReadBounded(stream);
    }

    private static byte[] ReadBounded(Stream input)
    {
        using var output = new MemoryStream(); var buffer = new byte[16_384];
        int count; while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
        { if (output.Length + count > MaxBytes) throw new IOException("Import exceeds the size limit."); output.Write(buffer, 0, count); }
        return output.ToArray();
    }

    internal static void CheckDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new IOException("Duplicate JSON fields are not allowed."); CheckDuplicateProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckDuplicateProperties(item);
    }

    private static void RejectLink(string path)
    {
        try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked import/export paths are not supported."); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
