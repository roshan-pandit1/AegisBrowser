using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AegisBrowser.WebView2Feasibility;

internal enum PrototypeLifecycleState { NotStarted, Initializing, Ready, Closing, Closed, Failed }
internal sealed record DisposalFailure(string Category, int HResult);

// Tracks only application-owned operation completion. A timeout does not cancel
// native work, dispose a browser, or imply that a subprocess has stopped.
internal sealed class LifecycleCoordinator
{
    private readonly object gate = new();
    private PrototypeLifecycleState state = PrototypeLifecycleState.NotStarted;
    private TaskCompletionSource<bool>? drained;
    private int active;
    private bool initializationPending;
    private bool disposeAttempted;
    private int? initializationError;

    internal PrototypeLifecycleState State { get { lock (gate) return state; } }
    internal int? InitializationError { get { lock (gate) return initializationError; } }
    internal int ActiveCount { get { lock (gate) return active; } }

    internal bool TryBeginInitialization()
    {
        lock (gate)
        {
            if (state != PrototypeLifecycleState.NotStarted) return false;
            state = PrototypeLifecycleState.Initializing;
            initializationPending = true;
            BeginUnderLock();
            return true;
        }
    }

    internal bool FinishInitialization(bool success, int? errorCode)
    {
        TaskCompletionSource<bool>? signal;
        lock (gate)
        {
            if (!initializationPending) return false;
            initializationPending = false;
            if (!success) initializationError = errorCode;
            if (state == PrototypeLifecycleState.Initializing)
                state = success ? PrototypeLifecycleState.Ready : PrototypeLifecycleState.Failed;
            signal = FinishUnderLock();
        }
        signal?.TrySetResult(true);
        return true;
    }

    internal bool TryBeginOperation(out IDisposable? lease) => TryBegin(false, out lease);
    internal bool TryBeginNavigation(out IDisposable? lease) => TryBegin(true, out lease);

    private bool TryBegin(bool allowInitializing, out IDisposable? lease)
    {
        lock (gate)
        {
            lease = null;
            if (state != PrototypeLifecycleState.Ready &&
                !(allowInitializing && state == PrototypeLifecycleState.Initializing)) return false;
            BeginUnderLock();
            lease = new OperationLease(this);
            return true;
        }
    }

    internal bool TryBeginClose()
    {
        lock (gate)
        {
            if (disposeAttempted || state == PrototypeLifecycleState.Closing ||
                state == PrototypeLifecycleState.Closed) return false;
            state = PrototypeLifecycleState.Closing;
            return true;
        }
    }

    // Caller supplies a bounded deadline task. False means incomplete, not
    // cancelled. A later explicit request can wait again without re-closing.
    internal async Task<bool> WaitForQuiescenceAsync(Task deadline)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        Task pending;
        lock (gate)
        {
            if (state != PrototypeLifecycleState.Closing) return false;
            pending = active == 0 ? Task.CompletedTask : drained!.Task;
        }
        if (pending.IsCompleted) return true;
        await Task.WhenAny(pending, deadline);
        return pending.IsCompleted;
    }

    // Null means disposal was refused (not closing, active work, or duplicate).
    // Each callback is attempted independently, outside the coordinator lock.
    internal IReadOnlyList<DisposalFailure>? DisposeAll(IReadOnlyList<Action> disposeActions)
    {
        ArgumentNullException.ThrowIfNull(disposeActions);
        if (!TryBeginDisposal()) return null;
        var failures = new List<DisposalFailure>();
        foreach (var dispose in disposeActions)
        {
            try { dispose(); }
            catch (Exception error) { failures.Add(new DisposalFailure(error.GetType().Name, error.HResult)); }
        }
        FinishDisposal(failures.Count == 0);
        return failures;
    }

    // The form may await a bounded diagnostic flush between control disposals.
    // This reserves disposal once while retaining the original quiescence guard.
    internal bool TryBeginDisposal()
    {
        lock (gate)
        {
            if (state != PrototypeLifecycleState.Closing || active != 0 || disposeAttempted) return false;
            disposeAttempted = true;
            return true;
        }
    }

    internal void FinishDisposal(bool success)
    {
        lock (gate)
        {
            if (!disposeAttempted || state != PrototypeLifecycleState.Closing)
                throw new InvalidOperationException("Disposal was not reserved.");
            state = success ? PrototypeLifecycleState.Closed : PrototypeLifecycleState.Failed;
        }
    }

    private void BeginUnderLock()
    {
        if (active++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private TaskCompletionSource<bool>? FinishUnderLock() => --active == 0 ? drained : null;

    private void EndOperation()
    {
        TaskCompletionSource<bool>? signal;
        lock (gate) signal = FinishUnderLock();
        signal?.TrySetResult(true);
    }

    private sealed class OperationLease : IDisposable
    {
        private LifecycleCoordinator? owner;
        internal OperationLease(LifecycleCoordinator owner) => this.owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.EndOperation();
    }
}
