using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using AegisBrowser.P309G;

namespace AegisBrowser.P309H;

// This is an admission check for handles supplied by a test supervisor; it
// never enumerates processes or opens a PID. A direct-parent relationship is
// evidence of lineage, not proof of a renderer role or complete process tree.
// A hostile local process can spoof its parent; this is not an admin boundary.
public sealed record ProcessIdentity(uint Pid, long CreationFileTimeUtc, string ImagePath);

public sealed record LineageObservation(string Status, string Reason, TokenObservation? Token)
{
    public string RendererDiscovery => "INCONCLUSIVE";
    public string SubprocessCleanup => "INCONCLUSIVE";
    public string SandboxEnforcement => "INCONCLUSIVE";
}

public static class OwnedProcessLineage
{
    private const uint WaitTimeout = 258;
    private const int ProcessBasicInformation = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority;
        public UIntPtr UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass,
        out BasicInformation information, int length, out int returnLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation,
        out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint timeout);

    // Both handles must be retained by the caller for the duration. The
    // observer verifies the parent image as well as the candidate image via
    // TokenObserver; neither image-path comparison proves mapped-file hashes.
    public static LineageObservation Observe(SafeProcessHandle? parent, ProcessIdentity? expectedParent,
        SafeProcessHandle? candidate, ProcessIdentity? expectedCandidate)
    {
        if (!OperatingSystem.IsWindows()) return Fail("WINDOWS_REQUIRED");
        if (parent is null || candidate is null || parent.IsInvalid || parent.IsClosed ||
            candidate.IsInvalid || candidate.IsClosed || expectedParent is null || expectedCandidate is null ||
            expectedParent.Pid == 0 || expectedCandidate.Pid == 0 || expectedParent.Pid == expectedCandidate.Pid ||
            expectedParent.CreationFileTimeUtc <= 0 || expectedCandidate.CreationFileTimeUtc <= 0 ||
            expectedCandidate.CreationFileTimeUtc < expectedParent.CreationFileTimeUtc)
            return Fail("INVALID_IDENTITIES");
        try
        {
            if (!Matches(parent, expectedParent) || !Matches(candidate, expectedCandidate))
                return Fail("IDENTITY_OR_LIVENESS_MISMATCH");
            int size = Marshal.SizeOf<BasicInformation>();
            int status = NtQueryInformationProcess(candidate, ProcessBasicInformation, out var info, size, out int returned);
            if (status != 0 || returned != size) return Fail("PARENT_QUERY_UNAVAILABLE");
            if (info.UniqueProcessId.ToUInt64() != expectedCandidate.Pid ||
                info.InheritedFromUniqueProcessId.ToUInt64() != expectedParent.Pid)
                return Fail("PARENT_RELATIONSHIP_MISMATCH");

            // This intentionally performs no token collection for a candidate
            // until a live, handle-bound parent relationship was established.
            var parentSnapshot = TokenObserver.Observe(parent, expectedParent.Pid,
                expectedParent.CreationFileTimeUtc, expectedParent.ImagePath);
            if (parentSnapshot.Status != "OBSERVED") return Fail("PARENT_" + parentSnapshot.Reason);
            var token = TokenObserver.Observe(candidate, expectedCandidate.Pid,
                expectedCandidate.CreationFileTimeUtc, expectedCandidate.ImagePath);
            if (token.Status != "OBSERVED") return Fail("CANDIDATE_" + token.Reason);
            if (!Matches(parent, expectedParent) || !Matches(candidate, expectedCandidate))
                return Fail("IDENTITY_OR_LIVENESS_RECHECK_FAILED");
            return new LineageObservation("OBSERVED", "LIVE_DIRECT_CHILD_HANDLE_AND_TOKEN", token);
        }
        catch (Exception e) when (e is Win32Exception or ObjectDisposedException or ArgumentException or
            InvalidOperationException or System.Security.SecurityException)
        { return Fail("OBSERVATION_FAILED_" + e.GetType().Name); }
    }

    private static bool Matches(SafeProcessHandle handle, ProcessIdentity identity) =>
        WaitForSingleObject(handle, 0) == WaitTimeout && GetProcessId(handle) == identity.Pid &&
        GetProcessTimes(handle, out long created, out _, out _, out _) && created == identity.CreationFileTimeUtc;

    private static LineageObservation Fail(string reason) => new("INCONCLUSIVE", reason, null);
}
