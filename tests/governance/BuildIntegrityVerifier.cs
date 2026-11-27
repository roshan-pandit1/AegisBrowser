using System.Security.Cryptography;
using System.Text.Json;

// This pure comparison neither launches an assembly nor supplies a trust anchor.
// The expected manifest digest and argument list must come from an independent
// review. The caller must separately establish trustworthy observations.
internal static class BuildIntegrityVerifier
{
    internal const string Scope = "governance-decision-test-v1";
    internal const string Configuration = "deterministic-debug-disabled";
    internal const string SdkVersion = "10.0.404";
    internal const string RuntimeVersion = "10.0.15";

    // IDs name an exact closure; they are not filesystem paths to be opened.
    internal static readonly IReadOnlyList<string> RequiredIds = Array.AsReadOnly(new[]
    {
        "source:AegisBrowser.Tests/TestGovernance.cs",
        "source:AegisBrowser.Tests/TestGovernanceTests.cs",
        "source:AegisBrowser.Tests/InventoryValidation.cs",
        "source:AegisBrowser.Tests/InventoryBinding.cs",
        "source:tests/governance/InventoryValidationTests.cs",
        "source:tests/governance/PureDecisionRunner.cs",
        "source:tests/governance/BuildIntegrityVerifier.cs",
        "source:tests/governance/BuildIntegrityVerifierTests.cs",
        "tool:dotnet.exe", "tool:csc.dll", "runtime:coreclr.dll",
        "runtime:runtimeconfig.json", "input:inventory.json",
        "ref:System.Runtime.dll", "ref:System.Console.dll", "ref:System.Collections.dll",
        "ref:System.Text.Json.dll", "ref:System.Security.Cryptography.dll",
        "ref:System.Memory.dll", "ref:System.Text.Encoding.Extensions.dll",
        "ref:System.IO.FileSystem.dll", "ref:System.Linq.dll", "output:assembly"
    });

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal static bool Verify(ReadOnlyMemory<byte> manifest, string approvedManifestHash,
        IReadOnlyDictionary<string, byte[]> observed, IReadOnlyList<string> approvedArguments,
        out string reason)
    {
        reason = "INVALID_OR_UNAPPROVED_BUILD";
        if (manifest.IsEmpty || manifest.Length > 2 * 1024 * 1024 || !IsHash(approvedManifestHash) ||
            observed is null || approvedArguments is null || approvedArguments.Count == 0 ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(manifest.Span)), approvedManifestHash,
                StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            using var doc = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 32 });
            var root = doc.RootElement;
            if (!NoDuplicates(root) || !ExactProperties(root, "schema_version", "scope", "build_configuration", "sdk_version", "runtime_version", "arguments", "components") ||
                !root.GetProperty("schema_version").TryGetInt32(out var version) || version != 1 ||
                !TextEquals(root.GetProperty("scope"), Scope) ||
                !TextEquals(root.GetProperty("build_configuration"), Configuration) ||
                !TextEquals(root.GetProperty("sdk_version"), SdkVersion) ||
                !TextEquals(root.GetProperty("runtime_version"), RuntimeVersion)) return false;

            var args = root.GetProperty("arguments");
            if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() != approvedArguments.Count) return false;
            var index = 0;
            foreach (var item in args.EnumerateArray())
            {
                if (string.IsNullOrEmpty(approvedArguments[index]) || !TextEquals(item, approvedArguments[index])) return false;
                index++;
            }

            var required = new HashSet<string>(RequiredIds, StringComparer.Ordinal);
            if (required.Count != RequiredIds.Count || observed.Count != required.Count) return false;
            foreach (var item in observed)
                if (!required.Contains(item.Key) || item.Value is null) return false;
            var entries = root.GetProperty("components");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != required.Count) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                if (!ExactProperties(entry, "id", "sha256") ||
                    entry.GetProperty("id").ValueKind != JsonValueKind.String ||
                    entry.GetProperty("sha256").ValueKind != JsonValueKind.String) return false;
                var id = entry.GetProperty("id").GetString();
                var hash = entry.GetProperty("sha256").GetString();
                if (id is null || !required.Contains(id) || !seen.Add(id) || !IsHash(hash) ||
                    !observed.TryGetValue(id, out var bytes) || bytes is null ||
                    !string.Equals(hash, Hash(bytes), StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (!required.SetEquals(seen)) return false;
            reason = "MATCHED_APPROVED_MANIFEST";
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static bool TextEquals(JsonElement element, string expected) =>
        element.ValueKind == JsonValueKind.String && element.GetString() == expected;

    private static bool IsHash(string? hash)
    {
        if (hash is null || hash.Length != 64) return false;
        foreach (var c in hash)
            if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }

    private static bool ExactProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var fields = new HashSet<string>(expected, StringComparer.Ordinal);
        var count = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (!fields.Contains(property.Name)) return false;
            count++;
        }
        return count == fields.Count;
    }

    private static bool NoDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || !NoDuplicates(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (!NoDuplicates(item)) return false;
        return true;
    }
}
