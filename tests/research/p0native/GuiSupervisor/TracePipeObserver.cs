using System.Globalization;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using AegisBrowser.P309G;
using AegisBrowser.P309J;
using Microsoft.Win32.SafeHandles;

namespace P309g;

// Test-only browser writer. The kernel authenticates the pipe client as the
// retained browser, not a renderer. Neither matching its report to a retained
// child nor a trace frame ID establishes sandbox enforcement or live role.
internal sealed class TracePipeObserver : IDisposable
{
    internal sealed record Evidence(string Status, string Reason, uint? CandidatePid = null,
        long? CandidateCreationFileTimeUtc = null, int Barrier = 0,
        string RendererIdentity = "INCONCLUSIVE", string SandboxEnforcement = "INCONCLUSIVE", TraceTokenEvidence? Token = null,
        TraceTokenEvidence? TokenAfter = null);
    internal sealed record TraceTokenEvidence(string Status, string Reason, uint? ProcessId = null,
        long? CreationFileTimeUtc = null, uint? IntegrityRid = null, bool? IsAppContainer = null,
        uint? DenyOnlyGroupCount = null, uint? RestrictingSidCount = null, bool? HasRestrictingSids = null,
        uint? PrivilegeCount = null, uint? EnabledPrivilegeCount = null, bool? InAnyJob = null,
        bool? Win32kSystemCallsDisabled = null, uint? SystemCallMitigationFlags = null,
        string ProcessRole = "NOT_ESTABLISHED", string SandboxEnforcement = "INCONCLUSIVE",
        long? QpcStart = null, long? QpcEnd = null, long? QpcFrequency = null);
    private const string Prefix = "aegis-p0trace-";
    private readonly NamedPipeServerStream pipe;
    private readonly CancellationTokenSource stop = new();
    private Task<Evidence>? task;
    private bool disposed;
    internal string FullName { get; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    private TracePipeObserver(string name, NamedPipeServerStream pipe)
    { FullName = @"\\.\pipe\" + name; this.pipe = pipe; }

    internal static TracePipeObserver Create()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string name = Prefix + Guid.NewGuid().ToString("N");
        return new(name, new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 128, 128));
    }

    internal void Start(ProcessRunner.Identity browser, SafeProcessHandle retainedBrowser, PassiveTrackingSession tracking)
    {
        if (task != null) throw new InvalidOperationException("TRACE_OBSERVER_ALREADY_STARTED");
        task = ObserveAsync(browser, retainedBrowser, tracking);
    }

    private static Evidence Failure(string reason) => new("INCONCLUSIVE", reason);
    private static async Task<string?> ReadLine(Stream stream, CancellationToken cancel)
    {
        byte[] one = new byte[1];
        var bytes = new List<byte>(96);
        while (bytes.Count < 96)
        {
            if (await stream.ReadAsync(one, cancel).ConfigureAwait(false) == 0) return null;
            if (one[0] == (byte)'\n') return Encoding.ASCII.GetString(bytes.ToArray());
            if (one[0] < 0x20 || one[0] > 0x7e) throw new InvalidDataException("TRACE_NON_ASCII");
            bytes.Add(one[0]);
        }
        throw new InvalidDataException("TRACE_LINE_LIMIT");
    }

