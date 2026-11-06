namespace AegisBrowser;

// Queues lifecycle notifications independently of a particular WinForms handle.
// Handle notifications and drains are UI-thread-only; Post is safe from CEF threads.
// Delivery is not evidence that CEF has closed and never authorizes native disposal.
internal sealed class LifetimeDispatcher
{
    private readonly object gate = new();
    private readonly Dictionary<object, Action> pending = new();
    private readonly Action<Action> postToUi;
    private readonly Action<Exception> onFailure;
    private readonly int ownerThreadId = Environment.CurrentManagedThreadId;
    private bool handleAvailable;
    private bool scheduled;
    private bool stopped;
    private long generation;

    internal LifetimeDispatcher(Action<Action> postToUi, Action<Exception> onFailure)
    {
        this.postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
        this.onFailure = onFailure ?? throw new ArgumentNullException(nameof(onFailure));
    }

    private void AssertUiThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThreadId)
            throw new InvalidOperationException("Handle lifecycle and dispatch must run on the owning UI thread.");
    }

    // Keys identify coalescible state notifications, not arbitrary commands.
    internal bool Post(object key, Action action)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(action);
        lock (gate)
        {
            if (stopped) return false;
            pending[key] = action;
        }
        Schedule();
        return true;
    }

    internal void HandleCreated()
    {
        AssertUiThread();
        lock (gate)
        {
            if (stopped) return;
            generation++;
            handleAvailable = true;
            scheduled = false;
        }
        Schedule();
    }

    internal void HandleDestroyed()
    {
        AssertUiThread();
        lock (gate)
        {
            if (stopped) return;
            generation++;
            handleAvailable = false;
            scheduled = false;
        }
    }

    // No scheduling or user action is invoked under gate. A failed post remains
    // pending until another notification or a later HandleCreated retries it.
    private void Schedule()
    {
        long token;
        lock (gate)
        {
            if (stopped || !handleAvailable || scheduled || pending.Count == 0) return;
            scheduled = true;
            token = generation;
        }
        try { postToUi(() => Drain(token)); }
        catch (Exception ex)
        {
            lock (gate)
            {
                if (token == generation) scheduled = false;
            }
            onFailure(ex);
        }
    }

    private void Drain(long token)
    {
        AssertUiThread();
        while (true)
        {
            Action next;
            lock (gate)
            {
                // A callback queued to an older, destroyed handle must not
                // consume work belonging to the recreated handle.
                if (stopped || !handleAvailable || token != generation) return;
                if (pending.Count == 0) { scheduled = false; return; }
                var item = pending.First();
                pending.Remove(item.Key);
                next = item.Value;
            }
            try { next(); }
            catch (Exception ex) { onFailure(ex); }
        }
    }

    // Called on final form disposal, not temporary handle destruction.
    // Returns the number of undelivered notifications for diagnostics.
    internal int Stop()
    {
        AssertUiThread();
        lock (gate)
        {
            if (stopped) return 0;
            stopped = true;
            handleAvailable = false;
            scheduled = false;
            generation++;
            var count = pending.Count;
            pending.Clear();
            return count;
        }
    }
}
