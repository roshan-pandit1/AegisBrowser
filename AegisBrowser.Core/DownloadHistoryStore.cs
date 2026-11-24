using System.Text.Json;

namespace AegisBrowser.Core;

public enum DownloadFinalStatus { Completed, Cancelled, Failed }

// Deliberately contains no URL, request headers, cookies or credentials.
public sealed record DownloadHistoryEntry(string FileName, string Hostname, string Destination,
    long? TotalBytes, long ReceivedBytes, DownloadFinalStatus Status, DateTimeOffset StartedAt, DateTimeOffset FinishedAt);

/// <summary>Profile-scoped, lease-bound history; corrupt input is never overwritten.</summary>
public sealed class DownloadHistoryStore
{
    private const int MaxEntries = 5000;
    private const long MaxBytes = 4_000_000;
    private readonly string profilePath;
    private readonly string historyPath;
    private readonly ProfileLease lease;
    private bool loadFailed;

    public DownloadHistoryStore(string profilePath, ProfileLease lease)
    {
        this.profilePath = Path.GetFullPath(profilePath);
        this.lease = lease;
        historyPath = Path.Combine(this.profilePath, "download-history.json");
        if (!lease.IsHeldFor(this.profilePath)) throw new IOException("The matching profile lease is required for download history.");
    }

    public IReadOnlyList<DownloadHistoryEntry> Load()
    {
        List<DownloadHistoryEntry>? entries = null;
        RunUnderLease(() => entries = ReadCore());
        return entries!;
    }

    public void Save(IReadOnlyList<DownloadHistoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        RunUnderLease(() =>
        {
            if (loadFailed) throw new IOException("Download history could not be loaded; preserve the original file.");
            // Re-read before every write so a corrupt existing file is not replaced.
            _ = ReadCore();
            if (entries.Count > MaxEntries || entries.Any(entry => !Valid(entry))) throw new IOException("Invalid download history.");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entries);
            if (bytes.LongLength > MaxBytes) throw new IOException("Download history is too large.");
            var temporary = historyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var created = false;
            try
            {
                RejectLink(historyPath);
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    file.Write(bytes);
                    file.Flush(true);
                }
                RejectLink(historyPath);
                File.Move(temporary, historyPath, overwrite: true);
                created = false;
            }
            finally { if (created) File.Delete(temporary); }
        });
    }

    // Clears records only; never removes download destinations.
    public void Clear() => Save(Array.Empty<DownloadHistoryEntry>());

    private List<DownloadHistoryEntry> ReadCore()
    {
        try
        {
            RejectLink(historyPath);
            using var stream = new FileStream(historyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxBytes) throw new InvalidDataException("Download history is too large.");
            var entries = JsonSerializer.Deserialize<List<DownloadHistoryEntry>>(stream) ?? throw new InvalidDataException("Empty download history.");
            if (entries.Count > MaxEntries || entries.Any(entry => !Valid(entry))) throw new InvalidDataException("Invalid download history.");
            return entries;
        }
        catch (FileNotFoundException) { return []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            loadFailed = true;
            throw new IOException("Download history could not be read; the original file is preserved.", ex);
        }
    }

    private static bool Valid(DownloadHistoryEntry? entry) => entry is not null &&
        DownloadFileNames.TrySanitize(entry.FileName, out var safeName) && safeName == entry.FileName &&
        !string.IsNullOrEmpty(entry.Hostname) && Uri.CheckHostName(entry.Hostname) != UriHostNameType.Unknown &&
        !string.IsNullOrEmpty(entry.Destination) && Path.IsPathFullyQualified(entry.Destination) &&
        entry.ReceivedBytes >= 0 && (!entry.TotalBytes.HasValue || entry.TotalBytes >= 0) &&
        Enum.IsDefined(entry.Status) && entry.FinishedAt >= entry.StartedAt;

    private void RunUnderLease(Action action)
    {
        if (!lease.TryInvokeWhileHeld(action)) throw new IOException("The profile lease is no longer held.");
    }

    private static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("Linked download history is not supported.");
        }
        catch (FileNotFoundException) { }
    }
}
