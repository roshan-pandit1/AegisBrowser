using System.Diagnostics;
using AegisBrowser.Core;

// Synthetic compatibility probe only; it exercises the actual profile and
// credential-vault source with one of the two exact dependency graphs.
#if COMPAT_OLD
const string generation = "old";
const int expectedMajor = 8;
#elif COMPAT_NEW
const string generation = "new";
const int expectedMajor = 10;
#else
#error A compatibility dependency generation must be selected.
#endif

try
{
    if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64 ||
        args.Length != 3 || !Guid.TryParseExact(args[2], "N", out var runId))
        throw new FixtureFailure("INVALID_ARGUMENTS_OR_ARCHITECTURE");
    if (typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly.GetName().Version?.Major != expectedMajor ||
        typeof(System.Security.Cryptography.ProtectedData).Assembly.GetName().Version?.Major != expectedMajor)
        throw new FixtureFailure("LOADED_DEPENDENCY_VERSION_MISMATCH");
    var phase = args[0];
    if ((phase is "old-write" or "old-check") != (generation == "old") ||
        phase is not ("old-write" or "new-update" or "old-check" or "new-check"))
        throw new FixtureFailure("INCORRECT_GENERATION_OR_PHASE");
    var root = Path.GetFullPath(args[1]);
    if (!Directory.Exists(root) || !Path.GetFileName(root).Equals("aegis-dependency-compat-" + runId.ToString("N"), StringComparison.Ordinal))
        throw new FixtureFailure("INVALID_FIXTURE_ROOT");

    // The supervisor verifies this child against its retained process object
    // and executable path, then acknowledges before any test data is touched.
    using var self = Process.GetCurrentProcess();
    Console.WriteLine($"READY|{generation}|{Environment.ProcessId}|{self.StartTime.ToUniversalTime().ToFileTimeUtc()}");
    Console.Out.Flush();
    if (!string.Equals(Console.ReadLine(), runId.ToString("N"), StringComparison.Ordinal))
        throw new FixtureFailure("SUPERVISOR_ACKNOWLEDGEMENT_MISSING");

    var marker = Path.Combine(root, "run-id.txt");
    var oldIdPath = Path.Combine(root, "old-id.txt");
    var newIdPath = Path.Combine(root, "new-id.txt");
    var profiles = new ProfileRepository(root);
    var vault = new ProxyCredentialVault(root);
    var first = new ProxyCredentials("synthetic-old-user", "synthetic-old-password");
    var second = new ProxyCredentials("synthetic-new-user", "synthetic-new-password");
    if (phase == "old-write")
    {
        if (File.Exists(marker) || File.Exists(oldIdPath) || File.Exists(newIdPath))
            throw new FixtureFailure("FIXTURE_ALREADY_USED");
        var created = profiles.Create("dependency-fixture");
        using var lease = new ProfileLease(BrowserPolicy.ProfilePath(root, created.Slug));
        var current = profiles.GetOrMigrate(created.Slug, lease);
        _ = profiles.Update(current with { Notes = "created-by-old" }, current.Revision);
        var id = vault.Store(current.Id, first);
        File.WriteAllText(oldIdPath, id.ToString("N"));
        File.WriteAllText(marker, runId.ToString("N"));
    }
    else
    {
        if (!File.Exists(marker) || File.ReadAllText(marker) != runId.ToString("N"))
            throw new FixtureFailure("FIXTURE_RUN_ID_MISMATCH");
        var profile = profiles.GetBySlug("dependency-fixture") ?? throw new FixtureFailure("PROFILE_NOT_FOUND");
        using var lease = new ProfileLease(BrowserPolicy.ProfilePath(root, profile.Slug));
        profile = profiles.GetOrMigrate(profile.Slug, lease);
        var oldId = ReadId(oldIdPath);
        CheckCredentials(vault.Read(profile.Id, oldId), first);
        if (phase == "new-update")
        {
            if (profile.Notes != "created-by-old" || File.Exists(newIdPath)) throw new FixtureFailure("OLD_DATA_MISMATCH");
            _ = profiles.Update(profile with { Notes = "updated-by-new" }, profile.Revision);
            File.WriteAllText(newIdPath, vault.Store(profile.Id, second).ToString("N"));
        }
        else
        {
            CheckCredentials(vault.Read(profile.Id, ReadId(newIdPath)), second);
            if (phase == "old-check")
            {
                if (profile.Notes != "updated-by-new") throw new FixtureFailure("NEW_DATA_MISMATCH");
                _ = profiles.Update(profile with { Notes = "updated-by-old" }, profile.Revision);
            }
            else if (profile.Notes != "updated-by-old") throw new FixtureFailure("ROLLBACK_DATA_MISMATCH");
        }
    }
    Console.WriteLine("PASS_COMPAT_PHASE=" + phase);
    return 0;
}
catch (Exception ex)
{
    // Do not log secret values, ciphertext, paths or arbitrary exception text.
    Console.Error.WriteLine("COMPAT_FAILURE=" + (ex is FixtureFailure failure ? failure.Code : ex.GetType().Name));
    return 1;
}

static Guid ReadId(string path) => Guid.TryParseExact(File.ReadAllText(path), "N", out var id) && id != Guid.Empty
    ? id : throw new FixtureFailure("CREDENTIAL_ID_MISMATCH");

static void CheckCredentials(ProxyCredentials actual, ProxyCredentials expected)
{
    if (actual.Username != expected.Username || actual.Password != expected.Password)
        throw new FixtureFailure("CREDENTIAL_ROUNDTRIP_MISMATCH");
}

sealed class FixtureFailure(string code) : Exception
{
    public string Code { get; } = code;
}
