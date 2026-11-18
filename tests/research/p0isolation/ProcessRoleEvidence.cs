using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace P0Isolation;

// Test-only corroboration. Neither an argument nor a PID claim proves that
// a process is the renderer servicing a particular frame.
internal static class ProcessRoleEvidence
{
    private const uint WaitTimeout = 258;
    private const int ProcessBasicInformation = 0, ProcessCommandLineInformation = 60;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority;
        public UIntPtr UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeString { public ushort Length, MaximumLength; public IntPtr Buffer; }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass,
        IntPtr information, int length, out int returnLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created,
        out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, int flags,
        StringBuilder name, ref int size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    internal readonly record struct Identity(uint Pid, long Created, string Image);
    internal readonly record struct Result(string Status, string Reason)
    {
        // Never transform this diagnostic into a sandbox or renderer verdict.
        internal string RendererIdentity => "INCONCLUSIVE";
        internal string SandboxEnforcement => "INCONCLUSIVE";
    }

    internal static Identity Capture(SafeProcessHandle process)
    {
        if (process.IsInvalid || process.IsClosed || WaitForSingleObject(process, 0) != WaitTimeout)
            throw new InvalidOperationException("PROCESS_NOT_LIVE");
        var pid = GetProcessId(process);
        if (pid == 0 || !GetProcessTimes(process, out var created, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new StringBuilder(32768);
        int length = buffer.Capacity;
        if (!QueryFullProcessImageNameW(process, 0, buffer, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new Identity(pid, created, buffer.ToString());
    }

    private static bool Matches(SafeProcessHandle handle, Identity expected)
    {
        try { return Capture(handle) == expected; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ObjectDisposedException)
        { return false; }
    }

    // Caller supplies retained handles for the test-owned parent and child.
    // This method never enumerates processes or opens a claimed PID.
    internal static Result Observe(SafeProcessHandle parent, Identity expectedParent,
        SafeProcessHandle child, Identity expectedChild, string marker)
    {
        if (!OperatingSystem.IsWindows()) return Unknown("WINDOWS_REQUIRED");
        if (!Guid.TryParseExact(marker, "N", out _) || expectedParent.Pid == expectedChild.Pid ||
            expectedChild.Created < expectedParent.Created) return Unknown("INVALID_INPUT");
        if (!Matches(parent, expectedParent) || !Matches(child, expectedChild)) return Unknown("STALE_IDENTITY");

        IntPtr basic = Marshal.AllocHGlobal(Marshal.SizeOf<BasicInformation>());
        try
        {
            var size = Marshal.SizeOf<BasicInformation>();
            if (NtQueryInformationProcess(child, ProcessBasicInformation, basic, size, out var returned) != 0 || returned != size)
                return Unknown("PARENT_QUERY_UNAVAILABLE");
            var info = Marshal.PtrToStructure<BasicInformation>(basic);
            if (info.UniqueProcessId.ToUInt64() != expectedChild.Pid ||
                info.InheritedFromUniqueProcessId.ToUInt64() != expectedParent.Pid)
                return Unknown("PARENT_MISMATCH");
        }
        finally { Marshal.FreeHGlobal(basic); }

        // ProcessCommandLineInformation is a best-effort OS query and may be
        // unavailable for sandboxed children. Raw arguments are never logged.
        _ = NtQueryInformationProcess(child, ProcessCommandLineInformation, IntPtr.Zero, 0, out var required);
        if (required < Marshal.SizeOf<NativeString>() || required > 65536) return Unknown("ARGUMENT_QUERY_UNAVAILABLE");
        IntPtr buffer = Marshal.AllocHGlobal(required);
        try
        {
            if (NtQueryInformationProcess(child, ProcessCommandLineInformation, buffer, required, out var returned) != 0 ||
                returned > required || returned < Marshal.SizeOf<NativeString>()) return Unknown("ARGUMENT_QUERY_UNAVAILABLE");
            var value = Marshal.PtrToStructure<NativeString>(buffer);
            long start = buffer.ToInt64(), text = value.Buffer.ToInt64();
            if ((value.Length & 1) != 0 || value.Length > value.MaximumLength || text < start ||
                text > start + returned || value.Length > start + returned - text) return Unknown("ARGUMENT_BUFFER_INVALID");
            string command = Marshal.PtrToStringUni(value.Buffer, value.Length / 2) ?? "";
            IntPtr argv = CommandLineToArgvW(command, out var count);
            if (argv == IntPtr.Zero) return Unknown("ARGUMENT_PARSE_FAILED");
            try
            {
                if (count <= 0 || count > 128) return Unknown("ARGUMENT_PARSE_FAILED");
                int roleCount = 0, markerCount = 0;
                bool role = false, sameMarker = false;
                for (int i = 1; i < count; i++)
                {
                    string arg = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
                    if (arg.StartsWith("--type=", StringComparison.Ordinal)) { roleCount++; role = arg == "--type=renderer"; }
                    if (arg.StartsWith("--aegis-test-role=", StringComparison.Ordinal))
                    { markerCount++; sameMarker = arg == "--aegis-test-role=" + marker; }
                }
                if (!Matches(parent, expectedParent) || !Matches(child, expectedChild)) return Unknown("STALE_IDENTITY");
                if (roleCount != 1 || markerCount != 1) return Unknown("AMBIGUOUS_ARGUMENTS");
                if (!role || !sameMarker) return Unknown("ARGUMENT_MISMATCH");
                return new Result("CORROBORATED_ARGUMENTS_ONLY", "LIVE_DIRECT_CHILD_ARGUMENTS_MATCH");
            }
            finally { LocalFree(argv); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static Result Unknown(string reason) => new("INCONCLUSIVE", reason);
}
