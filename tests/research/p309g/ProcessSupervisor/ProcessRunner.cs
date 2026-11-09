using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices.ComTypes;

namespace P309g;

// Test-only process supervision. A process handle identifies the launched
// process across PID reuse; no descendant or sandbox guarantee is inferred.
internal static class ProcessRunner
{
    private const uint Signaled = 0, TimedOut = 258, Failed = 0xffffffff;
    private const int CaptureLimit = 65536;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint exitCode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle handle, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle handle, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle handle, uint exitCode);
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        internal uint Attributes;
        internal FILETIME CreationTime, LastAccessTime, LastWriteTime;
        internal uint VolumeSerialNumber, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);

    internal sealed record Identity(int Pid, long CreationFileTimeUtc, string ImagePath, string ImageSha256);
    internal sealed record Capture(string Text, bool Truncated, bool ValidUtf8);
    internal sealed record Result(string ProcessExit, uint? NativeExitCode, Identity? ProcessIdentity,
        bool ParentTerminationRequested, string ParentCleanup, bool OutputComplete,
        Capture Stdout, Capture Stderr, string SubprocessCleanup, string SandboxEnforcement, string Error);

    internal static FileStream OpenPinned(string path, string expectedHash)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("INPUT_PATH_INVALID");
        var full = Path.GetFullPath(path);
        if (new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
            throw new InvalidDataException("INPUT_NETWORK_DRIVE");
        if (full.AsSpan(2).Contains(':')) throw new InvalidDataException("INPUT_ALTERNATE_STREAM");
        if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit)) throw new InvalidDataException("INPUT_HASH_INVALID");
        var file = new FileInfo(full);
        if (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("INPUT_FILE_INVALID");
        for (var parent = file.Directory; parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("INPUT_REPARSE_POINT");
        // Deny subsequent write/delete sharing while the launched process is
        // observed. This is defense in depth, not a hostile-admin guarantee.
        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length is <= 0 or > 1073741824) throw new InvalidDataException("INPUT_SIZE_INVALID");
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info) || info.NumberOfLinks != 1)
                throw new InvalidDataException("INPUT_LINK_COUNT_INVALID");
            var actual = SHA256.HashData(stream);
            if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedHash)))
                throw new InvalidDataException("INPUT_HASH_MISMATCH");
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private sealed class CaptureState
    {
        private readonly object gate = new();
        private readonly MemoryStream retained = new();
        private bool truncated;
        internal void Append(byte[] buffer, int count)
        {
            lock (gate)
            {
                int keep = (int)Math.Min(count, CaptureLimit - retained.Length);
                if (keep > 0) retained.Write(buffer, 0, keep);
                if (keep != count) truncated = true;
            }
        }
        internal Capture Snapshot()
        {
            lock (gate)
            {
                try { return new Capture(new UTF8Encoding(false, true).GetString(retained.ToArray()), truncated, true); }
                catch (DecoderFallbackException) { return new Capture("", truncated, false); }
            }
        }
    }

    private static async Task DrainAsync(Stream stream, CaptureState state)
    {
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(buffer);
            if (count == 0) break;
            state.Append(buffer, count);
        }
    }

    private static Result Empty(string status, string error) => new(status, null, null, false, "NOT_APPLICABLE", false,
        new("", false, false), new("", false, false), "INCONCLUSIVE", "INCONCLUSIVE", error);

    internal static async Task<Result> RunAsync(string executable, string expectedHash, IReadOnlyList<string> arguments,
        string workingDirectory, int timeoutMs, bool terminateParentOnTimeout, Action<Identity, SafeProcessHandle>? onIdentity = null)
    {
        if (!OperatingSystem.IsWindows() || timeoutMs is < 1 or > 120000) return Empty("UNAVAILABLE", "UNSUPPORTED_OR_INVALID_TIMEOUT");
        FileStream? pinned = null;
        try { pinned = OpenPinned(executable, expectedHash); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or FormatException)
        { return Empty("UNAVAILABLE", e.Message); }
        using (pinned)
        using (var process = new Process())
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(executable), WorkingDirectory = workingDirectory,
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.StartInfo.Environment["AEGIS_P309F_EXECUTE_SANDBOX_TEST"] = "YES";
            try { if (!process.Start()) return Empty("UNAVAILABLE", "PROCESS_START_FAILED"); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            { return Empty("UNAVAILABLE", "PROCESS_START_FAILED:" + e.GetType().Name); }

            // Begin draining both pipes before any wait. Excess bytes are
            // discarded after the capture cap, but pipes continue draining.
            var stdoutState = new CaptureState();
            var stderrState = new CaptureState();
            var stdout = DrainAsync(process.StandardOutput.BaseStream, stdoutState);
            var stderr = DrainAsync(process.StandardError.BaseStream, stderrState);
            Identity? identity = null;
            string error = "";
            string disposition = "UNAVAILABLE", cleanup = "NOT_NEEDED";
            uint? code = null;
            bool terminationRequested = false;
            try
            {
                SafeProcessHandle handle = process.SafeHandle;
                var image = new StringBuilder(32768);
                uint length = (uint)image.Capacity;
                if (!GetProcessTimes(handle, out long created, out _, out _, out _) ||
                    !QueryFullProcessImageName(handle, 0, image, ref length) || created <= 0 ||
                    !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(executable), Path.GetFullPath(image.ToString())))
                    error = "PROCESS_IDENTITY_UNAVAILABLE_OR_MISMATCH";
                else
                {
                    identity = new Identity(process.Id, created, image.ToString(), expectedHash.ToUpperInvariant());
                    // The callback is synchronous and must not retain or close
                    // the owned handle; it may inspect only this launched PID.
                    try { onIdentity?.Invoke(identity, handle); }
                    catch (Exception e) { error = "IDENTITY_DELIVERY_FAILED:" + e.GetType().Name; }
                }

                uint wait = await Task.Run(() => WaitForSingleObject(handle, (uint)timeoutMs));
                if (wait == Signaled)
                {
                    if (!GetExitCodeProcess(handle, out uint observed)) { disposition = "UNAVAILABLE"; error = "EXIT_CODE_UNAVAILABLE"; }
                    else { code = observed; disposition = observed == 0 ? "NORMAL" : "NONZERO"; }
                }
                else if (wait == TimedOut)
                {
                    disposition = "TIMEOUT";
                    // Terminate only the exact launched process represented by
                    // its still-open handle. Child cleanup remains inconclusive.
                    if (terminateParentOnTimeout && identity != null)
                    {
                        terminationRequested = true;
                        bool terminated = TerminateProcess(handle, 0xE0000001);
                        cleanup = terminated && WaitForSingleObject(handle, 5000) == Signaled ? "PARENT_TERMINATED" : "PARENT_TERMINATION_FAILED";
                    }
                    else cleanup = "PARENT_NOT_TERMINATED";
                }
                else { disposition = "UNAVAILABLE"; error = wait == Failed ? "WAIT_FAILED" : "WAIT_UNEXPECTED"; }
                if (identity == null || error.Length != 0) disposition = disposition == "TIMEOUT" ? "TIMEOUT" : "UNAVAILABLE";
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException or ArgumentException)
            { disposition = "UNAVAILABLE"; error = "PROCESS_OBSERVATION_FAILED:" + e.GetType().Name; }

            // Descendants may inherit pipe handles after the parent has exited.
            // Never wait indefinitely for EOF or classify output as complete.
            var both = Task.WhenAll(stdout, stderr);
            bool complete = await Task.WhenAny(both, Task.Delay(3000)) == both && both.IsCompletedSuccessfully;
            if (!complete) { process.StandardOutput.Dispose(); process.StandardError.Dispose(); _ = both.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); }
            Capture outCapture = stdoutState.Snapshot(), errCapture = stderrState.Snapshot();
            return new Result(disposition, code, identity, terminationRequested, cleanup,
                complete && !outCapture.Truncated && !errCapture.Truncated && outCapture.ValidUtf8 && errCapture.ValidUtf8, outCapture, errCapture,
                "INCONCLUSIVE", "INCONCLUSIVE", error);
        }
    }
}
