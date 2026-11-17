using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace P0Isolation;

// Synthetic Windows access control test only. A restricted impersonation
// token is NOT a Chromium renderer or a test of the CEF sandbox.
internal static class SentinelControl
{
    private const uint TokenDuplicate = 0x2, TokenImpersonate = 0x4, TokenQuery = 0x8;
    private const uint DisableMaxPrivilege = 0x1, DaclInformation = 0x4, ProtectedDacl = 0x80000000;
    private const uint GenericRead = 0x80000000, ShareReadWriteDelete = 7, OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAttributes { public IntPtr Sid; public uint Attributes; }
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle token, uint flags,
        uint disabledSidCount, IntPtr disabledSids, uint deletedPrivilegeCount, IntPtr deletedPrivileges,
        uint restrictingSidCount, ref SidAttributes restrictingSids, out SafeAccessTokenHandle result);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ImpersonateLoggedOnUser(SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool RevertToSelf();
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSidToSidW(string sid, out IntPtr result);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl,
        uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetFileSecurityW(string path, uint information, IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr attributes, uint disposition, uint flags, IntPtr template);

    internal static void Run(Action<bool, string> check)
    {
        var temp = Path.GetFullPath(Path.GetTempPath());
        var dir = Path.Combine(temp, "aegis-p0-sentinel-" + Guid.NewGuid().ToString("N"));
        if (!string.Equals(Path.GetDirectoryName(dir)?.TrimEnd(Path.DirectorySeparatorChar),
                temp.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("UNSAFE_TEST_DIRECTORY");
        Directory.CreateDirectory(dir);
        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("LINKED_TEST_DIRECTORY");
        var path = Path.Combine(dir, "sentinel.txt");
        try
        {
            File.WriteAllText(path, "synthetic sandbox access control");
            using var identity = WindowsIdentity.GetCurrent();
            string sid = identity.User?.Value ?? throw new InvalidOperationException("USER_SID_UNAVAILABLE");
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;;GA;;;" + sid + ")", 1, out var descriptor, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!SetFileSecurityW(path, DaclInformation | ProtectedDacl, descriptor))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { LocalFree(descriptor); }

            using (var positive = CreateFileW(path, GenericRead, ShareReadWriteDelete, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
                check(!positive.IsInvalid, "unrestricted test process opens the owned sentinel");

            if (!OpenProcessToken(GetCurrentProcess(), TokenDuplicate | TokenImpersonate | TokenQuery, out var source))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OPEN_PROCESS_TOKEN_FAILED");
            using (source)
            {
                if (!ConvertStringSidToSidW("S-1-5-12", out var sidPointer))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CONVERT_RESTRICTING_SID_FAILED");
                try
                {
                    var restricting = new SidAttributes { Sid = sidPointer };
                    if (!CreateRestrictedToken(source, DisableMaxPrivilege, 0, IntPtr.Zero, 0, IntPtr.Zero,
                            1, ref restricting, out var restricted))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "CREATE_RESTRICTED_TOKEN_FAILED");
                    using (restricted)
                    {
                        if (!ImpersonateLoggedOnUser(restricted)) throw new Win32Exception(Marshal.GetLastWin32Error(), "IMPERSONATION_FAILED");
                        int error;
                        bool denied;
                        try
                        {
                            using var candidate = CreateFileW(path, GenericRead, ShareReadWriteDelete, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                            denied = candidate.IsInvalid;
                            error = denied ? Marshal.GetLastWin32Error() : 0;
                        }
                        finally { if (!RevertToSelf()) Environment.FailFast("Could not revert test impersonation."); }
                        check(denied && error == 5, "synthetic restricted token receives access denied, not a missing-file result");
                    }
                }
                finally { LocalFree(sidPointer); }
            }
            using (var positiveAfter = CreateFileW(path, GenericRead, ShareReadWriteDelete, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
                check(!positiveAfter.IsInvalid, "unrestricted access remains available after impersonation");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(dir)) Directory.Delete(dir);
        }
    }
}
