using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AegisBrowser.Core;

public sealed record ProfileBackupContent(int FormatVersion, Guid BackupId, DateTimeOffset CreatedUtc,
    Guid ProfileId, string Slug, long DatabaseBytes, string DatabaseSha256, ProfileSettingsSnapshot Settings);
public sealed record ProfileBackupManifest(ProfileBackupContent Content, string Sha256);
public sealed record ProfileBackupInfo(Guid Id, DateTimeOffset? CreatedUtc, string Description, bool Valid);
public sealed record ProfileBackupPreview(ProfileBackupManifest Manifest, ProfileDefinition Profile);
public sealed record BackupRetentionReminder(int Version, long Revision, int Threshold);

/// <summary>Consistent SQLite backups and bounded, non-destructive profile recovery.</summary>
public sealed class ProfileBackupService(string root, ProfileRepository repository)
{
    public const int FormatVersion = 1;
    public const long MaxDatabaseBytes = 256_000_000;
    private readonly string rootPath = Path.GetFullPath(root);
    private string BackupRoot => Path.Combine(rootPath, "manual-backups");
    private static readonly JsonSerializerOptions StrictJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32 };

    public BackupRetentionReminder GetRetentionReminder()
    {
        if (!Directory.Exists(BackupRoot)) return new(1, 0, 20);
        return WithPolicyLock(ReadRetention);
    }

    public BackupRetentionReminder SaveRetentionReminder(BackupRetentionReminder expected, int threshold)
    {
        if (threshold is < 1 or > 10_000) throw new ArgumentException("Reminder threshold must be 1–10000.");
        return WithPolicyLock(() =>
        {
            var current = ReadRetention();
            if (current != expected) throw new IOException("Retention reminder changed; refresh before saving.");
            var updated = new BackupRetentionReminder(1, checked(current.Revision + 1), threshold);
            var destination = Path.Combine(BackupRoot, "retention.json"); CheckNotLink(destination);
            var temporary = Path.Combine(BackupRoot, ".retention-" + Guid.NewGuid().ToString("N") + ".tmp"); var owns = false;
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { owns = true; JsonSerializer.Serialize(file, updated); file.Flush(true); }
                File.Move(temporary, destination, true);
            }
            finally { if (owns && File.Exists(temporary)) File.Delete(temporary); }
            return updated;
        });
    }

    private BackupRetentionReminder ReadRetention()
    {
        var path = Path.Combine(BackupRoot, "retention.json"); CheckNotLink(path);
        if (!File.Exists(path)) return new(1, 0, 20);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 4096) throw new IOException("Retention settings exceed the size limit.");
            var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
            using var document = JsonDocument.Parse(bytes);
            ProfileTransfer.CheckDuplicateProperties(document.RootElement);
            var value = JsonSerializer.Deserialize<BackupRetentionReminder>(bytes, StrictJson) ?? throw new IOException("Retention settings are empty.");
            if (value.Version != 1 || value.Revision < 1 || value.Revision == long.MaxValue || value.Threshold is < 1 or > 10_000)
                throw new IOException("Retention settings are invalid or unsupported.");
            return value;
        }
        catch (JsonException ex) { throw new IOException("Retention settings are invalid; the original was preserved.", ex); }
    }

    private T WithPolicyLock<T>(Func<T> action)
    {
        CheckNotLink(rootPath); CheckNotLink(BackupRoot); Directory.CreateDirectory(BackupRoot); CheckNotLink(BackupRoot);
        var path = Path.Combine(BackupRoot, ".retention.lock"); CheckNotLink(path);
        var timer = System.Diagnostics.Stopwatch.StartNew(); FileStream lease;
        while (true)
        {
            try { lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && timer.ElapsedMilliseconds < 10_000) { Thread.Sleep(50); }
        }
        using (lease) return action();
    }

    public ProfileBackupPreview Create(ProfileDefinition source, bool includePrivacy, bool includeSearch)
    {
        ArgumentNullException.ThrowIfNull(source);
        var settings = ProfileSettings.ReadClosed(rootPath, source.Slug, includePrivacy, includeSearch);
        CheckNotLink(rootPath); CheckNotLink(BackupRoot); Directory.CreateDirectory(BackupRoot); CheckNotLink(BackupRoot);
        var id = Guid.NewGuid();
        var stage = Path.Combine(BackupRoot, ".creating-backup-" + id.ToString("N"));
        var destination = Path.Combine(BackupRoot, "backup-" + id.ToString("D"));
        Directory.CreateDirectory(stage);
        if ((File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateFileSystemEntries(stage).Any())
            throw new IOException("Backup staging path is linked or not empty.");
        var database = Path.Combine(stage, "metadata.sqlite3");
        repository.CreateConsistentSnapshot(database);
        var copied = ProfileRepository.ReadSnapshotProfile(database, source.Slug);
        if (!ProfileRepository.MatchesSnapshot(copied, source)) throw new IOException("Profile metadata changed while creating the backup; refresh and retry.");
        var size = new FileInfo(database).Length;
        if (size > MaxDatabaseBytes) throw new IOException("Metadata backup exceeds the size limit.");
        var content = new ProfileBackupContent(FormatVersion, id, DateTimeOffset.UtcNow, source.Id, source.Slug, size, FileHash(database), settings);
        var manifest = new ProfileBackupManifest(content, ContentHash(content));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > ProfileTransfer.MaxBytes) throw new IOException("Backup manifest exceeds the size limit.");
        using (var file = new FileStream(Path.Combine(stage, "manifest.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        var preview = ValidateAt(stage, id);
        Directory.Move(stage, destination);
        return preview;
    }

    public IReadOnlyList<ProfileBackupInfo> List()
    {
        if (!Directory.Exists(BackupRoot)) return [];
        CheckNotLink(rootPath); CheckNotLink(BackupRoot);
        var result = new List<ProfileBackupInfo>();
        foreach (var directory in Directory.EnumerateDirectories(BackupRoot, "backup-*"))
        {
            var name = Path.GetFileName(directory);
            if (!Guid.TryParseExact(name["backup-".Length..], "D", out var id)) continue;
            try { var p = Verify(id); result.Add(new(id, p.Manifest.Content.CreatedUtc, p.Profile.DisplayName + " (metadata snapshot)", true)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { result.Add(new(id, null, "Invalid backup: " + ex.Message, false)); }
        }
        return result.OrderByDescending(r => r.CreatedUtc).ThenBy(r => r.Id).ToArray();
    }

    public ProfileBackupPreview Verify(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A backup ID is required.");
        CheckNotLink(rootPath); CheckNotLink(BackupRoot);
        return ValidateAt(Path.Combine(BackupRoot, "backup-" + id.ToString("D")), id);
    }

    public ProfileDefinition RestoreMetadata(Guid id, ProfileDefinition expectedCurrent)
    {
        var preview = Verify(id);
        ProfileRepository.CheckSafeProfilePath(rootPath, expectedCurrent.Slug);
        var path = BrowserPolicy.ProfilePath(rootPath, expectedCurrent.Slug);
        if (!Directory.Exists(path)) throw new IOException("The profile directory is missing; restore as a new profile instead.");
        using var lease = new ProfileLease(path);
        return repository.RestoreMetadata(preview.Profile, expectedCurrent, lease);
    }

    public ProfileDefinition RestoreAsNew(Guid id, string slug, string displayName)
    {
        var preview = Verify(id);
        var snapshot = preview.Profile;
        return repository.CreateConfigured(new(slug, displayName, snapshot.Notes, snapshot.Tags, snapshot.Folders), preview.Manifest.Content.Settings, null);
    }

    private ProfileBackupPreview ValidateAt(string directory, Guid expectedId)
    {
        CheckNotLink(directory);
        var manifestPath = Path.Combine(directory, "manifest.json"); var database = Path.Combine(directory, "metadata.sqlite3");
        CheckNotLink(manifestPath); CheckNotLink(database);
        ProfileBackupManifest manifest;
        try
        {
            using var file = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > ProfileTransfer.MaxBytes) throw new IOException("Backup manifest exceeds the size limit.");
            var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            ProfileTransfer.CheckDuplicateProperties(document.RootElement);
            manifest = JsonSerializer.Deserialize<ProfileBackupManifest>(bytes, StrictJson) ?? throw new IOException("Backup manifest is empty.");
            var c = manifest.Content;
            if (c is null || c.FormatVersion != FormatVersion || c.BackupId != expectedId || c.ProfileId == Guid.Empty ||
                c.CreatedUtc == default || c.CreatedUtc.Offset != TimeSpan.Zero || c.DatabaseBytes < 1 || c.DatabaseBytes > MaxDatabaseBytes ||
                BrowserPolicy.ReadProfile(["--profile=" + c.Slug]) != c.Slug)
                throw new IOException("Backup manifest is invalid or unsupported.");
            ProfileSettings.Validate(c.Settings);
            if (!FixedEquals(manifest.Sha256, ContentHash(c))) throw new IOException("Backup manifest checksum does not match.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        { throw new IOException("Backup manifest is invalid or unsupported.", ex); }
        using var guard = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (guard.Length != manifest.Content.DatabaseBytes || !FixedEquals(manifest.Content.DatabaseSha256, StreamHash(guard)))
            throw new IOException("Backup database checksum does not match.");
        var profile = ProfileRepository.ReadSnapshotProfile(database, manifest.Content.Slug);
        if (profile.Id != manifest.Content.ProfileId) throw new IOException("Backup profile identity does not match its manifest.");
        return new(manifest, profile);
    }

    private static string ContentHash(ProfileBackupContent content) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content))).ToLowerInvariant();
    private static string FileHash(string path) { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return StreamHash(file); }
    private static string StreamHash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    private static bool FixedEquals(string a, string b)
    {
        try { var left = Convert.FromHexString(a); var right = Convert.FromHexString(b); return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right); }
        catch (Exception ex) when (ex is FormatException or ArgumentNullException) { return false; }
    }
    private static void CheckNotLink(string path)
    {
        try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked backup paths are not supported."); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
