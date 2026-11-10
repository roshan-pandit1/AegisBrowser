using System.IO.Pipes;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using AegisBrowser.P309G;
using AegisBrowser.P309H;
using Microsoft.Win32.SafeHandles;

namespace P309g;

// An optional, per-invocation research channel. The endpoint name is random,
// and Windows reports the connected client PID. The payload is only a claim
// from the test client. Even a live direct child with the expected image is
// not independent proof of a CEF renderer role or sandbox enforcement.
internal sealed class RendererPipeObserver : IDisposable
{
    internal sealed record Evidence(string Status, string Reason, uint? CandidatePid,
        long? CandidateCreationFileTimeUtc, string RoleEvidence, TokenObservation? Token,
        string RendererIdentity = "INCONCLUSIVE", string SandboxEnforcement = "INCONCLUSIVE",
        string SubprocessAccounting = "INCONCLUSIVE", string SubprocessCleanup = "INCONCLUSIVE",
        string CandidateExit = "NOT_OBSERVED", uint? CandidateExitCode = null,
        P309j.PipeDaclProbe.Result? PipeDaclDiagnostic = null);

    private const string Prefix = @"\\.\pipe\aegis-p309i-";
    private const string PidPrefix = "NATIVE_DIAGNOSTIC: P309I_RENDERER_PID=";
    private static readonly byte[] Frame = Encoding.ASCII.GetBytes("P309I_RENDERER_V1\n");
    private const uint PipeAccessDuplex = 0x00000003, FileFlagOverlapped = 0x40000000,
        FirstPipeInstance = 0x00080000, RejectRemoteClients = 0x00000008;
    private const uint QueryInformation = 0x0400, QueryLimitedInformation = 0x1000, Synchronize = 0x100000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision,
        out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBuffer, uint inBuffer, uint defaultTimeout, ref SecurityAttributes security);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientPid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation,
        out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        internal uint Size, Usage, Pid;
        internal UIntPtr Heap;
        internal uint Module, Threads, ParentPid;
        internal int Priority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Image;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);

    private readonly NamedPipeServerStream pipe;
    private readonly string expectedUserSid;
    private readonly CancellationTokenSource stop = new();
    private readonly object candidateLock = new();
    private SafeProcessHandle? acceptedCandidate;
    private bool disposed;
    private bool attemptedCandidate, sawClaim, invalidClaim;
    private uint claimPid;
    private ProcessRunner.Identity? sourceParent;
    private SafeProcessHandle? sourceParentHandle;
    private Task<Evidence>? claimTask;
    private readonly TaskCompletionSource<Evidence> testClaimObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<Evidence>? task;
    internal string FullName { get; }

    private RendererPipeObserver(string fullName, NamedPipeServerStream pipe, string expectedUserSid)
    { FullName = fullName; this.pipe = pipe; this.expectedUserSid = expectedUserSid; }

    internal static RendererPipeObserver Create()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid)) throw new InvalidOperationException("CURRENT_USER_SID_UNAVAILABLE");
        string name = Prefix + Guid.NewGuid().ToString("N");
        // Restricted and AppContainer tokens require their own access-check
        // ACEs; an untrusted mandatory label permits a sandboxed client to
        // write. Same-user identity is rechecked by pipe impersonation before
        // any process is opened. The random name is not an authorization credential. Hostile
        // same-user or privileged local code is outside this test trust model.
        string sddl = "D:P(A;;GA;;;" + sid + ")(A;;GRGW;;;RC)(A;;GRGW;;;AC)S:(ML;;NW;;;S-1-16-0)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            var handle = CreateNamedPipeW(name, PipeAccessDuplex | FileFlagOverlapped | FirstPipeInstance,
                RejectRemoteClients, 1, 256, 256, 0, ref attributes);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new System.ComponentModel.Win32Exception(error); }
            try { return new RendererPipeObserver(name, new NamedPipeServerStream(PipeDirection.InOut, true, false, handle), sid); }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }

    internal void Start(ProcessRunner.Identity parent, SafeProcessHandle retainedParent)
    {
        if (task != null) throw new InvalidOperationException("OBSERVER_ALREADY_STARTED");
        task = ObserveAsync(parent, retainedParent);
        lock (candidateLock)
        {
            sourceParent = parent;
            sourceParentHandle = retainedParent;
            StartClaimLocked();
        }
    }

    // This is a lossy, spoofable producer claim on inherited stdout, not an
    // authenticated renderer message. Only one strictly formed claim is
    // considered; duplicates or malformed claims invalidate the observation.
    internal void AcceptLine(string line)
    {
        if (!line.StartsWith(PidPrefix, StringComparison.Ordinal)) return;
        string number = line.Substring(PidPrefix.Length);
        bool valid = number.Length is > 0 and <= 10 && number[0] != '0' &&
            uint.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out _);
        lock (candidateLock)
        {
            if (disposed) return;
            if (sawClaim || !valid) { invalidClaim = true; sawClaim = true; return; }
            sawClaim = true;
            claimPid = uint.Parse(number, CultureInfo.InvariantCulture);
            StartClaimLocked();
        }
    }

    private void StartClaimLocked()
    {
        if (!sawClaim || invalidClaim || sourceParent == null || sourceParentHandle == null || claimTask != null) return;
        var parent = sourceParent;
        var handle = sourceParentHandle;
        uint pid = claimPid;
        claimTask = Task.Run(() => {
            try
            {
                var result = ObservePid(pid, parent, handle, "CLIENT_PID_CLAIM");
                testClaimObserved.TrySetResult(result);
                return result;
            }
            catch (Exception ex)
            {
                testClaimObserved.TrySetException(ex);
                throw;
            }
        });
    }

    private static bool SnapshotShowsParent(uint pid, uint parentPid)
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snapshot.IsInvalid) return false;
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Image = "" };
        if (!Process32FirstW(snapshot, ref entry)) return false;
        do { if (entry.Pid == pid) return entry.ParentPid == parentPid; }
        while (Process32NextW(snapshot, ref entry));
        return false;
    }

    private async Task<Evidence> ObserveAsync(ProcessRunner.Identity parent, SafeProcessHandle parentHandle)
    {
        if (!OperatingSystem.IsWindows()) return Failure("WINDOWS_REQUIRED");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
            byte[] bytes = new byte[Frame.Length];
            int read = 0;
            while (read < bytes.Length)
            {
                int count = await pipe.ReadAsync(bytes.AsMemory(read), deadline.Token).ConfigureAwait(false);
                if (count == 0) return Failure("CLIENT_FRAME_TRUNCATED");
                read += count;
            }
            if (!bytes.AsSpan().SequenceEqual(Frame)) return Failure("CLIENT_FRAME_INVALID");
            string? clientUserSid = null;
            pipe.RunAsClient(() => {
                if (!OperatingSystem.IsWindows()) return;
                using var identity = WindowsIdentity.GetCurrent(true);
                clientUserSid = identity?.User?.Value;
            });
            if (!string.Equals(clientUserSid, expectedUserSid, StringComparison.Ordinal))
                return Failure("CLIENT_USER_MISMATCH");
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid == 0)
                return Failure("CLIENT_PID_UNAVAILABLE");
            return ObservePid(pid, parent, parentHandle, "CLIENT_CLAIM_ONLY");
        }
        catch (OperationCanceledException) { return Failure("OBSERVATION_TIMEOUT_OR_PARENT_EXIT"); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or
            UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        { return Failure("OBSERVATION_FAILED_" + ex.GetType().Name); }
    }

    private Evidence ObservePid(uint pid, ProcessRunner.Identity parent, SafeProcessHandle parentHandle, string channel)
    {
        try
        {
            // Snapshot prefilter avoids opening an arbitrary claimed PID. It is
            // still subject to PID reuse between the snapshot and OpenProcess;
            // even matching held-handle lineage cannot authenticate a role or
            // prove which lifetime made the original pipe/stdout claim.
            if (!SnapshotShowsParent(pid, checked((uint)parent.Pid))) return Failure("PARENT_SNAPSHOT_MISMATCH");
            lock (candidateLock)
            {
                if (disposed || attemptedCandidate) return Failure("CANDIDATE_ALREADY_ATTEMPTED");
                attemptedCandidate = true;
            }
            var candidate = OpenProcess(QueryInformation | Synchronize, false, pid);
            if (candidate.IsInvalid)
            {
                candidate.Dispose();
                candidate = OpenProcess(QueryLimitedInformation | Synchronize, false, pid);
            }
            bool retained = false;
            try
            {
                if (candidate.IsInvalid) return Failure("CLIENT_HANDLE_UNAVAILABLE");
                if (!GetProcessTimes(candidate, out long created, out _, out _, out _) || created <= 0)
                    return Failure("CLIENT_CREATION_TIME_UNAVAILABLE");
                var parentId = new ProcessIdentity(checked((uint)parent.Pid), parent.CreationFileTimeUtc, parent.ImagePath);
                // CEF's selected bootstrap is both browser and subprocess image.
                var childId = new ProcessIdentity(pid, created, parent.ImagePath);
                var result = OwnedProcessLineage.Observe(parentHandle, parentId, candidate, childId);
                if (result.Status != "OBSERVED" || result.Token?.Status != "OBSERVED")
                    return Failure("LINEAGE_OR_TOKEN_" + result.Reason);
                // Comparison of the current pipe DACL against a copied primary
                // token only. It cannot determine why the earlier renderer
                // CreateFile failed, its effective thread token, MIC or broker.
                var daclDiagnostic = P309j.PipeDaclProbe.Check(pipe.SafePipeHandle, candidate);
                lock (candidateLock)
                {
                    if (disposed) return Failure("OBSERVER_DISPOSED");
                    acceptedCandidate = candidate;
                    retained = true;
                }
                return new Evidence("OBSERVED", "LIVE_DIRECT_CHILD_TOKEN_AND_CLAIM", pid, created,
                    channel, result.Token, PipeDaclDiagnostic: daclDiagnostic);
            }
            finally { if (!retained) candidate.Dispose(); }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or
            UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or System.Security.SecurityException or OverflowException)
        { return Failure("OBSERVATION_FAILED_" + ex.GetType().Name); }
    }

    internal async Task<Evidence> FinishAsync()
    {
        // Parent has exited or supervision has ended. Never wait indefinitely
        // for a missing client or turn cancellation into a successful result.
        if (task == null) return Failure("PARENT_IDENTITY_UNAVAILABLE");
        if (!task.IsCompleted)
        {
            try { stop.Cancel(); }
            catch (ObjectDisposedException) { /* Dispose already canceled the observer. */ }
        }
        var evidence = await task.ConfigureAwait(false);
        Task<Evidence>? fallback;
        lock (candidateLock) fallback = claimTask;
        if (fallback != null)
        {
            var claim = await fallback.ConfigureAwait(false);
            if (evidence.Status != "OBSERVED") evidence = claim;
        }
        lock (candidateLock)
        {
            if (invalidClaim) evidence = Failure("PID_CLAIM_MALFORMED_OR_DUPLICATE");
            if (acceptedCandidate == null || acceptedCandidate.IsClosed) return evidence;
            try
            {
                uint wait = WaitForSingleObject(acceptedCandidate, 0);
                if (wait == 0)
                    return GetExitCodeProcess(acceptedCandidate, out uint code)
                        ? evidence with { CandidateExit = "EXITED", CandidateExitCode = code }
                        : evidence with { CandidateExit = "EXITED_CODE_UNAVAILABLE" };
                return evidence with { CandidateExit = wait == 258 ? "STILL_RUNNING" : "UNAVAILABLE" };
            }
            finally { acceptedCandidate.Dispose(); }
        }
    }

    // Used only by the owned-process test; production waits alongside the
    // normal CEF lifecycle and cancels at parent exit.
    internal Task<Evidence> WaitForEvidenceAsync() => task ?? Task.FromResult(Failure("PARENT_IDENTITY_UNAVAILABLE"));

    // Tests coordinate an owned helper's release after its bounded claim
    // observation. Production never awaits this synthetic barrier.
    internal Task<Evidence> WaitForClaimEvidenceAsync() => testClaimObserved.Task;

    internal bool CandidateHandleClosedForTest
    { get { lock (candidateLock) return acceptedCandidate?.IsClosed == true; } }

    internal static Evidence Failure(string reason) => new("INCONCLUSIVE", reason, null, null, "NOT_ESTABLISHED", null);

    public void Dispose()
    {
        lock (candidateLock)
        {
            if (disposed) return;
            disposed = true;
            acceptedCandidate?.Dispose();
        }
        stop.Cancel(); pipe.Dispose(); stop.Dispose();
    }
}
