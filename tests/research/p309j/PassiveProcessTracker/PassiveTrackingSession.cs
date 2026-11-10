using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AegisBrowser.P309J;

// Owns a duplicate of the already-verified root handle. This avoids racing
// the supervisor's disposal of its original handle while scanning. The
// worker is passive and bounded; sampling gaps always make accounting partial.
public sealed class PassiveTrackingSession
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeProcessHandle sourceHandle,
        IntPtr targetProcess, out SafeProcessHandle targetHandle, uint access, bool inherit, uint options);

    private readonly SafeProcessHandle rootCopy;
    private readonly PassiveProcessTracker tracker;
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource<bool> firstScan = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task worker;
    private int finished;

    private PassiveTrackingSession(SafeProcessHandle rootCopy, PassiveProcessTracker tracker)
    {
        this.rootCopy = rootCopy;
        this.tracker = tracker;
        worker = Task.Run(ScanLoop);
    }

    public static PassiveTrackingSession Start(SafeProcessHandle root, uint rootPid,
        long rootCreationFileTimeUtc, string expectedImage)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (root == null || root.IsClosed || root.IsInvalid) throw new ArgumentException("ROOT_HANDLE_INVALID");
        var current = GetCurrentProcess();
        if (!DuplicateHandle(current, root, current, out var copy, 0, false, 2))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            return new PassiveTrackingSession(copy, new PassiveProcessTracker(copy, rootPid,
                rootCreationFileTimeUtc, expectedImage));
        }
        catch { copy.Dispose(); throw; }
    }

    private async Task ScanLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                bool scanned = tracker.Scan();
                firstScan.TrySetResult(scanned);
                if (!scanned) return;
                await Task.Delay(200, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { firstScan.TrySetResult(false); }
    }

    // A test or caller may await the first attempt without sleeping. False
    // means no successful first scan and never means no children existed.
    public Task<bool> FirstScanAsync() => firstScan.Task;

    // Must be awaited exactly once. It stops the worker, reports only observed
    // lifetimes and releases its handles; it never kills any process.
    public async Task<PassiveProcessTracker.Report> FinishAsync()
    {
        if (Interlocked.Exchange(ref finished, 1) != 0) throw new InvalidOperationException("ALREADY_FINISHED");
        stop.Cancel();
        try
        {
            await worker.ConfigureAwait(false);
            return tracker.Finish();
        }
        finally
        {
            tracker.Dispose();
            rootCopy.Dispose();
            stop.Dispose();
        }
    }
}
