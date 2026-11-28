using System;
using System.Threading.Tasks;

namespace AegisBrowser.WebView2Feasibility;

// In-memory state and synthetic callbacks only. No browser is started.
internal static class LifecycleCoordinatorTests
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("Lifecycle check " + (count + 1)); count++; }

        var normal = new LifecycleCoordinator();
        Check(normal.State == PrototypeLifecycleState.NotStarted);
        Check(!normal.TryBeginOperation(out _));
        Check(normal.TryBeginInitialization());
        Check(normal.State == PrototypeLifecycleState.Initializing);
        Check(!normal.TryBeginInitialization());
        Check(normal.FinishInitialization(true, null));
        Check(normal.State == PrototypeLifecycleState.Ready);
        Check(!normal.FinishInitialization(true, null));
        Check(normal.TryBeginOperation(out var first));
        Check(normal.TryBeginOperation(out var second));
        Check(normal.TryBeginClose());
        Check(!normal.TryBeginClose());
        Check(!normal.TryBeginOperation(out _));
        Check(!normal.TryBeginNavigation(out _));
        Check(normal.DisposeAll(new Action[] { () => { } }) is null);
        Check(!await normal.WaitForQuiescenceAsync(Task.CompletedTask));
        first!.Dispose();
        first.Dispose(); // duplicate release must not finish the second lease
        Check(!await normal.WaitForQuiescenceAsync(Task.CompletedTask));
        second!.Dispose();
        Check(await normal.WaitForQuiescenceAsync(Task.CompletedTask));
        var disposed = 0;
        var outcome = normal.DisposeAll(new Action[] { () => disposed++, () => disposed++ });
        Check(outcome is not null && outcome.Count == 0 && disposed == 2);
        Check(normal.State == PrototypeLifecycleState.Closed);
        Check(normal.DisposeAll(Array.Empty<Action>()) is null);

        var failure = new LifecycleCoordinator();
        Check(failure.TryBeginInitialization());
        Check(failure.FinishInitialization(false, unchecked((int)0x80004005)));
        Check(failure.State == PrototypeLifecycleState.Failed && failure.InitializationError == unchecked((int)0x80004005));
        Check(!failure.TryBeginOperation(out _));
        Check(failure.TryBeginClose());
        Check(await failure.WaitForQuiescenceAsync(Task.CompletedTask));
        var calls = 0;
        outcome = failure.DisposeAll(new Action[] { () => throw new InvalidOperationException(), () => calls++ });
        Check(outcome is not null && outcome.Count == 1 && calls == 1 &&
            outcome[0].Category == nameof(InvalidOperationException) &&
            outcome[0].HResult == new InvalidOperationException().HResult);
        Check(failure.State == PrototypeLifecycleState.Failed);

        var racing = new LifecycleCoordinator();
        Check(racing.TryBeginInitialization());
        Check(racing.TryBeginNavigation(out var navigation));
        Check(racing.TryBeginClose());
        Check(!await racing.WaitForQuiescenceAsync(Task.CompletedTask));
        Check(racing.FinishInitialization(true, null));
        Check(racing.State == PrototypeLifecycleState.Closing);
        var deadline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = racing.WaitForQuiescenceAsync(deadline.Task);
        Check(!wait.IsCompleted);
        navigation!.Dispose();
        Check(await wait);
        Check(racing.DisposeAll(Array.Empty<Action>()) is { Count: 0 });
        Check(racing.State == PrototypeLifecycleState.Closed);

        var early = new LifecycleCoordinator();
        Check(!early.FinishInitialization(true, null));
        Check(early.TryBeginClose());
        Check(!early.TryBeginInitialization());
        Check(await early.WaitForQuiescenceAsync(Task.CompletedTask));
        Check(early.DisposeAll(Array.Empty<Action>()) is { Count: 0 });
        return count;
    }
}
