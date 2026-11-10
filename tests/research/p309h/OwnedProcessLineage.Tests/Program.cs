using System.Diagnostics;
using System.Runtime.InteropServices;
using AegisBrowser.P309H;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation,
        out long exit, out long kernel, out long user);

    private static int count;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        count++;
        Console.WriteLine("PASS: " + message);
    }

    private static ProcessIdentity Identity(Process process, string path)
    {
        if (!GetProcessTimes(process.SafeHandle, out long creation, out _, out _, out _))
            throw new InvalidOperationException("Cannot read owned process creation time.");
        return new ProcessIdentity(checked((uint)process.Id), creation, path);
    }

    private static int Main()
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("NOT_RUN: Windows required"); return 2; }
        try
        {
            using var self = Process.GetCurrentProcess();
            var parent = Identity(self, Environment.ProcessPath ?? throw new InvalidOperationException("No image path"));
            var command = Environment.GetEnvironmentVariable("ComSpec") ?? throw new InvalidOperationException("No ComSpec");
            using var child = new Process { StartInfo = new ProcessStartInfo {
                FileName = command, Arguments = "/d /q", UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true } };
            if (!child.Start()) throw new InvalidOperationException("Owned child start failed");
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            try
            {
                var childIdentity = Identity(child, command);
                var observed = OwnedProcessLineage.Observe(self.SafeHandle, parent, child.SafeHandle, childIdentity);
                Check(observed.Status == "OBSERVED", "owned live direct child identity and token observed: " + observed.Reason);
                Check(observed.Token?.Status == "OBSERVED", "token is bound to retained child handle");
                Check(observed.RendererDiscovery == "INCONCLUSIVE" && observed.SandboxEnforcement == "INCONCLUSIVE" &&
                    observed.SubprocessCleanup == "INCONCLUSIVE", "direct lineage is not a renderer, sandbox or cleanup claim");
                Check(OwnedProcessLineage.Observe(self.SafeHandle, parent, child.SafeHandle,
                    childIdentity with { Pid = childIdentity.Pid + 1 }).Token is null, "wrong child PID rejected without token evidence");
                Check(OwnedProcessLineage.Observe(self.SafeHandle, parent, child.SafeHandle,
                    childIdentity with { CreationFileTimeUtc = childIdentity.CreationFileTimeUtc + 1 }).Status == "INCONCLUSIVE",
                    "stale child creation time rejected");
                Check(OwnedProcessLineage.Observe(self.SafeHandle, parent with { CreationFileTimeUtc = parent.CreationFileTimeUtc + 1 },
                    child.SafeHandle, childIdentity).Status == "INCONCLUSIVE", "stale parent creation time rejected");
                Check(OwnedProcessLineage.Observe(self.SafeHandle, parent, child.SafeHandle,
                    childIdentity with { ImagePath = Path.Combine(Path.GetDirectoryName(command)!, "wrong.exe") }).Token is null,
                    "wrong child image rejected");
                Check(OwnedProcessLineage.Observe(self.SafeHandle, parent with {
                    ImagePath = Path.Combine(Path.GetDirectoryName(parent.ImagePath)!, "wrong.exe") },
                    child.SafeHandle, childIdentity).Token is null, "wrong parent image rejected");
                Check(OwnedProcessLineage.Observe(self.SafeHandle, parent with { ImagePath = "relative.exe" },
                    child.SafeHandle, childIdentity).Token is null, "untrusted parent image rejected");
                Check(OwnedProcessLineage.Observe(self.SafeHandle, parent, self.SafeHandle, parent).Reason == "INVALID_IDENTITIES",
                    "same process cannot be its own child");
                Check(OwnedProcessLineage.Observe(null, parent, child.SafeHandle, childIdentity).Status == "INCONCLUSIVE",
                    "missing parent handle rejected");
                Check(OwnedProcessLineage.Observe(self.SafeHandle, parent, null, childIdentity).Status == "INCONCLUSIVE",
                    "missing candidate handle rejected");
                Check(OwnedProcessLineage.Observe(self.SafeHandle, null, child.SafeHandle, childIdentity).Status == "INCONCLUSIVE",
                    "missing parent identity rejected");
                Check(OwnedProcessLineage.Observe(child.SafeHandle, childIdentity, self.SafeHandle, parent).Status == "INCONCLUSIVE",
                    "reversed lineage rejected");
                // Both cmd processes are children of this test, not of each
                // other. This exercises the actual Windows parent query rather
                // than rejecting only because of creation-time ordering.
                using (var sibling = new Process { StartInfo = new ProcessStartInfo {
                    FileName = command, Arguments = "/d /q", UseShellExecute = false,
                    RedirectStandardInput = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true } })
                {
                    if (!sibling.Start()) throw new InvalidOperationException("Owned sibling start failed");
                    var siblingOutput = sibling.StandardOutput.ReadToEndAsync();
                    var siblingError = sibling.StandardError.ReadToEndAsync();
                    try
                    {
                        var siblingIdentity = Identity(sibling, command);
                        var wrongParent = OwnedProcessLineage.Observe(child.SafeHandle, childIdentity,
                            sibling.SafeHandle, siblingIdentity);
                        Check(wrongParent.Reason == "PARENT_RELATIONSHIP_MISMATCH" && wrongParent.Token is null,
                            "live sibling cannot be claimed as a direct child");
                    }
                    finally
                    {
                        sibling.StandardInput.WriteLine("exit");
                        sibling.StandardInput.Close();
                        if (!sibling.WaitForExit(5000)) throw new InvalidOperationException("Owned sibling did not exit; no termination attempted");
                        Task.WhenAll(siblingOutput, siblingError).GetAwaiter().GetResult();
                    }
                }
                child.StandardInput.WriteLine("exit");
                child.StandardInput.Close();
                if (!child.WaitForExit(5000)) throw new InvalidOperationException("Owned child did not exit; no termination attempted");
                Task.WhenAll(output, error).GetAwaiter().GetResult();
                Check(OwnedProcessLineage.Observe(self.SafeHandle, parent, child.SafeHandle, childIdentity).Status == "INCONCLUSIVE",
                    "exited child rejected");
            }
            finally { if (!child.HasExited) child.StandardInput.Close(); }
            Console.WriteLine($"PASS: {count} handle-bound lineage checks; no renderer or sandbox acceptance");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
