using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AegisBrowser.Core;

/// <summary>Bounded JSON file operations for portable proxy metadata.</summary>
internal static class ProxyPortableFiles
{
    internal const int MaxBytes = 16_384;
    internal static readonly JsonSerializerOptions StrictJson = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };

    internal static byte[] Read(string source)
    {
        var path = Path.GetFullPath(source); RejectLinkedPath(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > MaxBytes) throw new IOException("The proxy file is empty or too large.");
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes); return bytes;
    }

    internal static void Publish(string root, string destination, byte[] bytes, string prefix)
    {
        if (bytes.Length > MaxBytes) throw new IOException("The proxy export exceeds the size limit.");
        var rootPath = Path.GetFullPath(root); RejectLinkedPath(rootPath);
        var target = Path.GetFullPath(destination);
        var relative = Path.GetRelativePath(rootPath, target);
        if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            throw new IOException("Choose an export location outside the profile storage directory.");
        var directory = Path.GetDirectoryName(target) ?? throw new IOException("An export directory is required.");
        RejectLinkedPath(directory); RejectLinkedPath(target);
        var temporary = Path.Combine(directory, prefix + Guid.NewGuid().ToString("N") + ".tmp");
        var owns = false;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { owns = true; file.Write(bytes); file.Flush(true); }
            RejectLinkedPath(directory); RejectLinkedPath(target);
            File.Move(temporary, target); // Never replace an existing file.
        }
        finally
        {
            if (owns)
            {
                RejectLinkedPath(directory); RejectLinkedPath(temporary);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    internal static string Hash<T>(T content) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content))).ToLowerInvariant();

    internal static void ValidateChecksum<T>(T content, string checksum)
    {
        byte[] expected;
        try { expected = Convert.FromHexString(checksum); }
        catch (Exception ex) when (ex is FormatException or ArgumentNullException) { throw new IOException("The proxy file checksum is invalid.", ex); }
        var actual = Convert.FromHexString(Hash(content));
        if (expected.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new IOException("The proxy file checksum does not match.");
    }

    private static void RejectLinkedPath(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            ProxyRepository.RejectLink(current);
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
            current = parent;
        }
    }
}
