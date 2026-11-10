using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace P309g;

// Owned helper processes only. These tests do not launch CEF, discover a real
// renderer or establish any sandbox restriction.
internal static class RendererPipeTests
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation,
        out long exit, out long kernel, out long user);
    private static int count;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        count++;
        Console.WriteLine("PASS: " + name);
    }
    internal static async Task<int> Run()
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("NOT_RUN: Windows required"); return 2; }
        try
        {
            using var self = Process.GetCurrentProcess();
            string image = Environment.ProcessPath ?? throw new InvalidOperationException("NO_PROCESS_IMAGE");
            if (!GetProcessTimes(self.SafeHandle, out long created, out _, out _, out _))
                throw new InvalidOperationException("NO_CREATION_TIME");
            var identity = new ProcessRunner.Identity(self.Id, created, image, "SYNTHETIC");
            using (var observer = RendererPipeObserver.Create())
            {
                observer.Start(identity, self.SafeHandle);
                using var child = Process.Start(global::SelfTest.HelperStartInfo("renderer-pipe", observer.FullName))
                    ?? throw new InvalidOperationException("CHILD_START_FAILED");
                var evidence = await observer.WaitForEvidenceAsync();
                Check(evidence.Status == "OBSERVED", "owned direct-child token snapshot observed: " + evidence.Reason);
                Check(evidence.CandidatePid == child.Id && evidence.CandidateCreationFileTimeUtc > created,
                    "kernel pipe PID and creation time bind the owned helper");
                Check(evidence.Token?.Status == "OBSERVED", "token snapshot is handle-bound");
                Check(evidence.RoleEvidence == "CLIENT_CLAIM_ONLY" && evidence.RendererIdentity == "INCONCLUSIVE",
                    "client role claim is not verified renderer identity");
                Check(evidence.SandboxEnforcement == "INCONCLUSIVE" && evidence.SubprocessCleanup == "INCONCLUSIVE" &&
                    evidence.SubprocessAccounting == "INCONCLUSIVE", "snapshot cannot promote sandbox or cleanup");
                var liveResult = await observer.FinishAsync();
                Check(liveResult.CandidateExit == "STILL_RUNNING" && liveResult.CandidateExitCode == null,
                    "live retained candidate is not reported exited");
                Check(observer.CandidateHandleClosedForTest, "live candidate handle is closed after observation finishes");
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(child.ExitCode == 0, "owned helper exited normally");
            }
            using (var observer = RendererPipeObserver.Create())
            {
                observer.Start(identity, self.SafeHandle);
                using var child = Process.Start(global::SelfTest.HelperStartInfo("renderer-pipe", observer.FullName))
                    ?? throw new InvalidOperationException("CHILD_START_FAILED");
                var evidence = await observer.WaitForEvidenceAsync();
                Check(evidence.Status == "OBSERVED", "candidate is accepted before exit observation");
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var exitedResult = await observer.FinishAsync();
                Check(exitedResult.CandidateExit == "EXITED" && exitedResult.CandidateExitCode == 0,
                    "retained candidate handle reports actual zero exit");
                Check(exitedResult.SubprocessAccounting == "INCONCLUSIVE" && exitedResult.SubprocessCleanup == "INCONCLUSIVE",
                    "one candidate exit does not establish descendant cleanup");
                Check(observer.CandidateHandleClosedForTest, "exited candidate handle is closed after observation finishes");
            }
            using (var observer = RendererPipeObserver.Create())
            {
                observer.Start(identity, self.SafeHandle);
                using var child = Process.Start(global::SelfTest.HelperStartInfo("renderer-pipe-bad", observer.FullName))
                    ?? throw new InvalidOperationException("CHILD_START_FAILED");
                var evidence = await observer.WaitForEvidenceAsync();
                Check(evidence.Status == "INCONCLUSIVE" && evidence.Reason == "CLIENT_FRAME_INVALID" && evidence.Token == null,
                    "malformed claim cannot collect token evidence");
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(child.ExitCode == 0, "malformed-frame helper exited normally");
            }
            using (var observer = RendererPipeObserver.Create())
            using (var otherParent = Process.Start(global::SelfTest.HelperStartInfo("timeout"))
                ?? throw new InvalidOperationException("OTHER_PARENT_START_FAILED"))
            {
                if (!GetProcessTimes(otherParent.SafeHandle, out long otherCreated, out _, out _, out _))
                    throw new InvalidOperationException("OTHER_PARENT_CREATION_TIME_UNAVAILABLE");
                var otherIdentity = new ProcessRunner.Identity(otherParent.Id, otherCreated, image, "SYNTHETIC");
                observer.Start(otherIdentity, otherParent.SafeHandle);
                using var child = Process.Start(global::SelfTest.HelperStartInfo("renderer-pipe", observer.FullName))
                    ?? throw new InvalidOperationException("CHILD_START_FAILED");
                var evidence = await observer.WaitForEvidenceAsync();
                Check(evidence.Status == "INCONCLUSIVE" && evidence.Token == null &&
                    evidence.Reason == "PARENT_SNAPSHOT_MISMATCH",
                    "connected sibling is rejected before token collection");
                await Task.WhenAll(child.WaitForExitAsync(), otherParent.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(5));
                Check(child.ExitCode == 0 && otherParent.ExitCode == 0, "owned sibling helpers exited normally");
            }
            using (var observer = RendererPipeObserver.Create())
            {
                observer.Start(identity, self.SafeHandle);
                var evidence = await observer.FinishAsync();
                Check(evidence.Status == "INCONCLUSIVE" && evidence.Token == null,
                    "missing connection remains inconclusive");
            }
            foreach (string mode in new[] { "renderer-claim-parent", "renderer-claim-duplicate", "renderer-claim-oversized" })
            {
                using var observer = RendererPipeObserver.Create();
                var helper = global::SelfTest.HelperStartInfo(mode);
                string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(helper.FileName)));
                var run = await ProcessRunner.RunAsync(helper.FileName, hash, helper.ArgumentList.ToArray(),
                    AppContext.BaseDirectory, 10000, true, (id, handle) => observer.Start(id, handle),
                    onStdoutLine: observer.AcceptLine);
                var claim = await observer.FinishAsync();
                Check(run.ProcessExit == "NORMAL" && run.OutputComplete, mode + " helper completed with bounded output");
                if (mode == "renderer-claim-parent")
                    Check(claim.Status == "OBSERVED" && claim.RoleEvidence == "CLIENT_PID_CLAIM" &&
                        claim.RendererIdentity == "INCONCLUSIVE" && claim.SandboxEnforcement == "INCONCLUSIVE",
                        "live claimed direct-child token remains unverified renderer evidence");
                else
                    Check(claim.Status == "INCONCLUSIVE" && claim.Token == null &&
                        claim.Reason == "PID_CLAIM_MALFORMED_OR_DUPLICATE",
                        mode + " cannot promote token evidence");
            }
            Console.WriteLine($"PASS: {count} owned-process pipe checks; no CEF renderer or sandbox acceptance");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
