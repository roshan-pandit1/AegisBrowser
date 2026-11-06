namespace AegisBrowser;

// Owned by the WinForms thread for one tab. This tracks requests only; none of
// these transitions proves that the CEF browser or its popups have closed.
internal sealed class RetirementRequest
{
    private readonly int ownerThreadId = Environment.CurrentManagedThreadId;
    private bool observing;
    private bool closeRequested;
    private bool popupsCloseRequested;

    private void AssertOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThreadId)
            throw new InvalidOperationException("Retirement requests must run on their owning UI thread.");
    }

    internal bool TryBeginObservation()
    {
        AssertOwnerThread();
        if (observing) return false;
        observing = true;
        return true;
    }

    internal bool TryClosePopups()
    {
        AssertOwnerThread();
        if (popupsCloseRequested) return false;
        popupsCloseRequested = true;
        return true;
    }

    internal bool TryRequestClose(bool initialized)
    {
        AssertOwnerThread();
        if (!initialized || closeRequested) return false;
        // Mark before calling CEF: an exception could follow a delivered request.
        // Blindly retrying an uncertain request could issue a duplicate close.
        closeRequested = true;
        return true;
    }

    internal void MarkNativeCloseObserved()
    {
        AssertOwnerThread();
        closeRequested = true;
    }
}
