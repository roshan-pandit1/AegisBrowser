using System;
using System.Collections.Generic;

namespace AegisBrowser.WebView2Feasibility;

internal readonly record struct NavigationStartObservation(bool Cancel, string Event);
internal readonly record struct NavigationCompletionObservation(bool Released, string Event);

// Used only on the WebView's owning UI thread. NavigateToString does not return
// a request token. The first eligible start is therefore a *candidate*, not an
// authenticated association: a same-URI, unobserved competing navigation could
// be mistaken for it even if no overlap has yet been observed. A candidate
// terminal is not proof of ownership and therefore NEVER releases the lease.
// This diagnostic fixture cannot establish ownership solely from this API.
internal sealed class NavigationLeaseTracker
{
    private const int MaximumRememberedIds = 256;
    private readonly HashSet<ulong> retired = new();
    private readonly HashSet<ulong> unowned = new();
    private IDisposable? lease;
    private ulong? candidateId;
    private bool ambiguous;
    private bool unreliable;
    private bool observedStart;
    private bool dispatchAttempted;

    internal bool HasPending => lease is not null;
    internal bool IsAmbiguous => ambiguous || unreliable || (lease is not null && candidateId is not null);
    internal ulong? CandidateId => candidateId;

    // Ownership of operationLease transfers only on true. The caller releases
    // a refused lease; no native navigation has been requested at that point.
    internal bool TryBegin(IDisposable operationLease)
    {
        ArgumentNullException.ThrowIfNull(operationLease);
        if (retired.Count + unowned.Count >= MaximumRememberedIds) unreliable = true;
        if (lease is not null || unreliable || unowned.Count != 0) return false;
        lease = operationLease;
        candidateId = null;
        ambiguous = false;
        observedStart = false;
        dispatchAttempted = false;
        return true;
    }

    // Callers must invoke WebView navigation through this method, so the
    // irreversible boundary is marked BEFORE control enters WebView2. A
    // synchronous exception may occur after native work has been queued.
    internal void Dispatch(Action request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (lease is null || dispatchAttempted) throw new InvalidOperationException("Navigation dispatch unavailable.");
        dispatchAttempted = true;
        request();
    }

    internal NavigationStartObservation ObserveStart(ulong id, bool expectedUri, bool redirected, bool closing)
    {
        if (lease is null)
        {
            RememberUnowned(id);
            return new(true, "UNOWNED_START");
        }
        observedStart = true;
        dispatchAttempted = true;
        if (retired.Contains(id))
        {
            ambiguous = true;
            return new(true, "STALE_START");
        }
        if (candidateId is null)
        {
            if (!expectedUri || redirected || unowned.Count != 0 || ambiguous || unreliable)
            {
                ambiguous = true;
                RememberUnowned(id);
                return new(true, "AMBIGUOUS_START");
            }
            candidateId = id;
            return new(closing, closing ? "CANDIDATE_START_CANCELLED" : "CANDIDATE_START");
        }
        if (candidateId.Value != id)
        {
            ambiguous = true;
            RememberUnowned(id);
            return new(true, "OVERLAPPING_START");
        }
        if (!redirected)
        {
            ambiguous = true;
            return new(true, "DUPLICATE_START");
        }
        return new(closing || !expectedUri || ambiguous || unreliable,
            closing || !expectedUri || ambiguous || unreliable ? "CANDIDATE_REDIRECT_CANCELLED" : "CANDIDATE_REDIRECT");
    }

    internal NavigationCompletionObservation ObserveCompletion(ulong id)
    {
        if (retired.Contains(id)) return new(false, "STALE_COMPLETION");
        if (lease is not null && candidateId == id)
        {
            if (ambiguous || unreliable) return new(false, "AMBIGUOUS_COMPLETION");
            RememberRetired(id);
            ambiguous = true;
            return new(false, unreliable ? "ID_CAPACITY_EXCEEDED" : "CANDIDATE_TERMINAL_UNVERIFIED");
        }
        if (unowned.Remove(id)) RememberRetired(id);
        else if (lease is not null) ambiguous = true;
        return new(false, "UNMATCHED_COMPLETION");
    }

    // Release only when the caller did not enter Dispatch and no native start
    // was observed. Once dispatch is attempted, a thrown exception or deadline
    // is not proof that native work was cancelled or never queued.
    internal bool FailSynchronousRequest()
    {
        if (lease is null) return false;
        if (dispatchAttempted || observedStart || candidateId is not null || ambiguous || unreliable)
        {
            ambiguous = true;
            return false;
        }
        Release();
        return true;
    }

    private void RememberUnowned(ulong id)
    {
        if (retired.Contains(id)) { unreliable = true; return; }
        if (unowned.Contains(id)) { unreliable = true; return; }
        if (retired.Count + unowned.Count >= MaximumRememberedIds) { unreliable = true; return; }
        unowned.Add(id);
    }

    private void RememberRetired(ulong id)
    {
        if (retired.Count + unowned.Count >= MaximumRememberedIds) { unreliable = true; return; }
        retired.Add(id);
    }

    private void Release()
    {
        var current = lease;
        lease = null;
        candidateId = null;
        current?.Dispose();
    }
}
