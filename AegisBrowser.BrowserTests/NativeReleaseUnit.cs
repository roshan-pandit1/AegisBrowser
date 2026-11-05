using AegisBrowser;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Simulated lifecycle signals: no native windows, CEF initialization or profiles.
    private static async Task<int> NativeReleaseUnit()
    {
        try
        {
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var destroyed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var destroyCount = 0;
            var release = NativeHostRelease.RunAsync(ready.Task, () => { destroyCount++; destroyed.TrySetResult(true); }, closed.Task);
            Check(!release.IsCompleted && destroyCount == 0, "missing ready callback retains host");
            var warned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = NativeCloseWaiter.WaitAsync(release, TimeSpan.FromMilliseconds(20), () => warned.TrySetResult(true));
            await warned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!release.IsCompleted && !observed.IsCompleted && destroyCount == 0, "warning cannot release host without ready callback");
            ready.SetResult(true);
            await destroyed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(destroyCount == 1 && !release.IsCompleted, "ready callback releases handle once but waits for native close");
            Check(!ready.TrySetResult(true) && destroyCount == 1, "duplicate ready cannot destroy twice");
            closed.SetResult(true);
            await observed.WaitAsync(TimeSpan.FromSeconds(5));
            Check(release.IsCompletedSuccessfully && destroyCount == 1, "late native closure completes release");

            var absentReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var alreadyClosed = Task.CompletedTask;
            var count = 0;
            var unresolved = NativeHostRelease.RunAsync(absentReady.Task, () => count++, alreadyClosed);
            Check(!unresolved.IsCompleted && count == 0, "closed without ready remains unresolved");
            var fault = new InvalidOperationException("synthetic handle failure");
            try { await NativeHostRelease.RunAsync(Task.CompletedTask, () => throw fault, Task.CompletedTask); }
            catch (InvalidOperationException ex) when (ReferenceEquals(ex, fault))
            {
                Check(true, "handle failure propagates without false release");
                Console.WriteLine("PASS: 7 native release ordering checks");
                return 0;
            }
            throw new Exception("Handle failure was suppressed.");
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
