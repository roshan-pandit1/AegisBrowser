using System.IO;

namespace AegisBrowser.Testing;

// The input paths are fixed in source, never supplied by an operation request.
// Hash agreement detects stale inputs; it is not publisher authentication or an
// operating-system boundary against someone who can edit this workspace.
internal static class InventoryBinding
{
    internal static readonly string[] DecisionSources =
    {
        "AegisBrowser.Tests/TestGovernance.cs",
        "AegisBrowser.Tests/TestGovernanceTests.cs",
        "AegisBrowser.Tests/InventoryValidation.cs",
        "AegisBrowser.Tests/InventoryBinding.cs",
        "tests/governance/InventoryValidationTests.cs",
        "tests/governance/PureDecisionRunner.cs"
    };

    internal static bool ValidateCurrent()
    {
        try
        {
            var root = Path.GetFullPath(Directory.GetCurrentDirectory());
            if (root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var inventoryPath = Path.Combine(root, "tests", "governance", "test-safety-inventory.json");
            if (!OrdinaryFile(inventoryPath)) return false;
            var info = new FileInfo(inventoryPath);
            if (info.Length <= 0 || info.Length > 2 * 1024 * 1024) return false;
            var observed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relative in DecisionSources)
            {
                var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!OrdinaryFile(path) || new FileInfo(path).Length > 2 * 1024 * 1024) return false;
                var digest = InventoryValidation.ComputeDigest(File.ReadAllBytes(path), InventoryValidation.RawSha256);
                if (digest is null || !observed.TryAdd(relative, digest)) return false;
            }
            return InventoryValidation.Validate(File.ReadAllBytes(inventoryPath),
                InventoryValidation.SelfTestOperation, "A", DecisionSources, observed, out _);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private static bool OrdinaryFile(string path)
    {
        var attributes = File.GetAttributes(path);
        return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
    }
}
