using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AegisBrowser.Core;

public sealed record BookmarkFolder(Guid Id, string Name, DateTimeOffset CreatedUtc, DateTimeOffset ModifiedUtc, int Order);
public sealed record BookmarkEntry(Guid Id, string Title, string Url, Guid? FolderId, DateTimeOffset CreatedUtc, DateTimeOffset ModifiedUtc, int Order);
public sealed record BookmarkDocument(int Version, DateTimeOffset SavedUtc, List<BookmarkFolder> Folders, List<BookmarkEntry> Items);

/// <summary>Profile-local bookmarks. All disk operations require the same live profile lease.</summary>
public sealed class BookmarkStore
{
    public const int MaxBookmarks = 10000, MaxFolders = 1000, MaxUrlLength = 2048, MaxTextLength = 256, MaxFileBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    private readonly object gate = new();
    private readonly string directory, path, backup;
    private readonly ProfileLease lease;
    private BookmarkDocument document = Empty();
    private bool blocked;
    private bool loadedFile;

    public event EventHandler? Changed;
    public BookmarkStore(string profileDirectory, ProfileLease lease)
    {
        directory = Path.GetFullPath(profileDirectory);
        this.lease = lease ?? throw new ArgumentNullException(nameof(lease));
        path = Path.Combine(directory, "bookmarks.json");
        backup = path + ".bak";
        WithLease(Load);
    }

    private static BookmarkDocument Empty() => new(1, DateTimeOffset.UtcNow, [], []);
    private void WithLease(Action action)
    {
        if (!lease.IsHeldFor(directory) || !lease.TryInvokeWhileHeld(() =>
            {
                if (!lease.IsHeldFor(directory)) throw new IOException("The profile lease does not match.");
                CheckPaths();
                action();
            })) throw new IOException("A live matching profile lease is required.");
    }

