using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AegisBrowser.Core;

/// <summary>Versioned manager metadata; never opens or moves Chromium's data files.</summary>
public sealed class ProfileRepository : IProfileRepository
{
    public const int CurrentVersion = 1;
    public const string DatabaseFileName = "profile-metadata.sqlite3";
    public const string BackupDirectoryName = "metadata-backups";
    private readonly string root;
    private string DatabasePath => Path.Combine(root, DatabaseFileName);

    public ProfileRepository(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A profile root is required.", nameof(root));
        this.root = Path.GetFullPath(root);
    }

    public ProfileDefinition? GetBySlug(string slug)
    {
        slug = NormalizeSlug(slug);
        if (!Directory.Exists(root)) return null;
        CheckNotLink(root);
        if (!Exists(DatabasePath)) return null;
        return WithStore(db => ReadBySlug(db, slug));
    }

    public IReadOnlyList<ProfileDefinition> List()
    {
        if (!Directory.Exists(root)) return Array.Empty<ProfileDefinition>();
        CheckNotLink(root);
        if (!Exists(DatabasePath)) return Array.Empty<ProfileDefinition>();
        return WithStore(db =>
        {
            using var command = Command(db, null, "SELECT slug FROM profiles ORDER BY slug COLLATE NOCASE, slug");
            using var reader = command.ExecuteReader();
            var slugs = new List<string>();
            while (reader.Read()) slugs.Add(reader.GetString(0));
            reader.Close();
            return (IReadOnlyList<ProfileDefinition>)slugs.Select(slug => ReadBySlug(db, slug)!).ToArray();
        });
    }

    public ProfileDefinition Create(string slug) => Create(new ProfileDraft(slug, NormalizeSlug(slug), "", [], []));

    public ProfileDefinition Create(ProfileDraft draft) => CreateConfigured(draft, new ProfileSettingsSnapshot(null, null), null);

