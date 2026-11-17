using System.Diagnostics;

namespace P0Isolation;

internal static class Program
{
    private static int checks;

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--owned-fixture")
        {
            Console.WriteLine("READY");
            return Console.ReadLine() == "release" ? 0 : 2;
        }
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Windows required."); return 2; }
        try
        {
            if (!args.Contains("--role-only", StringComparer.Ordinal)) SentinelControl.Run(Check);
            VerifyOwnedChild();
            Console.WriteLine($"PASS: {checks} synthetic isolation-control checks; renderer identity and sandbox enforcement INCONCLUSIVE");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex.GetType().Name + ": " + ex.Message +
                (ex is System.ComponentModel.Win32Exception native ? " code=" + native.NativeErrorCode : ""));
            return 1;
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        checks++;
        Console.WriteLine("PASS: " + description);
    }

    private static void VerifyOwnedChild()
    {
        string path = Environment.ProcessPath ?? throw new InvalidOperationException("PROCESS_PATH_UNAVAILABLE");
        string marker = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("ENTRY_ASSEMBLY_UNAVAILABLE"));
        start.ArgumentList.Add("--owned-fixture");
        start.ArgumentList.Add("--type=renderer");
        start.ArgumentList.Add("--aegis-test-role=" + marker);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("TEST_CHILD_START_FAILED");
        try
        {
            var ready = child.StandardOutput.ReadLineAsync();
            if (!ready.Wait(TimeSpan.FromSeconds(10)) || ready.Result != "READY")
                throw new TimeoutException("TEST_CHILD_NOT_READY");
            using var current = Process.GetCurrentProcess();
            var parentId = ProcessRoleEvidence.Capture(current.SafeHandle);
            var childId = ProcessRoleEvidence.Capture(child.SafeHandle);
            var observed = ProcessRoleEvidence.Observe(current.SafeHandle, parentId, child.SafeHandle, childId, marker);
            Check(observed.Status == "CORROBORATED_ARGUMENTS_ONLY", "test-owned live child arguments corroborate only a role claim");
            Check(observed.RendererIdentity == "INCONCLUSIVE" && observed.SandboxEnforcement == "INCONCLUSIVE",
                "matching arguments cannot establish renderer identity or sandbox enforcement");
            var wrong = ProcessRoleEvidence.Observe(current.SafeHandle, parentId, child.SafeHandle, childId, Guid.NewGuid().ToString("N"));
            Check(wrong.Status == "INCONCLUSIVE" && wrong.Reason == "ARGUMENT_MISMATCH", "wrong marker fails closed");
            var wrongParent = ProcessRoleEvidence.Observe(child.SafeHandle, childId, current.SafeHandle, parentId, marker);
            Check(wrongParent.Status == "INCONCLUSIVE", "wrong parent cannot satisfy the correlation");
            child.StandardInput.WriteLine("release");
            if (!child.WaitForExit(10000) || child.ExitCode != 0) throw new TimeoutException("TEST_CHILD_CLOSE_FAILED");
            var stale = ProcessRoleEvidence.Observe(current.SafeHandle, parentId, child.SafeHandle, childId, marker);
            Check(stale.Status == "INCONCLUSIVE" && stale.Reason == "STALE_IDENTITY", "exited child cannot satisfy correlation");
        }
        finally
        {
            if (!child.HasExited)
            {
                // This is only our synthetic helper, never a browser process.
                child.Kill(); child.WaitForExit();
            }
        }
    }
}
