using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace P309j;

// Read-only DACL diagnostic for an already-owned pipe and a caller-supplied
// live candidate process handle. It never opens a PID or impersonates a thread.
// AccessCheck checks an impersonation-token copy against an object descriptor
// observed later; it is not the original client CreateFile, broker decision,
// mandatory-label check, renderer identity, or sandbox-enforcement proof.
public static class PipeDaclProbe
{
    public sealed record Result(string Status, string Stage, int Error,
        string ActualOpenCause = "INCONCLUSIVE", string SandboxCompatibility = "INCONCLUSIVE");

    private const uint WaitTimeout = 258, TokenQuery = 8, TokenDuplicate = 2;
    // FILE_GENERIC_WRITE is the FILE_OBJECT mapping of GENERIC_WRITE used by
    // the experiment's CreateFileA; it includes READ_CONTROL and SYNCHRONIZE.
    private const uint FileGenericWrite = 0x00120116;
    [StructLayout(LayoutKind.Sequential)] private struct GenericMapping
    { public uint Read, Write, Execute, All; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint timeout);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint access,
        IntPtr attributes, int impersonationLevel, int tokenType, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(SafeHandle handle, int type, uint information,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AccessCheck(IntPtr descriptor, SafeAccessTokenHandle token,
        uint desired, ref GenericMapping mapping, IntPtr privileges, ref uint privilegeLength,
        out uint granted, [MarshalAs(UnmanagedType.Bool)] out bool accessStatus);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static Result Check(SafeHandle? serverPipe, SafeProcessHandle? candidate)
    {
        if (!OperatingSystem.IsWindows()) return Unknown("WINDOWS_REQUIRED", 0);
        if (serverPipe is null || serverPipe.IsClosed || serverPipe.IsInvalid ||
            candidate is null || candidate.IsClosed || candidate.IsInvalid)
            return Unknown("HANDLE_INVALID", 0);
        IntPtr descriptor = IntPtr.Zero;
        try
        {
            if (WaitForSingleObject(candidate, 0) != WaitTimeout) return Unknown("PROCESS_NOT_LIVE", 0);
            uint err = GetSecurityInfo(serverPipe, 6 /* SE_KERNEL_OBJECT */,
                1 | 2 | 4 /* owner, group, DACL; intentionally not SACL/MIC */,
                out _, out _, out _, IntPtr.Zero, out descriptor);
            if (err != 0 || descriptor == IntPtr.Zero) return Unknown("GET_SECURITY_INFO", checked((int)err));
            if (!OpenProcessToken(candidate, TokenQuery | TokenDuplicate, out var source))
                return Unknown("OPEN_PROCESS_TOKEN", Marshal.GetLastWin32Error());
            using (source)
            {
                var result = CheckDescriptorToken(descriptor, source);
                if (WaitForSingleObject(candidate, 0) != WaitTimeout)
                    return Unknown("PROCESS_EXITED_DURING_CHECK", 0);
                return result;
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or Win32Exception or ArgumentException)
        { return Unknown("DIAGNOSTIC_EXCEPTION_" + e.GetType().Name, 0); }
        finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
    }

    // Test-only entry for restricted-token fixtures; never use it to assert
    // an observed process role or the cause of a past CreateFile failure.
    internal static Result CheckSyntheticToken(SafeHandle pipe, SafeAccessTokenHandle source)
    {
        IntPtr descriptor = IntPtr.Zero;
        try
        {
            uint err = GetSecurityInfo(pipe, 6, 1 | 2 | 4, out _, out _, out _, IntPtr.Zero, out descriptor);
            return err == 0 && descriptor != IntPtr.Zero ? CheckDescriptorToken(descriptor, source) : Unknown("GET_SECURITY_INFO", (int)err);
        }
        finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
    }

    private static Result CheckDescriptorToken(IntPtr descriptor, SafeAccessTokenHandle source)
    {
        if (!DuplicateTokenEx(source, TokenQuery, IntPtr.Zero, 2 /* SecurityImpersonation */,
            2 /* TokenImpersonation */, out var token))
            return Unknown("DUPLICATE_TOKEN", Marshal.GetLastWin32Error());
        using (token)
        {
            var mapping = new GenericMapping { Read = 0x00120089, Write = FileGenericWrite,
                Execute = 0x001200A0, All = 0x001F01FF };
            // The fixed privilege-set buffer is bounded. An unexpectedly
            // larger result is explicitly unavailable, never allowed.
            uint length = 4096;
            IntPtr privileges = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!AccessCheck(descriptor, token, FileGenericWrite, ref mapping,
                    privileges, ref length, out _, out bool allowed))
                    return Unknown("ACCESS_CHECK", Marshal.GetLastWin32Error());
                return new(allowed ? "DACL_ALLOWED" : "DACL_DENIED", "ACCESS_CHECK", 0);
            }
            finally { Marshal.FreeHGlobal(privileges); }
        }
    }

    private static Result Unknown(string stage, int error) => new("UNAVAILABLE", stage, error);
}
