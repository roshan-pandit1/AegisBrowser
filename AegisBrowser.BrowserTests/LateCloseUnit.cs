using AegisBrowser;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Pure task test: no CEF initialization, profiles, sockets or native windows.
    private static async Task<int> LateCloseUnit()
    {
        try
        {
            var nativeClosed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var warned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var wait = NativeCloseWaiter.WaitAsync(nativeClosed.Task, TimeSpan.FromMilliseconds(20), () => warned.TrySetResult(true));
            await warned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!wait.IsCompleted && !nativeClosed.Task.IsCompleted, "warning does not finish or fault pending native close");
            nativeClosed.SetResult(true);
            await wait.WaitAsync(TimeSpan.FromSeconds(5));
            Check(wait.IsCompletedSuccessfully, "late native close completes the original wait");

            var warningCount = 0;
            await NativeCloseWaiter.WaitAsync(Task.CompletedTask, TimeSpan.FromSeconds(1), () => warningCount++);
            Check(warningCount == 0, "completed close does not warn");

            var error = new InvalidOperationException("synthetic native close failure");
            try { await NativeCloseWaiter.WaitAsync(Task.FromException(error), TimeSpan.FromSeconds(1), () => warningCount++); }
            catch (InvalidOperationException ex) when (ReferenceEquals(ex, error))
            {
                Check(warningCount == 0, "native failures propagate without a false timeout warning");
                Console.WriteLine("PASS: 4 late native close checks");
                return 0;
            }
            throw new Exception("Native close failure was suppressed.");
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
