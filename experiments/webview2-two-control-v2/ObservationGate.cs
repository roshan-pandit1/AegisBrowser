using System;
using System.Collections.Generic;

namespace AegisBrowser.WebView2TwoControl;

internal readonly record struct StartObservation(bool Candidate, bool Cancel);
internal readonly record struct TerminalObservation(bool Matched, bool Expected);

// This is an observation gate for the isolated fixture, not authenticated
// request ownership or proof that no native callbacks have been queued. All
// calls are made on the owning STA. A caller must not dispatch unless admitted.
internal sealed class ObservationGate
{
    private const int MaximumSeenIdsPerSlot = 256;
    private sealed class SlotState
    {
        internal bool InitializationFinished;
        internal bool Initialized;
        internal bool Pending;
        internal ulong? Candidate;
        internal bool CandidateCancellationRequested;
        internal int ResourceCallbacks;
        internal readonly HashSet<ulong> Seen = new();
    }

    private readonly SlotState[] slots = { new(), new() };
    private bool closing;
    private bool ambiguous;

    private SlotState Get(int slot) => slot is 0 or 1
        ? slots[slot] : throw new ArgumentOutOfRangeException(nameof(slot));

    internal bool IsAmbiguous => ambiguous;
    internal int PendingOperations => PendingForSlot(0) + PendingForSlot(1);
    internal int PendingForSlot(int slot)
    {
        var s = Get(slot);
        return (s.InitializationFinished ? 0 : 1) + (s.Pending ? 1 : 0) + s.ResourceCallbacks;
    }
    internal bool HasCandidate(int slot) => Get(slot).Candidate.HasValue;
    internal bool IsDispatchPending(int slot) => Get(slot).Pending;

    internal bool CompleteInitialization(int slot, bool success)
    {
        var s = Get(slot);
        if (s.InitializationFinished) { ambiguous = true; return false; }
        s.InitializationFinished = true;
        s.Initialized = success;
        if (!success) ambiguous = true;
        return success;
    }

    internal bool TryBeginDispatch(int slot)
    {
        var s = Get(slot);
        if (closing || ambiguous || !s.Initialized || s.Pending || s.ResourceCallbacks != 0) return false;
        s.Pending = true;
        s.Candidate = null;
        s.CandidateCancellationRequested = false;
        return true;
    }

    // A synchronous API exception does not prove no native request was queued.
    internal void DispatchUncertain(int slot) { _ = Get(slot); ambiguous = true; }

    internal StartObservation ObserveStart(int slot, ulong id, bool uriMatch, bool headerMatch, bool redirected)
    {
        var s = Get(slot);
        bool fresh = s.Seen.Count < MaximumSeenIdsPerSlot && s.Seen.Add(id);
        if (!fresh || !s.Initialized || !s.Pending || s.Candidate.HasValue ||
            !uriMatch || !headerMatch || redirected || ambiguous)
        {
            ambiguous = true;
            return new(false, true);
        }
        s.Candidate = id;
        s.CandidateCancellationRequested = closing;
        return new(true, closing);
    }

    internal TerminalObservation ObserveCompletion(int slot, ulong id, bool succeeded, bool cancelled)
    {
        var s = Get(slot);
        bool matches = s.Pending && s.Candidate.HasValue && s.Candidate.Value == id;
        if (!matches || ambiguous || (succeeded && cancelled))
        {
            ambiguous = true;
            return new(false, false);
        }

        bool expected = s.CandidateCancellationRequested ? cancelled : succeeded;
        if (!expected)
        {
            ambiguous = true;
            return new(true, false);
        }

        // Exactly one observed terminal changes the pending state. This does
        // not establish an authenticated link from the host call to this ID.
        s.Pending = false;
        s.Candidate = null;
        return new(true, true);
    }

    internal void RequestClose() => closing = true;

    internal void BeginResource(int slot)
    {
        var s = Get(slot);
        if (!s.Initialized) ambiguous = true;
        checked { s.ResourceCallbacks++; }
    }

    internal void EndResource(int slot)
    {
        var s = Get(slot);
        if (s.ResourceCallbacks == 0) { ambiguous = true; return; }
        s.ResourceCallbacks--;
    }

    // A UI-thread observation only, under a restricted fixture assumption.
    // It is not proof of native/browser-process quiescence or ownership.
    internal bool CanAttemptObservedDisposal => closing && !ambiguous &&
        slots[0].Initialized && slots[1].Initialized && PendingOperations == 0;
}
