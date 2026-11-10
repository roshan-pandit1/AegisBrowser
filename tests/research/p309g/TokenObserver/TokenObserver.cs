using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AegisBrowser.P309G;

// Read-only, point-in-time evidence for a caller-owned process handle. This
// component never opens a PID, finds descendants, enables privileges, or
// establishes that a process is a renderer. Its caller must independently
// bind the handle to the approved experiment and establish the process role.
// A path comparison is not a mapped-image hash or protection against a
// malicious local administrator/filesystem replacement.
public sealed class TokenObservation
{
    public string Status { get; internal set; } = "INCONCLUSIVE";
    public string Reason { get; internal set; } = "NOT_OBSERVED";
    public string SandboxEnforcement { get; } = "INCONCLUSIVE";
    public string ProcessRole { get; } = "NOT_ESTABLISHED";
    public uint ProcessId { get; internal set; }
    public long CreationFileTimeUtc { get; internal set; }
    public string ObservationStartedUtc { get; internal set; } = "";
    public string ObservationCompletedUtc { get; internal set; } = "";
    public uint? IntegrityRid { get; internal set; }
    public bool? IsAppContainer { get; internal set; }
    public uint? GroupCount { get; internal set; }
    public uint? DenyOnlyGroupCount { get; internal set; }
    // A zero count does not prove an unrestricted token: restrictions may
    // also derive from disabled SIDs, deleted privileges, jobs or mitigations.
    public uint? RestrictingSidCount { get; internal set; }
    // IsTokenRestricted checks restricting SIDs only; it does not decide
    // whether a token has been restricted by other mechanisms.
    public bool? HasRestrictingSids { get; internal set; }
    public uint? PrivilegeCount { get; internal set; }
    public uint? EnabledPrivilegeCount { get; internal set; }
    public string[] EnabledPrivilegeNames { get; internal set; } = Array.Empty<string>();
    public bool? InAnyJob { get; internal set; }
    public bool? Win32kSystemCallsDisabled { get; internal set; }
    // Raw OS-reported flags; null means the corresponding query failed.
    // These are configuration observations, never sandbox-verification claims.
    public uint? SystemCallMitigationFlags { get; internal set; }
    public uint? AslrMitigationFlags { get; internal set; }
    public uint? ExtensionPointMitigationFlags { get; internal set; }
    public uint? ImageLoadMitigationFlags { get; internal set; }
}

