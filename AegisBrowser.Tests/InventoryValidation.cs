using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AegisBrowser.Testing;

// Pure validation only. The caller owns source reads and must supply a fixed,
// reviewed dependency closure. This is not an authorization or OS boundary.
internal static class InventoryValidation
{
    internal const string SelfTestOperation = "governance-self-test";
    internal const string SelfTestScope = "fixed-source-decision-test";
    internal const string RawSha256 = "raw-sha256-v1";
    internal const string JsonLfSha256 = "json-lf-sha256-v1";

    private static readonly string[] RequiredFields =
    {
        "profile", "required_environment", "required_authorization",
        "expected_filesystem_changes", "process_operations", "network_operations",
        "privileges", "basis", "execution_status"
    };

    internal static bool Validate(ReadOnlyMemory<byte> json, string operation, string profile,
        IReadOnlyList<string> requiredPaths, IReadOnlyDictionary<string, string> observedSha256,
        out string reason)
    {
        reason = "INVALID_INVENTORY";
        if (operation != SelfTestOperation || profile != "A")
        {
            reason = "OPERATION_OR_PROFILE_REFUSED";
            return false;
        }
        if (json.IsEmpty || json.Length > 2 * 1024 * 1024 || requiredPaths is null || observedSha256 is null)
            return false;

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (!NoDuplicateKeys(root) || root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema_version", out var schema) ||
                !schema.TryGetInt32(out var version) || version != 1 ||
                !root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array)
                return false;

            var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in requiredPaths)
                if (!ValidPath(path) || !required.Add(path)) return false;
            if (required.Count == 0 || observedSha256.Count != required.Count) return false;

            var observed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in observedSha256)
                if (!ValidPath(pair.Key) || !IsHash(pair.Value) || !observed.TryAdd(pair.Key, pair.Value)) return false;

            var operationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var operationFound = false;
            foreach (var item in operations.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !TryText(item, "id", out var id) ||
                    !operationIds.Add(id)) return false;
                if (id != SelfTestOperation) continue;
                if (operationFound || !TryText(item, "profile", out var opProfile) || opProfile != "A" ||
                    !TryText(item, "category", out var category) || category != "SAFE_WORKSPACE" ||
                    !TryText(item, "scope", out var scope) || scope != SelfTestScope ||
                    !item.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Array)
                    return false;
                var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dep in deps.EnumerateArray())
                    if (dep.ValueKind != JsonValueKind.String || !ValidPath(dep.GetString()) ||
                        !declared.Add(dep.GetString()!)) return false;
                if (!required.SetEquals(declared)) return false;
                operationFound = true;
            }
            if (!operationFound) return false;

            var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matched = 0;
            foreach (var item in entries.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !TryText(item, "path", out var path) ||
                    !ValidPath(path) || !allPaths.Add(path)) return false;
                if (!required.Contains(path)) continue;
                foreach (var field in RequiredFields)
                    if (!TryText(item, field, out _)) return false;
                if (!TryText(item, "profile", out var itemProfile) || itemProfile != "A" ||
                    !TryText(item, "category", out var itemCategory) ||
                    (itemCategory != "SAFE_WORKSPACE" && itemCategory != "SAFE_READ_ONLY") ||
                    !TryText(item, "execution_status", out var status) ||
                    status != "PERMITTED_FIXED_SOURCE_DECISION_TEST" ||
                    !TryText(item, "observed_sha256", out var expected) || !IsHash(expected) ||
                    !observed.TryGetValue(path, out var actual) ||
                    !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) return false;
                // Executable source dependencies are byte-sensitive. An
                // omitted v1 field means raw SHA-256; no algorithm may be
                // silently downgraded or applied to a source file.
                if (item.TryGetProperty("identity_algorithm", out var identity) &&
                    (identity.ValueKind != JsonValueKind.String || identity.GetString() != RawSha256)) return false;
                if (!item.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Array ||
                    evidence.GetArrayLength() == 0) return false;
                foreach (var tag in evidence.EnumerateArray())
                    if (tag.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tag.GetString())) return false;
                matched++;
            }
            if (matched != required.Count) return false;
            reason = "INVENTORY_CONSISTENT";
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
    }

    // JSON-only normalization preserves every byte other than CRLF pairs; valid
    // JSON cannot contain an unescaped newline within a string. Raw identity is
    // mandatory for executable sources and binary inputs.
    internal static string? ComputeDigest(byte[] bytes, string algorithm)
    {
        if (bytes is null) return null;
        if (algorithm == RawSha256) return Convert.ToHexString(SHA256.HashData(bytes));
        if (algorithm != JsonLfSha256 || bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return null;
        try
        {
            var strictUtf8 = new UTF8Encoding(false, true);
            var input = strictUtf8.GetString(bytes);
            using var doc = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 64 });
            if (!NoDuplicateKeys(doc.RootElement)) return null;
            for (var i = 0; i < input.Length; i++)
                if (input[i] == '\r' && (i + 1 == input.Length || input[i + 1] != '\n')) return null;
            return Convert.ToHexString(SHA256.HashData(strictUtf8.GetBytes(input.Replace("\r\n", "\n"))));
        }
        catch (JsonException) { return null; }
        catch (DecoderFallbackException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static bool TryText(JsonElement item, string field, out string value)
    {
        value = "";
        if (!item.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value) && !value.Trim().StartsWith("UNKNOWN", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] == '/' || path.Contains('\\') || path.Contains(':')) return false;
        foreach (var c in path) if (c > 127 || char.IsControl(c)) return false;
        foreach (var part in path.Split('/')) if (part.Length == 0 || part == "." || part == "..") return false;
        return true;
    }

    private static bool IsHash(string? hash)
    {
        if (hash is null || hash.Length != 64) return false;
        foreach (var c in hash) if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }

    private static bool NoDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || !NoDuplicateKeys(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray()) if (!NoDuplicateKeys(value)) return false;
        return true;
    }
}
