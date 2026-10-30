using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AegisBrowser.Core;

internal sealed record SettingsTransaction(int Version, Guid OperationId, Guid ProfileId, string Slug,
    bool Privacy, string? OldPrivacy, string? NewPrivacy, bool Search, string? OldSearch, string? NewSearch);
internal sealed record SettingsPending(int Version, Guid OperationId, Guid ProfileId, string Slug, string ManifestSha256);

/// <summary>Applies supported template settings to closed profiles with durable rollback information.</summary>
public sealed class ProfileSettingsTransactions
{
    private readonly string rootPath;
    private readonly ProfileRepository profiles;
    private readonly ProfileTemplateRepository templates;
    private readonly Action? afterFirstSwap;
    private static readonly JsonSerializerOptions StrictJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32 };

    public ProfileSettingsTransactions(string root, ProfileRepository profiles, ProfileTemplateRepository templates) : this(root, profiles, templates, null) { }
    internal ProfileSettingsTransactions(string root, ProfileRepository profiles, ProfileTemplateRepository templates, Action? afterFirstSwap)
    { rootPath = Path.GetFullPath(root); this.profiles = profiles; this.templates = templates; this.afterFirstSwap = afterFirstSwap; }

    public IReadOnlyList<ProfileOperationResult> Apply(IReadOnlyList<ProfileDefinition> targets, ProfileTemplate template,
        CancellationToken cancellation, IProgress<ProfileOperationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(targets); ProfileTemplateRepository.Validate(template);
        if (targets.Count is < 1 or > 1000 || targets.Any(t => t is null || t.Id == Guid.Empty) || targets.Select(t => t.Id).Distinct().Count() != targets.Count)
            throw new ArgumentException("Select 1–1000 distinct registered profiles.");
        if (template.Settings.Privacy is null && template.Settings.Search is null)
            throw new ArgumentException("This template has no saved privacy or search settings.");
        var results = new List<ProfileOperationResult>(targets.Count);
        foreach (var target in targets)
        {
            if (cancellation.IsCancellationRequested) results.Add(new(target.Id, target.Slug, ProfileOperationOutcome.Canceled, "Not started."));
            else
            {
                try
                {
                    var currentTemplate = templates.List().SingleOrDefault(t => t.Id == template.Id);
                    if (currentTemplate is null || !JsonSerializer.SerializeToUtf8Bytes(currentTemplate).SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(template)))
                        throw new IOException("Template changed; refresh before applying.");
                    ProfileRepository.CheckSafeProfilePath(rootPath, target.Slug);
                    var directory = BrowserPolicy.ProfilePath(rootPath, target.Slug);
                    if (!Directory.Exists(directory)) throw new IOException("The profile directory is missing.");
                    using var lease = new ProfileLease(directory);
                    profiles.WithExpectedProfile(target, () => { ApplyOne(target, directory, template.Settings); return 0; });
                    results.Add(new(target.Id, target.Slug, ProfileOperationOutcome.Succeeded, "Settings applied."));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
                { results.Add(new(target.Id, target.Slug, ProfileOperationOutcome.Failed, ex.Message)); }
            }
            progress?.Report(new(results.Count, targets.Count));
        }
        return results;
    }

    public void Recover(ProfileDefinition profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ProfileRepository.CheckSafeProfilePath(rootPath, profile.Slug);
        var directory = BrowserPolicy.ProfilePath(rootPath, profile.Slug);
        if (!Directory.Exists(directory)) throw new IOException("The profile directory is missing.");
        using var lease = new ProfileLease(directory);
        profiles.WithExpectedProfile(profile, () => { Rollback(profile, directory); return 0; });
    }

    public static void EnsureNoPending(string root, string slug)
    {
        slug = BrowserPolicy.ReadProfile(["--profile=" + slug]);
        var rootPath = Path.GetFullPath(root); var pending = Path.Combine(rootPath, ".settings-pending");
        RejectLink(rootPath); RejectLink(pending);
        var marker = Path.Combine(pending, "profile-" + slug + ".json");
        if (Exists(marker)) throw new IOException("Interrupted settings operation detected. Use Profiles → Actions → Recover interrupted settings before opening this profile.");
    }

    private void ApplyOne(ProfileDefinition profile, string directory, ProfileSettingsSnapshot settings)
    {
        EnsureNoPending(rootPath, profile.Slug);
        ProfileSettings.Validate(settings);
        var id = Guid.NewGuid(); var stage = Path.Combine(rootPath, ".settings-work-" + id.ToString("N"));
        RejectLink(rootPath); Directory.CreateDirectory(stage);
        if ((File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateFileSystemEntries(stage).Any())
            throw new IOException("Settings staging path is linked or not empty.");
        string? oldPrivacy = null, oldSearch = null, newPrivacy = null, newSearch = null;
        if (settings.Privacy is not null)
        {
            oldPrivacy = CaptureExisting(directory, "privacy.json", Path.Combine(stage, "old-privacy.json"), 64_000_000);
            ValidateOld(Path.Combine(stage, "old-privacy.json"), true);
        }
        if (settings.Search is not null)
        {
            oldSearch = CaptureExisting(directory, "search.json", Path.Combine(stage, "old-search.json"), 4096);
            ValidateOld(Path.Combine(stage, "old-search.json"), false);
        }
        ProfileSettings.Write(stage, settings);
        if (settings.Privacy is not null) newPrivacy = FileHash(Path.Combine(stage, "privacy.json"));
        if (settings.Search is not null) newSearch = FileHash(Path.Combine(stage, "search.json"));
        var manifest = new SettingsTransaction(1, id, profile.Id, profile.Slug, oldPrivacy is not null, oldPrivacy, newPrivacy,
            oldSearch is not null, oldSearch, newSearch);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        WriteNew(Path.Combine(stage, "manifest.json"), manifestBytes);
        var pending = Path.Combine(rootPath, ".settings-pending");
        RejectLink(pending); Directory.CreateDirectory(pending); RejectLink(pending);
        var marker = Path.Combine(pending, "profile-" + profile.Slug + ".json");
        var markerData = new SettingsPending(1, id, profile.Id, profile.Slug, Hash(manifestBytes));
        var markerTemporary = Path.Combine(pending, ".pending-" + id.ToString("N") + ".tmp");
        var ownsMarker = false;
        try { WriteNew(markerTemporary, JsonSerializer.SerializeToUtf8Bytes(markerData)); ownsMarker = true; File.Move(markerTemporary, marker); }
        finally { if (ownsMarker && File.Exists(markerTemporary)) File.Delete(markerTemporary); }
        try
        {
            var swapped = false;
            if (manifest.Privacy) { ReplaceNew(stage, directory, "privacy.json", manifest.OldPrivacy!, manifest.NewPrivacy!); swapped = true; afterFirstSwap?.Invoke(); }
            if (manifest.Search) { ReplaceNew(stage, directory, "search.json", manifest.OldSearch!, manifest.NewSearch!); if (!swapped) afterFirstSwap?.Invoke(); }
            if ((manifest.Privacy && FileHash(Path.Combine(directory, "privacy.json")) != manifest.NewPrivacy) ||
                (manifest.Search && FileHash(Path.Combine(directory, "search.json")) != manifest.NewSearch))
                throw new IOException("Applied settings could not be verified.");
            DeleteMarker(marker, markerData);
            Cleanup(stage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            try { Rollback(profile, directory); }
            catch (Exception recovery) when (recovery is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            { throw new IOException("Settings application was interrupted. Browser startup is blocked until interrupted settings are recovered.", new AggregateException(ex, recovery)); }
            throw new IOException("Settings application failed and previous settings were restored.", ex);
        }
    }

    private void Rollback(ProfileDefinition profile, string directory)
    {
        var pending = Path.Combine(rootPath, ".settings-pending"); RejectLink(pending);
        var marker = Path.Combine(pending, "profile-" + profile.Slug + ".json");
        var markerBytes = ReadBounded(marker, 16_384);
        var state = Parse<SettingsPending>(markerBytes);
        if (state.Version != 1 || state.OperationId == Guid.Empty || state.ProfileId != profile.Id || state.Slug != profile.Slug)
            throw new IOException("Pending settings identity is invalid; files were preserved.");
        var stage = Path.Combine(rootPath, ".settings-work-" + state.OperationId.ToString("N")); RejectLink(stage);
        var manifestBytes = ReadBounded(Path.Combine(stage, "manifest.json"), 16_384);
        if (Hash(manifestBytes) != state.ManifestSha256) throw new IOException("Pending settings manifest checksum does not match.");
        var manifest = Parse<SettingsTransaction>(manifestBytes);
        if (manifest.Version != 1 || manifest.OperationId != state.OperationId || manifest.ProfileId != profile.Id || manifest.Slug != profile.Slug ||
            (!manifest.Privacy && !manifest.Search)) throw new IOException("Pending settings manifest is invalid.");
        // Validate both originals and both current states before replacing either file.
        if (manifest.Privacy) CheckRollback(stage, directory, "privacy.json", "old-privacy.json", manifest.OldPrivacy, manifest.NewPrivacy, 64_000_000);
        if (manifest.Search) CheckRollback(stage, directory, "search.json", "old-search.json", manifest.OldSearch, manifest.NewSearch, 4096);
        if (manifest.Privacy) RestoreOld(stage, directory, "privacy.json", "old-privacy.json", manifest.OldPrivacy!, manifest.NewPrivacy!);
        if (manifest.Search) RestoreOld(stage, directory, "search.json", "old-search.json", manifest.OldSearch!, manifest.NewSearch!);
        DeleteMarker(marker, state);
        Cleanup(stage);
    }

    private static void CheckRollback(string stage, string directory, string name, string backup, string? oldHash, string? newHash, int limit)
    {
        if (oldHash is null || newHash is null || Hash(ReadBounded(Path.Combine(stage, backup), limit)) != oldHash)
            throw new IOException("Original settings backup is invalid; files were preserved.");
        var current = Hash(ReadBounded(Path.Combine(directory, name), limit));
        if (current != oldHash && current != newHash) throw new IOException("Settings changed outside this operation; manual recovery is required.");
    }

    private static void RestoreOld(string stage, string directory, string name, string backup, string expected, string replacement)
    {
        var destination = Path.Combine(directory, name); RejectLink(destination);
        var current = FileHash(destination);
        if (current == expected) return;
        if (current != replacement) throw new IOException("Settings changed outside this operation; manual recovery is required.");
        var temporary = Path.Combine(stage, ".restore-" + Guid.NewGuid().ToString("N") + ".tmp");
        var owns = false;
        try
        {
            var bytes = ReadBounded(Path.Combine(stage, backup), name == "privacy.json" ? 64_000_000 : 4096);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { owns = true; file.Write(bytes); file.Flush(true); }
            File.Move(temporary, destination, true);
            if (FileHash(destination) != expected) throw new IOException("Restored settings could not be verified.");
        }
        finally { if (owns && File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ReplaceNew(string stage, string directory, string name, string oldHash, string newHash)
    {
        RejectLink(stage);
        var source = Path.Combine(stage, name); RejectLink(source);
        if (FileHash(source) != newHash) throw new IOException("Staged settings changed; no replacement was made.");
        var destination = Path.Combine(directory, name); RejectLink(destination);
        if (FileHash(destination) != oldHash) throw new IOException("Settings changed outside this operation; no replacement was made.");
        File.Move(source, destination, true);
    }
    private static void DeleteMarker(string marker, SettingsPending expected)
    {
        if (Parse<SettingsPending>(ReadBounded(marker, 16_384)) != expected) throw new IOException("Pending operation changed; files were preserved.");
        RejectLink(marker); File.Delete(marker);
    }
    private static string CaptureExisting(string directory, string name, string backup, int limit)
    {
        var source = Path.Combine(directory, name);
        if (!Exists(source)) throw new IOException("Save existing " + name + " in the browser before applying a template.");
        var bytes = ReadBounded(source, limit); WriteNew(backup, bytes); return Hash(bytes);
    }
    private static void ValidateOld(string path, bool privacy)
    {
        try
        {
            var bytes = ReadBounded(path, privacy ? 64_000_000 : 4096);
            if (privacy) ProfileSettings.Validate(new(JsonSerializer.Deserialize<PrivacyConfiguration>(bytes) ?? throw new IOException("Existing privacy settings are empty."), null));
            else ProfileSettings.Validate(new(null, JsonSerializer.Deserialize<SearchSettings>(bytes) ?? throw new IOException("Existing search settings are empty.")));
        }
        catch (JsonException ex) { throw new IOException("Existing settings are corrupt; the original file was preserved.", ex); }
    }
    private static byte[] ReadBounded(string path, int limit)
    {
        RejectLink(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > limit) throw new IOException("Settings operation file exceeds the size limit.");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); return bytes;
    }
    private static T Parse<T>(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            ProfileTransfer.CheckDuplicateProperties(document.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, StrictJson) ?? throw new IOException("Settings operation record is empty.");
        }
        catch (JsonException ex) { throw new IOException("Settings operation record is invalid.", ex); }
    }
    private static void WriteNew(string path, byte[] bytes)
    { using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Write(bytes); file.Flush(true); }
    private static string FileHash(string path) => Hash(ReadBounded(path, 64_000_000));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool Exists(string path)
    { try { _ = File.GetAttributes(path); return true; } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; } }
    private static void RejectLink(string path)
    { if (Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked settings operation paths are not supported."); }
    private static void Cleanup(string stage)
    {
        try
        {
            RejectLink(stage);
            foreach (var name in new[] { "manifest.json", "old-privacy.json", "old-search.json", "privacy.json", "search.json" })
            {
                var path = Path.Combine(stage, name); RejectLink(path); if (File.Exists(path)) File.Delete(path);
            }
            if (Directory.Exists(stage) && !Directory.EnumerateFileSystemEntries(stage).Any()) Directory.Delete(stage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Preserve remaining owned staging files for inspection. */ }
    }
}