public static class TokenObserver
{
    private const uint TokenQuery = 0x0008;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const int ErrorInsufficientBuffer = 122;
    private const int TokenGroups = 2, TokenPrivileges = 3, TokenRestrictedSids = 11;
    private const int TokenIntegrityLevel = 25, TokenIsAppContainer = 29;
    private const uint GroupUseForDenyOnly = 0x10, PrivilegeEnabled = 0x2;
    private const int MaximumTokenInformationBytes = 1024 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation,
        out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags,
        StringBuilder image, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, IntPtr job, out bool result);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessMitigationPolicy(SafeProcessHandle process, int policy,
        out uint flags, int length);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access,
        out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int tokenClass,
        IntPtr information, int length, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool IsTokenRestricted(SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool IsValidSid(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetLengthSid(IntPtr sid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeNameW(string? system, ref Luid luid,
        StringBuilder? name, ref int length);

    // The supplied handle must remain owned and open by the caller throughout
    // this call and have PROCESS_QUERY_LIMITED_INFORMATION and SYNCHRONIZE.
    // Exact creation FILETIME protects against stale PID reuse; a verified
    // renderer role and executable provenance must be established separately.
    public static TokenObservation Observe(SafeProcessHandle? process, uint expectedPid,
        long expectedCreationFileTimeUtc, string? expectedFullImagePath)
    {
        var result = new TokenObservation { ObservationStartedUtc = DateTime.UtcNow.ToString("O") };
        try
        {
            if (!OperatingSystem.IsWindows()) return Fail(result, "WINDOWS_REQUIRED");
            if (process is null || process.IsInvalid || process.IsClosed || expectedPid == 0 ||
                expectedCreationFileTimeUtc <= 0 || string.IsNullOrWhiteSpace(expectedFullImagePath) ||
                !Path.IsPathFullyQualified(expectedFullImagePath))
                return Fail(result, "INVALID_IDENTITY_INPUT");

            uint pid = GetProcessId(process);
            if (pid == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (pid != expectedPid) return Fail(result, "PID_MISMATCH");
            result.ProcessId = pid;
            RequireAlive(process);
            if (!GetProcessTimes(process, out long created, out _, out _, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (created != expectedCreationFileTimeUtc) return Fail(result, "CREATION_TIME_MISMATCH");
            result.CreationFileTimeUtc = created;
            var image = new StringBuilder(32768);
            int imageLength = image.Capacity;
            if (!QueryFullProcessImageNameW(process, 0, image, ref imageLength))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!string.Equals(Path.GetFullPath(image.ToString()), Path.GetFullPath(expectedFullImagePath), StringComparison.OrdinalIgnoreCase))
                return Fail(result, "IMAGE_PATH_MISMATCH");

            if (!OpenProcessToken(process, TokenQuery, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            using (token)
            {
                result.IntegrityRid = WithTokenInfo(token, TokenIntegrityLevel, ReadIntegrityRid);
                result.IsAppContainer = WithTokenInfo(token, TokenIsAppContainer, (pointer, length) =>
                    length >= sizeof(int) ? Marshal.ReadInt32(pointer) != 0 : throw new InvalidDataException());
                var groups = WithTokenInfo(token, TokenGroups, ReadGroupCounts);
                result.GroupCount = groups.count;
                result.DenyOnlyGroupCount = groups.denyOnly;
                result.RestrictingSidCount = WithTokenInfo(token, TokenRestrictedSids, ReadGroupCounts).count;
                // SetLastError=true resets the native last-error before the
                // call on .NET. False plus an error is unknown, not false.
                result.HasRestrictingSids = ReadRestrictingSidIndicator(token);
                var privileges = WithTokenInfo(token, TokenPrivileges, ReadPrivilegeCounts);
                result.PrivilegeCount = privileges.count;
                result.EnabledPrivilegeCount = (uint)privileges.names.Length;
                result.EnabledPrivilegeNames = privileges.names;
            }

            // Optional observations are not converted to false on access/API failure.
            result.InAnyJob = IsProcessInJob(process, IntPtr.Zero, out bool inJob) ? inJob : null;
            result.SystemCallMitigationFlags = ReadMitigationFlags(process, 4);
            result.AslrMitigationFlags = ReadMitigationFlags(process, 1);
            result.ExtensionPointMitigationFlags = ReadMitigationFlags(process, 6);
            result.ImageLoadMitigationFlags = ReadMitigationFlags(process, 10);
            result.Win32kSystemCallsDisabled = result.SystemCallMitigationFlags is uint flags
                ? (flags & 1) != 0 : null;
            RequireAlive(process);
            if (!GetProcessTimes(process, out long rechecked, out _, out _, out _) || rechecked != created)
                return Fail(result, "IDENTITY_RECHECK_FAILED");
            result.Status = "OBSERVED";
            result.Reason = "HANDLE_IDENTITY_AND_TOKEN_SNAPSHOT";
            return result;
        }
        catch (Win32Exception e) { return Fail(result, "WINDOWS_API_ERROR_" + e.NativeErrorCode); }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or
            InvalidDataException or OverflowException or ObjectDisposedException or NotSupportedException or
            System.Security.SecurityException)
        { return Fail(result, "OBSERVATION_FAILED_" + e.GetType().Name); }
        finally { result.ObservationCompletedUtc = DateTime.UtcNow.ToString("O"); }
    }

    private static TokenObservation Fail(TokenObservation result, string reason)
    { result.Status = "INCONCLUSIVE"; result.Reason = reason; return result; }

    private static uint? ReadMitigationFlags(SafeProcessHandle process, int policy) =>
        GetProcessMitigationPolicy(process, policy, out uint flags, sizeof(uint)) ? flags : null;

    private static bool? ReadRestrictingSidIndicator(SafeAccessTokenHandle token)
    {
        bool restricted = IsTokenRestricted(token);
        int error = Marshal.GetLastWin32Error();
        return restricted ? true : error == 0 ? false : null;
    }

    private static void RequireAlive(SafeProcessHandle process)
    {
        uint wait = WaitForSingleObject(process, 0);
        if (wait == WaitTimeout) return;
        if (wait == WaitObject0) throw new InvalidDataException("Process exited.");
        throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static T WithTokenInfo<T>(SafeAccessTokenHandle token, int type, Func<IntPtr, int, T> parse)
    {
        bool initial = GetTokenInformation(token, type, IntPtr.Zero, 0, out int size);
        if (initial || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || size <= 0 || size > MaximumTokenInformationBytes)
            throw new InvalidDataException("Invalid token information size.");
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, type, buffer, size, out int returned))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (returned <= 0 || returned > size) throw new InvalidDataException("Invalid token information length.");
            return parse(buffer, returned);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static uint ReadIntegrityRid(IntPtr buffer, int length)
    {
        if (length < IntPtr.Size) throw new InvalidDataException();
        IntPtr sid = Marshal.ReadIntPtr(buffer);
        long start = buffer.ToInt64(), position = sid.ToInt64();
        if (position < start || position > checked(start + length - 8))
            throw new InvalidDataException("Invalid integrity SID.");
        // Mandatory integrity labels are S-1-16-RID. Check the header and
        // bounds before asking Windows to interpret the returned SID pointer.
        int count = Marshal.ReadByte(sid, 1);
        if (Marshal.ReadByte(sid, 0) != 1 || count != 1 || position + 12 > start + length ||
            Marshal.ReadByte(sid, 2) != 0 || Marshal.ReadByte(sid, 3) != 0 ||
            Marshal.ReadByte(sid, 4) != 0 || Marshal.ReadByte(sid, 5) != 0 ||
            Marshal.ReadByte(sid, 6) != 0 || Marshal.ReadByte(sid, 7) != 16 || !IsValidSid(sid))
            throw new InvalidDataException("Invalid integrity SID authority.");
        uint sidLength = GetLengthSid(sid);
        if (sidLength < 12 || position + sidLength > start + length) throw new InvalidDataException("Invalid integrity SID length.");
        return unchecked((uint)Marshal.ReadInt32(sid, 8 + 4 * (count - 1)));
    }

    private static (uint count, uint denyOnly) ReadGroupCounts(IntPtr buffer, int length)
    {
        if (length < sizeof(uint)) throw new InvalidDataException();
        uint count = unchecked((uint)Marshal.ReadInt32(buffer));
        int first = IntPtr.Size == 8 ? 8 : 4, stride = IntPtr.Size == 8 ? 16 : 8;
        if (count > 8192 || (count != 0 && first + (long)count * stride > length)) throw new InvalidDataException();
        uint deny = 0;
        for (int i = 0; i < count; i++)
            if ((unchecked((uint)Marshal.ReadInt32(buffer, first + i * stride + IntPtr.Size)) & GroupUseForDenyOnly) != 0) deny++;
        return (count, deny);
    }

    private static (uint count, string[] names) ReadPrivilegeCounts(IntPtr buffer, int length)
    {
        if (length < sizeof(uint)) throw new InvalidDataException();
        uint count = unchecked((uint)Marshal.ReadInt32(buffer));
        const int first = 4, stride = 12;
        if (count > 512 || first + (long)count * stride > length) throw new InvalidDataException();
        var names = new List<string>();
        for (int i = 0; i < count; i++)
        {
            int offset = first + i * stride;
            uint attributes = unchecked((uint)Marshal.ReadInt32(buffer, offset + 8));
            if ((attributes & PrivilegeEnabled) == 0) continue;
            var luid = new Luid { LowPart = unchecked((uint)Marshal.ReadInt32(buffer, offset)), HighPart = Marshal.ReadInt32(buffer, offset + 4) };
            int size = 0;
            LookupPrivilegeNameW(null, ref luid, null, ref size);
            if (size <= 0 || size > 128) throw new InvalidDataException("Privilege name unavailable.");
            var name = new StringBuilder(size + 1);
            size = name.Capacity;
            if (!LookupPrivilegeNameW(null, ref luid, name, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            names.Add(name.ToString());
        }
        return (count, names.ToArray());
    }
}
