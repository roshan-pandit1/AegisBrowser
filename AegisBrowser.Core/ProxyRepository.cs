using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AegisBrowser.Core;

/// <summary>Independent, versioned application metadata; never opens Chromium databases.</summary>
public sealed class ProxyRepository : IProxyRepository
{
    public const string DatabaseFileName = "proxy-config.sqlite3";
    public const string RequiredMarkerName = ".aegis-proxy-required";
    public const int CurrentVersion = 1;
    private readonly string root;
    private readonly ProfileRepository profiles;
    private string DatabasePath => Path.Combine(root, DatabaseFileName);

    public ProxyRepository(string root)
    {
        this.root = Path.GetFullPath(root);
        profiles = new ProfileRepository(this.root);
    }

    public ProxyConfiguration? Get(ProfileDefinition profile, ProfileLease lease)
    {
        CheckLease(profile, lease);
        return profiles.WithExpectedProfile(profile, () => WithStore(false, db => db is null ? null : Read(db, profile.Id)));
    }

    // expectedRevision is zero only for the first configuration. A held lease prevents a
    // browser from starting while the marker and configuration are being committed.
    public ProxyConfiguration Save(ProfileDefinition profile, ProfileLease lease, ProxyDraft draft, long expectedRevision)
    {
        CheckLease(profile, lease);
        ProxyValidation.Validate(draft);
        if (expectedRevision < 0 || expectedRevision == long.MaxValue) throw new ArgumentException("Invalid proxy revision.");
        var guard = Marker(profile); RejectLink(guard);
        if (Exists(guard) && !Exists(DatabasePath))
            throw new IOException("A required proxy guard exists but its database is missing. Restore the configuration before making changes.");
        return profiles.WithExpectedProfile(profile, () => WithStore(true, db =>
        {
            var current = Read(db!, profile.Id);
            if ((current?.Revision ?? 0) != expectedRevision) throw new IOException("Proxy configuration changed; reload it before saving.");
            var marker = Marker(profile);
            RejectLink(marker);
            if (current is null && Exists(marker))
                throw new IOException("A required proxy guard has no matching configuration. Restore the configuration before making changes.");
            // Publish the guard first. If anything fails after this point, startup stays blocked.
            if (draft.Protocol != ProxyProtocol.Direct && !Exists(marker))
            {
                using var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.WriteByte(1);
                file.Flush(true);
            }
            var now = DateTimeOffset.UtcNow;
            if (current is not null && now < current.UpdatedUtc) now = current.UpdatedUtc;
            var saved = new ProxyConfiguration(current?.Id ?? Guid.NewGuid(), profile.Id, draft, CurrentVersion,
                checked(expectedRevision + 1), current?.CreatedUtc ?? now, now);
            using (var tx = db!.BeginTransaction())
            {
                using var command = db.CreateCommand();
                command.Transaction = tx;
                command.CommandText = """
                    INSERT INTO profile_proxies (profile_id,id,protocol,host,port,auth,credential_id,label,require_proxy,revision,created_utc,updated_utc)
                    VALUES ($profile,$id,$protocol,$host,$port,$auth,$credential,$label,$required,$revision,$created,$updated)
                    ON CONFLICT(profile_id) DO UPDATE SET protocol=$protocol,host=$host,port=$port,auth=$auth,
                        credential_id=$credential,label=$label,require_proxy=$required,revision=$revision,updated_utc=$updated;
                    """;
                command.Parameters.AddWithValue("$profile", profile.Id.ToString("D"));
                command.Parameters.AddWithValue("$id", saved.Id.ToString("D"));
                command.Parameters.AddWithValue("$protocol", (int)draft.Protocol);
                command.Parameters.AddWithValue("$host", draft.Host);
                command.Parameters.AddWithValue("$port", draft.Port);
                command.Parameters.AddWithValue("$auth", draft.AuthenticationRequired ? 1 : 0);
                command.Parameters.AddWithValue("$credential", (object?)draft.CredentialId?.ToString("D") ?? DBNull.Value);
                command.Parameters.AddWithValue("$label", draft.Label);
                command.Parameters.AddWithValue("$required", draft.RequireProxy ? 1 : 0);
                command.Parameters.AddWithValue("$revision", saved.Revision);
                command.Parameters.AddWithValue("$created", saved.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$updated", saved.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
                tx.Commit();
            }
            // Only an explicitly saved Direct configuration may clear a guard.
            if (draft.Protocol == ProxyProtocol.Direct && Exists(marker)) { RejectLink(marker); File.Delete(marker); }
            return saved;
        }));
    }

    public void EnsureRuntimeReady(ProfileDefinition profile, ProfileLease lease)
    {
        var configuration = Get(profile, lease); // Corruption or unsupported versions must fail closed.
        var marker = Marker(profile);
        RejectLink(marker);
        if (Exists(marker) || (configuration is not null && configuration.Settings.Protocol != ProxyProtocol.Direct))
            throw new IOException("This profile has a required or interrupted proxy configuration. Proxy networking is not available in this build; the profile was not started. Restore the configuration or explicitly select Direct in a compatible management tool.");
    }

    private void CheckLease(ProfileDefinition profile, ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(lease);
        ProfileRepository.CheckSafeProfilePath(root, profile.Slug);
        var path = BrowserPolicy.ProfilePath(root, profile.Slug);
        if (!Directory.Exists(path) || !lease.IsHeldFor(path)) throw new IOException("The matching profile lease is required to read or change proxy settings.");
    }

    private string Marker(ProfileDefinition profile) => Path.Combine(BrowserPolicy.ProfilePath(root, profile.Slug), RequiredMarkerName);

    private T WithStore<T>(bool create, Func<SqliteConnection?, T> action)
    {
        RejectLink(root);
        var lockPath = Path.Combine(root, ".proxy-config.lock"); RejectLink(lockPath);
        using var gate = AcquireLock(lockPath);
        try
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) RejectLink(DatabasePath + suffix);
            if (!Exists(DatabasePath))
            {
                if (!create) return action(null);
                Initialize();
            }
            using var db = Open(DatabasePath);
            ValidateDatabase(db);
            return action(db);
        }
        catch (SqliteException ex) { throw new IOException("Proxy configuration could not be read or saved. Existing files were preserved; repair the configuration before starting the profile.", ex); }
    }

    private void Initialize()
    {
        var temporary = Path.Combine(root, ".proxy-config-" + Guid.NewGuid().ToString("N") + ".tmp");
        var owns = false;
        try
        {
            using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) owns = true;
            using (var db = Open(temporary))
            {
                using var command = db.CreateCommand();
                command.CommandText = """
                    CREATE TABLE profile_proxies (
                      profile_id TEXT PRIMARY KEY NOT NULL, id TEXT UNIQUE NOT NULL, protocol INTEGER NOT NULL,
                      host TEXT NOT NULL, port INTEGER NOT NULL, auth INTEGER NOT NULL, credential_id TEXT,
                      label TEXT NOT NULL, require_proxy INTEGER NOT NULL, revision INTEGER NOT NULL,
                      created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);
                    PRAGMA user_version=1;
                    """;
                command.ExecuteNonQuery();
                ValidateDatabase(db);
            }
            File.Move(temporary, DatabasePath);
        }
        finally { if (owns && File.Exists(temporary)) File.Delete(temporary); }
    }

    private static ProxyConfiguration? Read(SqliteConnection db, Guid profileId)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id,protocol,host,port,auth,credential_id,label,require_proxy,revision,created_utc,updated_utc FROM profile_proxies WHERE profile_id=$profile";
        command.Parameters.AddWithValue("$profile", profileId.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        try
        {
            var auth = reader.GetInt32(4); var required = reader.GetInt32(7);
            if (auth is not (0 or 1) || required is not (0 or 1)) throw new FormatException();
            var draft = new ProxyDraft((ProxyProtocol)reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3), auth == 1,
                reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)), reader.GetString(6), required == 1);
            ProxyValidation.Validate(draft);
            var value = new ProxyConfiguration(Guid.Parse(reader.GetString(0)), profileId, draft, CurrentVersion, reader.GetInt64(8),
                DateTimeOffset.ParseExact(reader.GetString(9), "O", CultureInfo.InvariantCulture),
                DateTimeOffset.ParseExact(reader.GetString(10), "O", CultureInfo.InvariantCulture));
            if (value.Id == Guid.Empty || value.Revision is < 1 or long.MaxValue || value.CreatedUtc.Offset != TimeSpan.Zero ||
                value.UpdatedUtc.Offset != TimeSpan.Zero || value.UpdatedUtc < value.CreatedUtc) throw new FormatException();
            return value;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException or InvalidCastException)
        { throw new IOException("Proxy configuration is invalid or unsupported; the database was preserved.", ex); }
    }

    private static void ValidateDatabase(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != CurrentVersion)
            throw new IOException("Unsupported proxy configuration version; the database was preserved.");
        command.CommandText = "PRAGMA quick_check";
        if (!string.Equals(Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture), "ok", StringComparison.Ordinal))
            throw new IOException("Proxy configuration failed its integrity check; the database was preserved.");
        command.CommandText = "SELECT COUNT(*) FROM profile_proxies";
        _ = command.ExecuteScalar();
    }

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 10 }.ToString());
        try { db.Open(); return db; } catch { db.Dispose(); throw; }
    }

    private static FileStream AcquireLock(string path)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && deadline.ElapsedMilliseconds < 10000)
            { Thread.Sleep(50); RejectLink(path); }
        }
    }

    internal static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal static void RejectLink(string path)
    {
        if (Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked proxy storage paths are not supported.");
    }
}
