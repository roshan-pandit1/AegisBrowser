using System.Security.Cryptography;
using System.Text.Json;

namespace P309g;

// Operator-pinned, bounded bundle. Reject unlisted files instead of trusting
// native modules or resource configuration copied alongside the bootstrap.
// A local administrator or concurrent hostile filesystem can still race path
// lookup; this is not an OS isolation boundary or publisher authentication.
internal sealed class BundlePins : IDisposable
{
    private readonly List<FileStream> streams = new();
    private readonly Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
    internal string BootstrapHash => hashes["bootstrapc.exe"];

    private static bool SafeName(string s)
    {
        if (s.Length is < 1 or > 180) return false;
        string[] parts = s.Split('/');
        if (parts.Length is < 1 or > 2 || (parts.Length == 2 && parts[0] != "locales")) return false;
        return parts.All(p => p.Length > 0 && p != "." && p != ".." &&
            p.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'));
    }
    private static Dictionary<string, JsonElement> Properties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("MANIFEST_OBJECT_REQUIRED");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in value.EnumerateObject())
            if (!names.Contains(p.Name, StringComparer.Ordinal) || !fields.TryAdd(p.Name, p.Value))
                throw new InvalidDataException("MANIFEST_DUPLICATE_OR_UNKNOWN_FIELD");
        if (fields.Count != names.Length) throw new InvalidDataException("MANIFEST_FIELD_MISSING");
        return fields;
    }
    internal static BundlePins Open(string directory, string manifestPath, string manifestHash)
    {
        var bundle = new BundlePins();
        try
        {
            string root = Path.GetFullPath(directory);
            string manifest = Path.GetFullPath(manifestPath);
            if (!Path.IsPathFullyQualified(manifestPath) ||
                manifest.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("MANIFEST_MUST_BE_EXTERNAL");
            var manifestStream = ProcessRunner.OpenPinned(manifest, manifestHash);
            bundle.streams.Add(manifestStream);
            if (manifestStream.Length > 65536) throw new InvalidDataException("MANIFEST_TOO_LARGE");
            using var json = JsonDocument.Parse(manifestStream, new JsonDocumentOptions { MaxDepth = 8 });
            var top = Properties(json.RootElement, "schema", "files");
            if (top["schema"].ValueKind != JsonValueKind.String || top["schema"].GetString() != "p309g-bundle-v1" ||
                top["files"].ValueKind != JsonValueKind.Array) throw new InvalidDataException("MANIFEST_SCHEMA_INVALID");
            foreach (var element in top["files"].EnumerateArray())
            {
                var fields = Properties(element, "name", "sha256");
                if (fields["name"].ValueKind != JsonValueKind.String || fields["sha256"].ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("MANIFEST_FIELD_TYPE_INVALID");
                string name = fields["name"].GetString()!;
                string hash = fields["sha256"].GetString()!;
                if (!SafeName(name) || hash.Length != 64 || !hash.All(Uri.IsHexDigit) || !bundle.hashes.TryAdd(name, hash))
                    throw new InvalidDataException("MANIFEST_FILE_INVALID_OR_DUPLICATE");
                if (bundle.hashes.Count > 1024) throw new InvalidDataException("MANIFEST_TOO_MANY_FILES");
            }
            foreach (string name in new[] { "bootstrapc.exe", "p309f_client.dll", "libcef.dll", "chrome_elf.dll" })
                if (!bundle.hashes.ContainsKey(name)) throw new InvalidDataException("MANIFEST_REQUIRED_FILE_MISSING");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            void InspectDirectory(string path, bool locales)
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(path))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("BUNDLE_REPARSE_POINT");
                    string name = Path.GetFileName(entry);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (locales || !string.Equals(name, "locales", StringComparison.Ordinal)) throw new InvalidDataException("BUNDLE_DIRECTORY_UNEXPECTED");
                        InspectDirectory(entry, true);
                        continue;
                    }
                    string relative = locales ? "locales/" + name : name;
                    if (!SafeName(relative) || !bundle.hashes.TryGetValue(relative, out string? hash) || !seen.Add(relative))
                        throw new InvalidDataException("BUNDLE_FILE_UNLISTED_OR_DUPLICATE");
                    var stream = ProcessRunner.OpenPinned(entry, hash);
                    bundle.streams.Add(stream);
                    totalBytes = checked(totalBytes + stream.Length);
                    if (totalBytes > 1073741824) throw new InvalidDataException("BUNDLE_SIZE_LIMIT");
                    if (seen.Count > 1024) throw new InvalidDataException("BUNDLE_TOO_MANY_FILES");
                }
            }
            InspectDirectory(root, false);
            if (seen.Count != bundle.hashes.Count) throw new InvalidDataException("BUNDLE_FILE_MISSING");
            return bundle;
        }
        catch { bundle.Dispose(); throw; }
    }
    public void Dispose()
    {
        foreach (var stream in streams) stream.Dispose();
        streams.Clear();
    }
}