    private async Task<Evidence> ObserveAsync(ProcessRunner.Identity browser, SafeProcessHandle retainedBrowser, PassiveTrackingSession tracking)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint client) || client != (uint)browser.Pid ||
                retainedBrowser.IsClosed || retainedBrowser.IsInvalid || GetProcessId(retainedBrowser) != client ||
                WaitForSingleObject(retainedBrowser, 0) != 258 ||
                !GetProcessTimes(retainedBrowser, out long created, out _, out _, out _) || created != browser.CreationFileTimeUtc)
                return Failure("TRACE_CLIENT_NOT_RETAINED_BROWSER");
            if (await ReadLine(pipe, deadline.Token).ConfigureAwait(false) != "P0TRACE1 HELLO") return Failure("TRACE_HELLO_INVALID");
            if (!await tracking.FirstScanAsync().WaitAsync(deadline.Token).ConfigureAwait(false)) return Failure("TRACE_TRACKER_NOT_READY");
            int barrier = tracking.CaptureBarrier();
            if (barrier <= 0) return Failure("TRACE_BARRIER_UNAVAILABLE");
            await pipe.WriteAsync(Encoding.ASCII.GetBytes("P0TRACE1 ACK\n"), deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            string? line = await ReadLine(pipe, deadline.Token).ConfigureAwait(false);
            var fields = line?.Split(' ');
            if (fields == null || fields.Length != 3 || fields[0] != "P0TRACE1" ||
                fields[1].Length is < 1 or > 10 || fields[1][0] == '0' ||
                !uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint pid) || pid == 0 || pid > int.MaxValue ||
                fields[2].Length != 32 || fields[2].Any(c => !((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))))
                return Failure("TRACE_MESSAGE_INVALID");
            var match = tracking.DuplicateMatchBeforeBarrier(pid, barrier, out var candidate);
            using var candidateLifetime = candidate;
            TraceTokenEvidence? tokenEvidence = null;
            if (match.Status == "OBSERVED" && candidate != null && match.CreationFileTimeUtc is long candidateCreated)
                tokenEvidence = ObserveToken(candidate, pid, candidateCreated, browser.ImagePath);
            await pipe.WriteAsync(Encoding.ASCII.GetBytes("P0TRACE1 RECEIVED\n"), deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            string? second = await ReadLine(pipe, deadline.Token).ConfigureAwait(false);
            if (second == null) return Failure("TRACE_REVALIDATION_MISSING");
            if (second == line) return Failure("TRACE_DUPLICATE_MESSAGE");
            if (!string.Equals(second, "P0TRACE2 " + fields[1] + " " + fields[2], StringComparison.Ordinal))
                return Failure("TRACE_REVALIDATION_MISMATCH");
            var after = tracking.MatchBeforeBarrier(pid, barrier);
            TraceTokenEvidence? tokenAfter = null;
            if (after.Status == "OBSERVED" && after.CreationFileTimeUtc == match.CreationFileTimeUtc &&
                candidate != null && match.CreationFileTimeUtc is long afterCreated)
                tokenAfter = ObserveToken(candidate, pid, afterCreated, browser.ImagePath);
            await pipe.WriteAsync(Encoding.ASCII.GetBytes("P0TRACE2 RECEIVED\n"), deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            string? final = await ReadLine(pipe, deadline.Token).ConfigureAwait(false);
            if (final == null) return Failure("TRACE_FINALIZATION_MISSING");
            if (final != "P0TRACE3 STABLE") return Failure("TRACE_FINALIZATION_INVALID");
            await pipe.WriteAsync(Encoding.ASCII.GetBytes("P0TRACE3 RECEIVED\n"), deadline.Token).ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
            if (await ReadLine(pipe, deadline.Token).ConfigureAwait(false) != null) return Failure("TRACE_DUPLICATE_MESSAGE");
            if (match.Status != "OBSERVED") return Failure(match.Reason);
            if (after.Status != "OBSERVED") return Failure(after.Reason);
            return tokenEvidence != null && tokenAfter != null && after.CreationFileTimeUtc == match.CreationFileTimeUtc
                ? new("OBSERVED", match.Reason, match.Pid, match.CreationFileTimeUtc, barrier, Token: tokenEvidence, TokenAfter: tokenAfter)
                : Failure("TRACE_REVALIDATION_UNAVAILABLE");
        }
        catch (OperationCanceledException) { return Failure("TRACE_TIMEOUT_OR_PARENT_EXIT"); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        { return Failure("TRACE_OBSERVATION_FAILED_" + ex.GetType().Name); }
    }

    private static TraceTokenEvidence ObserveToken(SafeProcessHandle candidate, uint pid, long created, string image)
    {
        // This duplicates no PID lookup: the input is the held kernel object.
        long start = Stopwatch.GetTimestamp();
        var token = TokenObserver.Observe(candidate, pid, created, image);
        long end = Stopwatch.GetTimestamp();
        return token.Status == "OBSERVED" && token.ProcessId == pid && token.CreationFileTimeUtc == created
            ? new(token.Status, token.Reason, token.ProcessId, token.CreationFileTimeUtc, token.IntegrityRid,
                token.IsAppContainer, token.DenyOnlyGroupCount, token.RestrictingSidCount, token.HasRestrictingSids,
                token.PrivilegeCount, token.EnabledPrivilegeCount, token.InAnyJob, token.Win32kSystemCallsDisabled,
                token.SystemCallMitigationFlags, QpcStart: start, QpcEnd: end, QpcFrequency: Stopwatch.Frequency)
            : new("INCONCLUSIVE", token.Status == "OBSERVED" ? "TOKEN_IDENTITY_MISMATCH" : token.Reason);
    }

    internal async Task<Evidence> FinishAsync()
    {
        if (task == null) return Failure("TRACE_OBSERVER_NOT_STARTED");
        // Allow the already-closed client's final bytes/EOF to be processed;
        // otherwise shutdown races can mask duplicate messages as timeouts.
        if (!task.IsCompleted && await Task.WhenAny(task, Task.Delay(2000)).ConfigureAwait(false) != task)
            stop.Cancel();
        return await task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stop.Cancel();
        pipe.Dispose();
        stop.Dispose();
    }
}
