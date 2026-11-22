using Microsoft.Data.Sqlite;

internal static class DependencyUpgradeTests
{
    public static void Run()
    {
        // Query the loaded native library rather than inferring it from the
        // NuGet package version. This checks one advisory's minimum only;
        // it is not a complete SQLite security assessment.
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version(), sqlite_source_id()";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new Exception("FAIL: SQLite did not report its native identity.");
        var versionText = reader.GetString(0);
        var sourceId = reader.GetString(1);
        if (!Version.TryParse(versionText, out var version) || version < new Version(3, 50, 2))
            throw new Exception("FAIL: Loaded SQLite is older than the CVE-2025-6965 fixed version: " + versionText);
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new Exception("FAIL: SQLite source identity is missing.");
        Console.WriteLine($"PASS: loaded SQLite {versionText}; source identity {sourceId}; CVE-2025-6965 version floor checked");
    }
}
