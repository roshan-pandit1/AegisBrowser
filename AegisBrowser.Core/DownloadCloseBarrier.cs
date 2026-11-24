namespace AegisBrowser.Core;

/// <summary>
/// Tracks one tab's downloads on a single owner thread. It never closes a browser
/// or interprets a cancellation request as a terminal download observation.
/// The caller must dispose the native callbacks and report a terminal result on
/// the same owner thread before using a successful barrier to close the browser.
/// </summary>
public sealed class DownloadCloseBarrier
{
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly Dictionary<(int BrowserId, uint DownloadId), Action> pending = new();
    private readonly TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool closing;
    private bool issuingRequests;

    public bool TryRegister(int browserId, uint downloadId, Action requestCancellation)
    {
        AssertOwner();
        ArgumentNullException.ThrowIfNull(requestCancellation);
        if (closing || browserId <= 0 || downloadId == 0) return false;
        return pending.TryAdd((browserId, downloadId), requestCancellation);
    }

    // This operation only requests cancellation. The returned task succeeds
    // after every registered download has independently reported a terminal
    // state and its callback has been disposed by the owner.
    public Task BeginClose()
    {
        AssertOwner();
        if (closing) return completion.Task;
        closing = true;
        var keys = pending.Keys.ToArray();
        List<Exception>? errors = null;
        issuingRequests = true;
        foreach (var key in keys)
        {
            if (!pending.TryGetValue(key, out var request)) continue;
            try { request(); }
            catch (Exception ex) { (errors ??= []).Add(ex); }
        }
        issuingRequests = false;
        if (errors is not null) completion.TrySetException(new AggregateException("Download cancellation could not be requested.", errors));
        else CompleteIfDrained();
        return completion.Task;
    }

    // The caller must invoke this only after observing a terminal CEF state and
    // releasing the associated callbacks, or after safely cancelling an
    // uncontinued approval and establishing that it cannot start.
    public bool ObserveTerminal(int browserId, uint downloadId)
    {
        AssertOwner();
        if (!pending.Remove((browserId, downloadId))) return false;
        CompleteIfDrained();
        return true;
    }

    private void CompleteIfDrained()
    {
        if (closing && !issuingRequests && pending.Count == 0) completion.TrySetResult(true);
    }

    private void AssertOwner()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("Download close coordination must run on its owner thread.");
    }
}
