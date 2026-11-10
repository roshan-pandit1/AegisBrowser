using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using P309j;

internal static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal IntPtr Descriptor; internal int Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision,
        out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr descriptor);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBuffer, uint inBuffer, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr attributes,
        uint creation, uint flags, IntPtr template);
    [StructLayout(LayoutKind.Sequential)] private struct SidAttributes { internal IntPtr Sid; internal uint Attributes; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSidToSidW(string sid, out IntPtr result);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existing, uint flags, uint disableCount,
        IntPtr disabled, uint privilegeCount, IntPtr privileges, uint restrictCount, ref SidAttributes restricting,
        out SafeAccessTokenHandle result);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint access, IntPtr attributes,
        int impersonationLevel, int tokenType, out SafeAccessTokenHandle token);

    private static int count;
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }

    private static void RunFixture(string dacl, string expectedStatus, bool openExpected)
    {
        string name = @"\\.\pipe\aegis-p309j-probe-" + Guid.NewGuid().ToString("N");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(dacl, 1, out var descriptor, out _))
            throw new InvalidOperationException("SDDL_FAILED: " + Marshal.GetLastWin32Error());
        try
        {
            var attr = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            using var server = CreateNamedPipeW(name, 3 | 0x00080000, 0x8, 1, 256, 256, 0, ref attr);
            if (server.IsInvalid) throw new InvalidOperationException("PIPE_CREATE_FAILED: " + Marshal.GetLastWin32Error());
            using var process = Process.GetCurrentProcess();
            var result = PipeDaclProbe.Check(server, process.SafeHandle);
            Check(result.Status == expectedStatus, "actual object DACL reports " + expectedStatus + ": " + result.Stage + "/" + result.Error);
            Check(result.ActualOpenCause == "INCONCLUSIVE" && result.SandboxCompatibility == "INCONCLUSIVE",
                "DACL result is not promoted to original renderer denial or sandbox conclusion");
            using var client = CreateFileW(name, 0x40000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
            int error = client.IsInvalid ? Marshal.GetLastWin32Error() : 0;
            Check(client.IsInvalid != openExpected && (openExpected || error == 5),
                "same-user synthetic CreateFile result matches fixture expectation: " + error);
        }
        finally { LocalFree(descriptor); }
    }

    [SupportedOSPlatform("windows")]
    private static void RunRestrictedFixture(string currentSid, string restrictionSid, bool allowedExpected)
    {
        string name = @"\\.\pipe\aegis-p309j-probe-" + Guid.NewGuid().ToString("N");
        string sddl = "D:P(A;;GA;;;" + currentSid + ")(A;;GRGW;;;RC)(A;;GRGW;;;AC)S:(ML;;NW;;;S-1-16-0)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _))
            throw new InvalidOperationException("SDDL_FAILED: " + Marshal.GetLastWin32Error());
        if (!ConvertStringSidToSidW(restrictionSid, out var sid))
        { LocalFree(descriptor); throw new InvalidOperationException("SID_FAILED: " + Marshal.GetLastWin32Error()); }
        try
        {
            var attr = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            using var server = CreateNamedPipeW(name, 3 | 0x00080000, 0x8, 1, 256, 256, 0, ref attr);
            if (server.IsInvalid) throw new InvalidOperationException("PIPE_CREATE_FAILED: " + Marshal.GetLastWin32Error());
            using var process = Process.GetCurrentProcess();
            if (!OpenProcessToken(process.SafeHandle, 0xE, out var original))
                throw new InvalidOperationException("OPEN_TOKEN_FAILED: " + Marshal.GetLastWin32Error());
            using (original)
            {
                var restriction = new SidAttributes { Sid = sid };
                if (!CreateRestrictedToken(original, 1 /* DISABLE_MAX_PRIVILEGE */, 0, IntPtr.Zero, 0,
                    IntPtr.Zero, 1, ref restriction, out var restricted))
                    throw new InvalidOperationException("RESTRICT_TOKEN_FAILED: " + Marshal.GetLastWin32Error());
                using (restricted)
                {
                    var result = PipeDaclProbe.CheckSyntheticToken(server, restricted);
                    Check(result.Status == (allowedExpected ? "DACL_ALLOWED" : "DACL_DENIED"),
                        "synthetic restricted SID " + restrictionSid + " DACL result: " + result.Status);
                    if (!DuplicateTokenEx(restricted, 0xC /* TOKEN_QUERY | TOKEN_IMPERSONATE */,
                        IntPtr.Zero, 2, 2, out var impersonation))
                        throw new InvalidOperationException("DUPLICATE_FAILED: " + Marshal.GetLastWin32Error());
                    using (impersonation)
                    {
                        int error = WindowsIdentity.RunImpersonated(impersonation, () => {
                            using var client = CreateFileW(name, 0x40000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
                            return client.IsInvalid ? Marshal.GetLastWin32Error() : 0;
                        });
                        Check(allowedExpected ? error == 0 : error == 5,
                            "synthetic restricted SID " + restrictionSid + " CreateFile result: " + error);
                    }
                }
            }
        }
        finally { LocalFree(sid); LocalFree(descriptor); }
    }

    private static int Main()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("NOT_RUN: Windows required"); return 2; }
        try
        {
            using var current = WindowsIdentity.GetCurrent();
            string sid = current.User?.Value ?? throw new InvalidOperationException("SID_UNAVAILABLE");
            // This fixture exactly copies the inherited experiment's access
            // descriptor. It creates only a per-run pipe, never changes it.
            RunFixture("D:P(A;;GA;;;" + sid + ")(A;;GRGW;;;RC)(A;;GRGW;;;AC)S:(ML;;NW;;;S-1-16-0)", "DACL_ALLOWED", true);
            RunFixture("D:P", "DACL_DENIED", false);
            RunRestrictedFixture(sid, "S-1-1-0", false);
            using var process = Process.GetCurrentProcess();
            var invalid = PipeDaclProbe.Check(null, process.SafeHandle);
            Check(invalid.Status == "UNAVAILABLE" && invalid.Stage == "HANDLE_INVALID", "missing server handle remains unavailable");
            using var bad = new SafePipeHandle(IntPtr.Zero, false);
            invalid = PipeDaclProbe.Check(bad, process.SafeHandle);
            Check(invalid.Status == "UNAVAILABLE", "invalid server handle remains unavailable");
            Console.WriteLine($"PASS: {count} synthetic pipe DACL checks; no renderer or sandbox acceptance");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
