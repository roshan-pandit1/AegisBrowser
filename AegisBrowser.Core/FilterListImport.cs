using System.Security.Cryptography;
using System.Text;

namespace AegisBrowser.Core;

public sealed record ImportedFilterList(string Text, string Sha256);

public static class FilterListImport
{
    public const int MaximumBytes = 8_000_000;
    public static ImportedFilterList Read(string path, CancellationToken cancellationToken = default)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length > MaximumBytes) throw new IOException("List exceeds 8 MB.");
        using var content = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (content.Length + read > MaximumBytes) throw new IOException("List exceeds 8 MB.");
            content.Write(buffer, 0, read);
        }
        var bytes = content.ToArray();
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.StartsWith('\uFEFF')) text = text[1..];
        return new(text, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
}