    public ProfileDefinition CreateConfigured(ProfileDraft draft, ProfileSettingsSnapshot settings, ProfileDefinition? source)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(settings);
        ProfileSettings.Validate(settings);
        var slug = NormalizeSlug(draft.Slug);
        var definition = NewDefinition(slug) with { DisplayName = draft.DisplayName, Notes = draft.Notes, Tags = draft.Tags, Folders = draft.Folders };
        // Validate before creating the database or profile directory.
        Validate(definition);
        return WithStore(db =>
        {
            if (source is not null)
            {
                var current = ReadBySlug(db, source.Slug);
                if (current is null || current.Id != source.Id || current.Revision != source.Revision)
                    throw new IOException("The source profile changed; reload it before duplicating.");
            }
            CheckProfilePath(slug, mustExist: false);
            if (ReadBySlug(db, slug) is not null) throw new IOException("That browser profile already has metadata.");
            using var transaction = db.BeginTransaction();
            Insert(db, transaction, definition);
            var directoryCreated = false;
            try
            {
                new ProfileCatalog(root).Create(slug, staging => ProfileSettings.Write(staging, settings));
                directoryCreated = true;
                transaction.Commit();
                return definition;
            }
            catch (Exception ex) when (directoryCreated)
            {
                // Another process may already have opened it. Never remove it as rollback.
                throw new IOException("The profile folder was created but metadata could not be committed. " +
                    "The folder was retained; opening it later can retry migration.", ex);
            }
        });
    }

    public ProfileDefinition GetOrMigrate(string slug, ProfileLease lease)
    {
        slug = NormalizeSlug(slug);
        ArgumentNullException.ThrowIfNull(lease);
        if (!lease.IsHeldFor(BrowserPolicy.ProfilePath(root, slug)))
            throw new IOException("The matching profile lease must be held before opening or migrating metadata.");
        return WithStore(db =>
        {
            CheckProfilePath(slug, mustExist: true);
            var existing = ReadBySlug(db, slug);
            if (existing is not null) return existing;

            // A consistent, validated snapshot is required before importing a legacy directory.
            BackupBeforeMigration(db);
            var definition = NewDefinition(slug);
            using var transaction = db.BeginTransaction();
            Insert(db, transaction, definition);
            transaction.Commit();
            return definition;
        });
    }

    public ProfileDefinition Update(ProfileDefinition candidate, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Validate(candidate);
        if (expectedRevision < 1 || expectedRevision != candidate.Revision)
            throw new ArgumentException("An exact current revision is required.", nameof(expectedRevision));
        return WithStore(db =>
        {
            var current = ReadBySlug(db, candidate.Slug) ?? throw new IOException("Profile metadata was not found.");
            if (current.Id != candidate.Id || current.MetadataCreatedUtc != candidate.MetadataCreatedUtc ||
                current.Revision != expectedRevision)
                throw new IOException("Profile metadata changed; reload it before saving.");
            var now = DateTimeOffset.UtcNow;
            if (now < current.UpdatedUtc) now = current.UpdatedUtc;
            var updated = candidate with { Revision = checked(expectedRevision + 1), UpdatedUtc = now };
            using var transaction = db.BeginTransaction();
            using (var command = Command(db, transaction, "UPDATE profiles SET display_name=$display, notes=$notes, updated_utc=$updated, revision=$next WHERE id=$id AND revision=$expected"))
            {
                command.Parameters.AddWithValue("$display", updated.DisplayName);
                command.Parameters.AddWithValue("$notes", updated.Notes);
                command.Parameters.AddWithValue("$updated", FormatTime(updated.UpdatedUtc));
                command.Parameters.AddWithValue("$next", updated.Revision);
                command.Parameters.AddWithValue("$id", updated.Id.ToString("D"));
                command.Parameters.AddWithValue("$expected", expectedRevision);
                if (command.ExecuteNonQuery() != 1) throw new IOException("Profile metadata changed; reload it before saving.");
            }
            ReplaceLabels(db, transaction, updated);
            transaction.Commit();
            return updated;
        });
    }

    private T WithStore<T>(Func<SqliteConnection, T> action)
    {
        if (Exists(root)) CheckNotLink(root);
        Directory.CreateDirectory(root);
        CheckNotLink(root);
        var lockPath = Path.Combine(root, ".profile-metadata.lock");
        CheckNotLink(lockPath);
        using var metadataLock = AcquireLock(lockPath);
        try
        {
            CheckDatabasePaths();
            if (!Exists(DatabasePath)) InitializeDatabase();
            using var db = Open(DatabasePath, SqliteOpenMode.ReadWrite);
            ValidateDatabase(db);
            // Enable WAL only after checking the version, so a newer database is never changed.
            Execute(db, null, "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
            return action(db);
        }
        catch (SqliteException ex)
        {
            throw new IOException("Profile metadata could not be read or saved. Existing files were preserved. " +
                "Close Aegis Browser and follow the profile metadata recovery instructions before retrying.", ex);
        }
    }

    private void InitializeDatabase()
    {
        var temporary = Path.Combine(root, ".profile-metadata-" + Guid.NewGuid().ToString("N") + ".tmp");
        var ownsTemporary = false;
        try
        {
            using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { ownsTemporary = true; }
            using (var db = Open(temporary, SqliteOpenMode.ReadWrite))
            {
                using var tx = db.BeginTransaction();
                Execute(db, tx, """
                    CREATE TABLE profiles (
                        id TEXT PRIMARY KEY NOT NULL, slug TEXT NOT NULL COLLATE NOCASE UNIQUE,
                        display_name TEXT NOT NULL, notes TEXT NOT NULL,
                        metadata_version INTEGER NOT NULL, revision INTEGER NOT NULL,
                        created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL
                    );
                    CREATE TABLE tags (id TEXT PRIMARY KEY NOT NULL, name TEXT NOT NULL COLLATE NOCASE UNIQUE);
                    CREATE TABLE folders (id TEXT PRIMARY KEY NOT NULL, name TEXT NOT NULL COLLATE NOCASE UNIQUE);
                    CREATE TABLE profile_tags (profile_id TEXT NOT NULL REFERENCES profiles(id), tag_id TEXT NOT NULL REFERENCES tags(id), PRIMARY KEY(profile_id, tag_id));
                    CREATE TABLE profile_folders (profile_id TEXT NOT NULL REFERENCES profiles(id), folder_id TEXT NOT NULL REFERENCES folders(id), PRIMARY KEY(profile_id, folder_id));
                    PRAGMA user_version=1;
                    """);
                tx.Commit();
                ValidateDatabase(db);
            }
            File.Move(temporary, DatabasePath); // Never overwrite an existing database.
        }
        finally { if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary); }
    }

    private void BackupBeforeMigration(SqliteConnection db)
    {
        var directory = Path.Combine(root, BackupDirectoryName);
        CheckNotLink(directory);
        Directory.CreateDirectory(directory);
        CheckNotLink(directory);
        var name = "metadata-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(directory, "." + name + ".tmp");
        var destination = Path.Combine(directory, name + ".sqlite3");
        var ownsTemporary = false;
        try
        {
            using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { ownsTemporary = true; }
            using (var backup = Open(temporary, SqliteOpenMode.ReadWrite))
            {
                db.BackupDatabase(backup);
                ValidateDatabase(backup);
                if (Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM profiles"), CultureInfo.InvariantCulture) !=
                    Convert.ToInt64(Scalar(backup, "SELECT COUNT(*) FROM profiles"), CultureInfo.InvariantCulture))
                    throw new IOException("The profile metadata backup did not match the source.");
            }
            File.Move(temporary, destination);
        }
        finally { if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateDatabase(SqliteConnection db)
    {
        var version = Convert.ToInt64(Scalar(db, "PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version != CurrentVersion)
            throw new IOException($"Unsupported profile metadata version {version}; the database was not modified. Restore a compatible version or update Aegis Browser.");
        if (!string.Equals(Convert.ToString(Scalar(db, "PRAGMA quick_check"), CultureInfo.InvariantCulture), "ok", StringComparison.Ordinal))
            throw new IOException("Profile metadata failed its integrity check; the database was not modified.");
        if (Scalar(db, "PRAGMA foreign_key_check") is not null)
            throw new IOException("Profile metadata failed its relationship check; the database was not modified.");
        // A version number alone is not enough to identify an intact schema.
        _ = Scalar(db, "SELECT COUNT(*) FROM profiles");
        _ = Scalar(db, "SELECT COUNT(*) FROM tags");
        _ = Scalar(db, "SELECT COUNT(*) FROM folders");
        _ = Scalar(db, "SELECT COUNT(*) FROM profile_tags");
        _ = Scalar(db, "SELECT COUNT(*) FROM profile_folders");
    }

    private static ProfileDefinition? ReadBySlug(SqliteConnection db, string slug)
    {
        using var command = Command(db, null, "SELECT id, slug, display_name, notes, metadata_version, revision, created_utc, updated_utc FROM profiles WHERE slug=$slug COLLATE NOCASE");
        command.Parameters.AddWithValue("$slug", slug);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        ProfileDefinition profile;
        try
        {
            profile = new ProfileDefinition(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                Array.Empty<string>(), Array.Empty<string>(), reader.GetInt32(4), reader.GetInt64(5),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        { throw new IOException("Profile metadata is malformed; the database was preserved.", ex); }
        reader.Close();
        profile = profile with { Tags = ReadLabels(db, "tags", "profile_tags", "tag_id", profile.Id), Folders = ReadLabels(db, "folders", "profile_folders", "folder_id", profile.Id) };
        try { Validate(profile); }
        catch (ArgumentException ex) { throw new IOException("Profile metadata is invalid or unsupported; the database was preserved.", ex); }
        return profile;
    }

    private static IReadOnlyList<string> ReadLabels(SqliteConnection db, string table, string joinTable, string column, Guid id)
    {
        using var command = Command(db, null, $"SELECT item.name FROM {table} item JOIN {joinTable} link ON item.id=link.{column} WHERE link.profile_id=$id ORDER BY item.name COLLATE NOCASE, item.name");
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using var reader = command.ExecuteReader();
        var labels = new List<string>();
        while (reader.Read()) labels.Add(reader.GetString(0));
        return labels;
    }

    private static void Insert(SqliteConnection db, SqliteTransaction tx, ProfileDefinition p)
    {
        Validate(p);
        using var command = Command(db, tx, "INSERT INTO profiles (id, slug, display_name, notes, metadata_version, revision, created_utc, updated_utc) VALUES ($id, $slug, $display, $notes, $version, $revision, $created, $updated)");
        command.Parameters.AddWithValue("$id", p.Id.ToString("D"));
        command.Parameters.AddWithValue("$slug", p.Slug);
        command.Parameters.AddWithValue("$display", p.DisplayName);
        command.Parameters.AddWithValue("$notes", p.Notes);
        command.Parameters.AddWithValue("$version", p.MetadataVersion);
        command.Parameters.AddWithValue("$revision", p.Revision);
        command.Parameters.AddWithValue("$created", FormatTime(p.MetadataCreatedUtc));
        command.Parameters.AddWithValue("$updated", FormatTime(p.UpdatedUtc));
        command.ExecuteNonQuery();
        ReplaceLabels(db, tx, p);
    }

    private static void ReplaceLabels(SqliteConnection db, SqliteTransaction tx, ProfileDefinition p)
    {
        Replace(db, tx, p.Id, p.Tags, "tags", "profile_tags", "tag_id");
        Replace(db, tx, p.Id, p.Folders, "folders", "profile_folders", "folder_id");
    }

    private static void Replace(SqliteConnection db, SqliteTransaction tx, Guid id, IReadOnlyList<string> labels, string table, string joinTable, string column)
    {
        using (var clear = Command(db, tx, $"DELETE FROM {joinTable} WHERE profile_id=$id"))
        { clear.Parameters.AddWithValue("$id", id.ToString("D")); clear.ExecuteNonQuery(); }
        foreach (var label in labels)
        {
            using (var add = Command(db, tx, $"INSERT INTO {table} (id, name) VALUES ($id, $name) ON CONFLICT(name) DO NOTHING"))
            { add.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D")); add.Parameters.AddWithValue("$name", label); add.ExecuteNonQuery(); }
            using var link = Command(db, tx, $"INSERT INTO {joinTable} (profile_id, {column}) SELECT $profile, id FROM {table} WHERE name=$name COLLATE NOCASE");
            link.Parameters.AddWithValue("$profile", id.ToString("D"));
            link.Parameters.AddWithValue("$name", label);
            if (link.ExecuteNonQuery() != 1) throw new IOException("Could not save profile labels.");
        }
    }

    private static ProfileDefinition NewDefinition(string slug)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), slug, slug, "", Array.Empty<string>(), Array.Empty<string>(), CurrentVersion, 1, now, now);
    }

    private static void Validate(ProfileDefinition p)
    {
        if (p.Id == Guid.Empty || p.Slug != NormalizeSlug(p.Slug) || p.MetadataVersion != CurrentVersion || p.Revision is < 1 or long.MaxValue ||
            p.MetadataCreatedUtc == default || p.MetadataCreatedUtc.Offset != TimeSpan.Zero || p.UpdatedUtc.Offset != TimeSpan.Zero || p.UpdatedUtc < p.MetadataCreatedUtc)
            throw new ArgumentException("Invalid profile identity, version or timestamps.");
        if (string.IsNullOrWhiteSpace(p.DisplayName) || p.DisplayName.Length > 120 || p.DisplayName != p.DisplayName.Trim() || p.DisplayName.Any(char.IsControl))
            throw new ArgumentException("Display names require 1–120 non-control characters without surrounding whitespace.");
        if (p.Notes is null || p.Notes.Length > 16_000 || p.Notes.Contains('\0')) throw new ArgumentException("Invalid profile notes.");
        ValidateLabels(p.Tags); ValidateLabels(p.Folders);
    }

    private static void ValidateLabels(IReadOnlyList<string> labels)
    {
        if (labels is null || labels.Count > 100 || labels.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 80 || s != s.Trim() || s.Any(char.IsControl)) ||
            labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != labels.Count)
            throw new ArgumentException("Tags and folders must be unique, nonempty names of at most 80 characters (100 maximum).");
    }

    private static string NormalizeSlug(string slug) => BrowserPolicy.ReadProfile(["--profile=" + slug]);
    private static string FormatTime(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private void CheckProfilePath(string slug, bool mustExist)
    {
        var path = BrowserPolicy.ProfilePath(root, slug);
        if (Exists(path))
        {
            CheckNotLink(path);
            if (!Directory.Exists(path)) throw new IOException("The profile path is not a directory.");
        }
        else if (mustExist) throw new IOException("The profile directory does not exist.");
    }

    private void CheckDatabasePaths()
    {
        foreach (var path in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm", DatabasePath + "-journal" })
            CheckNotLink(path);
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void CheckNotLink(string path)
    {
        if (Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked profile and metadata paths are not supported: " + path);
    }

    public static void CheckSafeProfilePath(string root, string slug)
    {
        var repository = new ProfileRepository(root);
        if (Exists(repository.root)) CheckNotLink(repository.root);
        repository.CheckProfilePath(NormalizeSlug(slug), mustExist: false);
    }

    private static FileStream AcquireLock(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(50); }
        }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, ForeignKeys = true, DefaultTimeout = 10 };
        var connection = new SqliteConnection(builder.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql; return command;
    }
    private static void Execute(SqliteConnection db, SqliteTransaction? tx, string sql)
    { using var command = Command(db, tx, sql); command.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection db, string sql)
    { using var command = Command(db, null, sql); return command.ExecuteScalar(); }
}