    private void CheckPaths()
    {
        var current = new DirectoryInfo(directory);
        while (current is not null)
        {
            var attributes = File.GetAttributes(current.FullName);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException("Linked or invalid bookmark directories are not supported.");
            current = current.Parent;
        }
        foreach (var file in new[] { path, backup })
        {
            try
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new IOException("Linked or invalid bookmark files are not supported.");
            }
            catch (FileNotFoundException) { }
        }
    }

    private void Load()
    {
        if (Directory.EnumerateFiles(directory, ".bookmarks-*.tmp", SearchOption.TopDirectoryOnly).Any())
        {
            blocked = true;
            throw new IOException("An interrupted bookmark write exists. Review the profile files before saving.");
        }
        if (!File.Exists(path))
        {
            if (File.Exists(backup)) { blocked = true; throw new IOException("A bookmark backup exists without the main file. Review it before restoring."); }
            if (Directory.EnumerateFiles(directory, "bookmarks.json.*.corrupt", SearchOption.TopDirectoryOnly).Any())
            {
                blocked = true;
                throw new IOException("A quarantined bookmark file exists without the main file. Review it before restoring.");
            }
            return;
        }
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("Bookmark file exceeds the limit.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var loaded = JsonSerializer.Deserialize<BookmarkDocument>(stream, JsonOptions) ?? throw new JsonException();
            Validate(loaded);
            document = loaded;
            loadedFile = true;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or NotSupportedException)
        {
            blocked = true;
            var quarantine = path + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + "." + Guid.NewGuid().ToString("N") + ".corrupt";
            try { File.Move(path, quarantine); }
            catch (IOException) { }
            throw new IOException("Bookmarks could not be loaded. The original file was preserved. Review the profile files before saving.", ex);
        }
    }

    public BookmarkDocument Snapshot()
    {
        lock (gate) return new(document.Version, document.SavedUtc, [.. document.Folders], [.. document.Items]);
    }

    public void Save(BookmarkDocument next)
    {
        ArgumentNullException.ThrowIfNull(next);
        lock (gate)
        {
            if (blocked) throw new IOException("Bookmarks are unavailable until the profile files are repaired.");
            if (next.SavedUtc != document.SavedUtc) throw new IOException("Bookmarks changed in another window. Reopen the editor and retry.");
            var now = DateTimeOffset.UtcNow;
            if (now <= document.SavedUtc) now = document.SavedUtc.AddTicks(1);
            var candidate = new BookmarkDocument(1, now, [.. next.Folders], [.. next.Items]);
            Validate(candidate);
            WithLease(() =>
            {
                // Multiple windows may hold store instances under the same lease.
                // Serialize through the lease and refuse to overwrite a newer file.
                if (File.Exists(path) != loadedFile)
                    throw new IOException("Bookmarks changed in another window. Reopen the editor and retry.");
                if (loadedFile)
                {
                    using var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (current.Length > MaxFileBytes) throw new IOException("Bookmark file exceeds the limit.");
                    var disk = JsonSerializer.Deserialize<BookmarkDocument>(current, JsonOptions) ?? throw new IOException("Bookmark file is invalid.");
                    Validate(disk);
                    if (disk.SavedUtc != document.SavedUtc)
                        throw new IOException("Bookmarks changed in another window. Reopen the editor and retry.");
                }
                var temporary = Path.Combine(directory, ".bookmarks-" + Guid.NewGuid().ToString("N") + ".tmp");
                var owned = false;
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        owned = true;
                        JsonSerializer.Serialize(stream, candidate, JsonOptions);
                        if (stream.Length > MaxFileBytes) throw new IOException("Bookmarks exceed the file-size limit.");
                        stream.Flush(true);
                    }
                    CheckPaths();
                    if (File.Exists(path)) File.Replace(temporary, path, backup);
                    else File.Move(temporary, path);
                    owned = false;
                    document = candidate;
                    loadedFile = true;
                }
                finally { if (owned && File.Exists(temporary)) File.Delete(temporary); }
            });
        }
        var handlers = Changed;
        if (handlers is not null)
            foreach (EventHandler handler in handlers.GetInvocationList())
                try { handler(this, EventArgs.Empty); }
                catch (Exception) { /* Persistence has already succeeded. */ }
    }

    public string ProfileDirectory => directory;

    private string ImportExportPath(string selected)
    {
        var full = Path.GetFullPath(selected);
        if (!string.Equals(Path.GetDirectoryName(full), directory, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            string.Equals(full, path, StringComparison.OrdinalIgnoreCase) || string.Equals(full, backup, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose an interchange file directly inside this profile directory.");
        try
        {
            var attributes = File.GetAttributes(full);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("Linked or invalid interchange files are not supported.");
        }
        catch (FileNotFoundException) { }
        return full;
    }

    public BookmarkDocument ReadImport(string selected)
    {
        BookmarkDocument? result = null;
        lock (gate) WithLease(() =>
        {
            var file = ImportExportPath(selected);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxFileBytes) throw new IOException("Import exceeds the size limit.");
            result = JsonSerializer.Deserialize<BookmarkDocument>(stream, JsonOptions) ?? throw new JsonException("Empty import.");
            if (result.Version != 1 || result.Folders is null || result.Items is null ||
                result.Folders.Count > MaxFolders || result.Items.Count > MaxBookmarks)
                throw new IOException("Unsupported or oversized import.");
        });
        return result!;
    }

    public void Export(string selected)
    {
        lock (gate) WithLease(() =>
        {
            var target = ImportExportPath(selected);
            var temporary = Path.Combine(directory, ".bookmarks-export-" + Guid.NewGuid().ToString("N") + ".tmp");
            var owned = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    owned = true;
                    JsonSerializer.Serialize(stream, document, JsonOptions);
                    stream.Flush(true);
                }
                ImportExportPath(target);
                File.Move(temporary, target, overwrite: false);
                owned = false;
            }
            finally { if (owned && File.Exists(temporary)) File.Delete(temporary); }
        });
    }

    public static string CleanText(string? input)
    {
        var result = new StringBuilder();
        foreach (var c in input ?? "")
            if (!char.IsControl(c) && c is not ('\u061c' or '\u200e' or '\u200f' or >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069'))
                result.Append(c);
        var text = result.ToString().Trim();
        if (text.Length > MaxTextLength) throw new ArgumentException("Text exceeds 256 characters.");
        return text;
    }

    public static string NormalizeUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaxUrlLength || input.Any(char.IsControl) ||
            !Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Host.Equals("aegis-diagnostics.invalid", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only HTTP or HTTPS addresses without credentials are supported.");
        var builder = new UriBuilder(uri) { Fragment = "" };
        var value = builder.Uri.AbsoluteUri;
        if (value.Length > MaxUrlLength) throw new ArgumentException("Address exceeds 2048 characters.");
        return value;
    }

    private static void Validate(BookmarkDocument value)
    {
        if (value.Version != 1) throw new NotSupportedException("Unsupported bookmark schema.");
        if (value.Folders is null || value.Items is null || value.Folders.Count > MaxFolders || value.Items.Count > MaxBookmarks)
            throw new ArgumentException("Bookmark limits exceeded.");
        var ids = new HashSet<Guid>();
        var folders = new HashSet<Guid>();
        foreach (var folder in value.Folders)
        {
            if (folder is null || folder.Id == Guid.Empty || !ids.Add(folder.Id) || string.IsNullOrEmpty(folder.Name) ||
                CleanText(folder.Name) != folder.Name || folder.Order < 0 || folder.CreatedUtc.Offset != TimeSpan.Zero ||
                folder.ModifiedUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Invalid folder.");
            folders.Add(folder.Id);
        }
        var duplicates = new HashSet<(Guid?, string)>();
        foreach (var item in value.Items)
        {
            if (item is null || item.Id == Guid.Empty || !ids.Add(item.Id) || item.Title is null ||
                CleanText(item.Title) != item.Title || string.IsNullOrEmpty(item.Title) ||
                (item.FolderId is Guid folder && !folders.Contains(folder)) || item.Order < 0 ||
                item.CreatedUtc.Offset != TimeSpan.Zero || item.ModifiedUtc.Offset != TimeSpan.Zero ||
                NormalizeUrl(item.Url) != item.Url || !duplicates.Add((item.FolderId, item.Url)))
                throw new ArgumentException("Invalid or duplicate bookmark.");
        }
    }
}
