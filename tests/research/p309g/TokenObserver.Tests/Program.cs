using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using AegisBrowser.P309G;
using Microsoft.Win32.SafeHandles;

// Only this test process is queried. None of these checks creates or inspects
// a browser process or claims to establish Chromium sandbox enforcement.
internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation,
        out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);

    private static int count;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        count++;
        Console.WriteLine("PASS: " + message);
    }

    private static bool ParserRejects(string name, IntPtr buffer, int length)
    {
        var method = typeof(TokenObserver).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Token parser missing.");
        try { _ = method.Invoke(null, new object[] { buffer, length }); return false; }
        catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { return true; }
    }

    private static int Main()
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("NOT_RUN: Windows required"); return 2; }
        try
        {
            using var self = Process.GetCurrentProcess();
            var handle = self.SafeHandle;
            if (!GetProcessTimes(handle, out long creation, out _, out _, out _))
                throw new InvalidOperationException("Cannot obtain test process creation time.");
            string path = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
            uint pid = checked((uint)self.Id);
            var observed = TokenObserver.Observe(handle, pid, creation, path);
            Check(observed.Status == "OBSERVED", "caller-owned test process token can be queried (" + observed.Reason + ")");
            Check(observed.ProcessId == pid && observed.CreationFileTimeUtc == creation, "snapshot is bound to PID and exact creation FILETIME");
            Check(observed.IntegrityRid.HasValue && observed.IsAppContainer.HasValue, "integrity and AppContainer state are observed without assumptions");
            Check(observed.GroupCount.HasValue && observed.RestrictingSidCount.HasValue && observed.PrivilegeCount.HasValue, "group, restricting-SID and privilege counts are returned");
            Check(observed.EnabledPrivilegeCount == observed.EnabledPrivilegeNames.Length, "enabled privilege count agrees with reported names");
            Check(observed.SandboxEnforcement == "INCONCLUSIVE" && observed.ProcessRole == "NOT_ESTABLISHED", "token snapshot alone never verifies a renderer or sandbox");
            Check(TokenObserver.Observe(handle, pid + 1, creation, path).Reason == "PID_MISMATCH", "wrong PID is rejected");
            Check(TokenObserver.Observe(handle, pid, creation + 1, path).Reason == "CREATION_TIME_MISMATCH", "stale creation time is rejected");
            Check(TokenObserver.Observe(handle, pid, creation, Path.Combine(Path.GetDirectoryName(path)!, "not-the-process.exe")).Reason == "IMAGE_PATH_MISMATCH", "wrong image path is rejected");
            Check(TokenObserver.Observe(handle, pid, creation, "relative.exe").Reason == "INVALID_IDENTITY_INPUT", "relative expected image path is rejected");
            Check(TokenObserver.Observe(handle, pid, creation, "C:\\invalid\0image.exe").Status == "INCONCLUSIVE", "malformed expected image path is inconclusive");
            Check(TokenObserver.Observe(null, pid, creation, path).Reason == "INVALID_IDENTITY_INPUT", "missing process handle is rejected");
            using var invalid = new SafeProcessHandle(IntPtr.Zero, false);
            Check(TokenObserver.Observe(invalid, pid, creation, path).Reason == "INVALID_IDENTITY_INPUT", "invalid process handle is rejected");
            // Synthetic buffers exercise bounds and SID-authority failure paths
            // without creating or modifying any Windows security token.
            IntPtr synthetic = Marshal.AllocHGlobal(64);
            try
            {
                Marshal.Copy(new byte[64], 0, synthetic, 64);
                Check(ParserRejects("ReadGroupCounts", synthetic, 3), "truncated group header is rejected");
                Marshal.WriteInt32(synthetic, int.MaxValue);
                Check(ParserRejects("ReadGroupCounts", synthetic, 64), "oversized group count is rejected");
                Marshal.WriteInt32(synthetic, 1);
                Check(ParserRejects("ReadGroupCounts", synthetic, 4), "truncated group entry is rejected");
                Check(ParserRejects("ReadPrivilegeCounts", synthetic, 4), "truncated privilege entry is rejected");
                Marshal.Copy(new byte[64], 0, synthetic, 64);
                IntPtr sid = IntPtr.Add(synthetic, 16);
                Marshal.WriteIntPtr(synthetic, sid);
                Marshal.WriteByte(sid, 0, 1); Marshal.WriteByte(sid, 1, 1); Marshal.WriteByte(sid, 7, 5);
                Check(ParserRejects("ReadIntegrityRid", synthetic, 64), "non-mandatory integrity SID authority is rejected");
            }
            finally { Marshal.FreeHGlobal(synthetic); }
            // This handle deliberately lacks SYNCHRONIZE and refers only to
            // the current test process. An unavailable liveness check must
            // not be turned into a successful token observation.
            using var limited = OpenProcess(0x1000, false, pid);
            if (limited.IsInvalid) throw new InvalidOperationException("Cannot open limited test-process handle.");
            Check(TokenObserver.Observe(limited, pid, creation, path).Status == "INCONCLUSIVE", "missing process synchronization access is inconclusive");

            var command = Environment.GetEnvironmentVariable("ComSpec") ?? throw new InvalidOperationException("ComSpec unavailable.");
            using var child = new Process { StartInfo = new ProcessStartInfo {
                FileName = command, Arguments = "/d /q", UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true } };
            if (!child.Start()) throw new InvalidOperationException("Cannot start owned test child.");
            var outputDrain = child.StandardOutput.ReadToEndAsync();
            var errorDrain = child.StandardError.ReadToEndAsync();
            var childHandle = child.SafeHandle;
            if (!GetProcessTimes(childHandle, out long childCreation, out _, out _, out _))
                throw new InvalidOperationException("Cannot obtain owned child creation time.");
            child.StandardInput.WriteLine("exit");
            child.StandardInput.Close();
            if (!child.WaitForExit(5000)) throw new InvalidOperationException("Owned test child did not exit; no termination attempted.");
            Task.WhenAll(outputDrain, errorDrain).GetAwaiter().GetResult();
            Check(TokenObserver.Observe(childHandle, checked((uint)child.Id), childCreation, command).Status == "INCONCLUSIVE",
                "an already-exited owned child is never a live observation");
            Console.WriteLine($"PASS: {count} process-token observer checks; no renderer or sandbox enforcement asserted");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
