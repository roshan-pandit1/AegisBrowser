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
    private static Process StartControlled(string mode, params string[] args)
    {
        var start = global::SelfTest.HelperStartInfo(mode, args);
        start.RedirectStandardInput = true;
        return Process.Start(start) ?? throw new InvalidOperationException("CHILD_START_FAILED");
    }
    private static void Release(Process child)
    {
        child.StandardInput.WriteLine("RELEASE");
        child.StandardInput.Close();
    }
    private static async Task SendSyntheticBrokerRelay(string fullName, uint targetPid)
    {
        const string prefix = @"\\.\pipe\";
        if (!fullName.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("SYNTHETIC_PIPE_PREFIX_INVALID");
        using var client = new System.IO.Pipes.NamedPipeClientStream(".",
            fullName.Substring(prefix.Length),
            System.IO.Pipes.PipeDirection.Out, System.IO.Pipes.PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await client.WriteAsync(System.Text.Encoding.ASCII.GetBytes("P309I_BROKER___V1\n"));
        await client.WriteAsync(BitConverter.GetBytes(targetPid));
        await client.FlushAsync();
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
                using var child = StartControlled("renderer-pipe", observer.FullName);
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
                Release(child);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(child.ExitCode == 0, "owned helper exited normally");
            }
            using (var observer = RendererPipeObserver.Create())
            {
                observer.Start(identity, self.SafeHandle);
                using var child = StartControlled("renderer-hold");
                await SendSyntheticBrokerRelay(observer.FullName, checked((uint)child.Id));
                var evidence = await observer.WaitForEvidenceAsync();
                Check(evidence.Status == "OBSERVED" && evidence.CandidatePid == child.Id,
                    "browser-owned test broker correlates child with kernel process handle");
                Check(evidence.RoleEvidence == "CEF_BROWSER_BROKER_ROLE_CLAIM" &&
                      evidence.RendererIdentity == "INCONCLUSIVE" &&
                      evidence.SandboxEnforcement == "INCONCLUSIVE",
                    "synthetic broker relay does not establish Chromium renderer role or sandbox policy");
                await observer.FinishAsync();
                Release(child);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(child.ExitCode == 0, "synthetic broker target exits cleanly");
            }
            // The real browser can emit a stdout PID claim and write its
            // broker frame nearly simultaneously. Either ordering must keep
            // the bounded pipe slot available, and neither authenticates a
            // renderer role or proves sandbox enforcement.
            foreach (bool claimFirst in new[] { true, false })
            {
                using var observer = RendererPipeObserver.Create();
                observer.Start(identity, self.SafeHandle);
                using var child = StartControlled("renderer-hold");
                try
                {
                    if (claimFirst)
                    {
                        observer.AcceptLine("NATIVE_DIAGNOSTIC: P309I_RENDERER_PID=" + child.Id);
                        var claim = await observer.WaitForClaimEvidenceAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        Check(claim.Status == "OBSERVED" && claim.RoleEvidence == "CLIENT_PID_CLAIM",
                            "stdout-first claim gets its own bounded observation slot");
                    }
                    await SendSyntheticBrokerRelay(observer.FullName, checked((uint)child.Id));
                    var broker = await observer.WaitForEvidenceAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Check(broker.Status == "OBSERVED" && broker.CandidatePid == child.Id &&
                          broker.RoleEvidence == "CEF_BROWSER_BROKER_ROLE_CLAIM",
                        (claimFirst ? "stdout-first" : "broker-first") + " broker receives an independent observation slot");
                    if (!claimFirst)
                    {
                        observer.AcceptLine("NATIVE_DIAGNOSTIC: P309I_RENDERER_PID=" + child.Id);
                        var claim = await observer.WaitForClaimEvidenceAsync().WaitAsync(TimeSpan.FromSeconds(5));
                        Check(claim.Status == "OBSERVED" && claim.RoleEvidence == "CLIENT_PID_CLAIM",
                            "broker-first stdout claim gets its own bounded observation slot");
                    }
                    var result = await observer.FinishAsync();
                    Check(result.Status == "OBSERVED" && result.CandidatePid == child.Id &&
                          result.RoleEvidence == "CEF_BROWSER_BROKER_ROLE_CLAIM" &&
                          result.RendererIdentity == "INCONCLUSIVE" && result.SandboxEnforcement == "INCONCLUSIVE",
                        "pipe evidence takes precedence without promoting role or sandbox claims");
                    Check(observer.CandidateHandleClosedForTest, "both retained candidate handles are closed");
                }
                finally
                {
                    if (!child.HasExited) { Release(child); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                }
            }
            using (var observer = RendererPipeObserver.Create())
            {
                observer.Start(identity, self.SafeHandle);
                using var claimed = StartControlled("renderer-hold");
                using var brokered = StartControlled("renderer-hold");
                try
                {
                    observer.AcceptLine("NATIVE_DIAGNOSTIC: P309I_RENDERER_PID=" + claimed.Id);
                    var claim = await observer.WaitForClaimEvidenceAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    await SendSyntheticBrokerRelay(observer.FullName, checked((uint)brokered.Id));
                    var pipeEvidence = await observer.WaitForEvidenceAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Check(claim.Status == "OBSERVED" && claim.CandidatePid == claimed.Id &&
                          pipeEvidence.Status == "OBSERVED" && pipeEvidence.CandidatePid == brokered.Id,
                        "distinct claims retain their own bounded process handles");
                    var result = await observer.FinishAsync();
                    Check(result.Status == "INCONCLUSIVE" && result.Token == null &&
                          result.Reason == "CONFLICTING_CANDIDATE_IDENTITIES" &&
                          result.RendererIdentity == "INCONCLUSIVE" && result.SandboxEnforcement == "INCONCLUSIVE",
                        "conflicting candidate lifetimes are rejected rather than attributed to one renderer");
                    Check(observer.CandidateHandleClosedForTest, "distinct candidate handles are both closed");
                }
                finally
                {
                    if (!claimed.HasExited) { Release(claimed); await claimed.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    if (!brokered.HasExited) { Release(brokered); await brokered.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                }
            }
            using (var observer = RendererPipeObserver.Create())
            {
                observer.Start(identity, self.SafeHandle);
                using var child = StartControlled("renderer-hold");
                Release(child);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await SendSyntheticBrokerRelay(observer.FullName, checked((uint)child.Id));
                var stale = await observer.WaitForEvidenceAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(stale.Status == "INCONCLUSIVE" && stale.Token == null,
                    "broker relay for an exited child cannot produce token evidence");
                await observer.FinishAsync();
            }
            using (var observer = RendererPipeObserver.Create())
            {
                observer.Start(identity, self.SafeHandle);
                using var child = StartControlled("renderer-hold");
                try
                {
                    observer.AcceptLine("NATIVE_DIAGNOSTIC: P309I_RENDERER_PID=" + child.Id);
                    _ = await observer.WaitForClaimEvidenceAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    observer.AcceptLine("NATIVE_DIAGNOSTIC: P309I_RENDERER_PID=" + child.Id);
                    await SendSyntheticBrokerRelay(observer.FullName, checked((uint)child.Id));
                    _ = await observer.WaitForEvidenceAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    var result = await observer.FinishAsync();
                    Check(result.Status == "INCONCLUSIVE" && result.Token == null &&
                          result.Reason == "PID_CLAIM_MALFORMED_OR_DUPLICATE" && result.CandidateExit == "NOT_OBSERVED",
                        "duplicate stdout claim remains rejected even when broker evidence arrives");
                    Check(observer.CandidateHandleClosedForTest, "duplicate-claim path closes both retained handles");
                }
                finally
                {
                    if (!child.HasExited) { Release(child); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                }
            }
            // A non-parent client cannot impersonate the browser-process broker.
            using (var observer = RendererPipeObserver.Create())
            using (var otherParent = StartControlled("renderer-hold"))
            {
                if (!GetProcessTimes(otherParent.SafeHandle, out long otherCreated, out _, out _, out _))
                    throw new InvalidOperationException("SYNTHETIC_BROKER_PARENT_TIME_MISSING");
                var otherIdentity = new ProcessRunner.Identity(otherParent.Id, otherCreated, image, "SYNTHETIC");
                observer.Start(otherIdentity, otherParent.SafeHandle);
                await SendSyntheticBrokerRelay(observer.FullName, checked((uint)otherParent.Id));
                var evidence = await observer.WaitForEvidenceAsync();
                Check(evidence.Status == "INCONCLUSIVE" && evidence.Reason == "BROKER_CLIENT_NOT_BROWSER_PARENT" &&
                      evidence.Token == null,
                    "brokered role event from non-parent process is rejected");
                Release(otherParent);
                await otherParent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            using (var observer = RendererPipeObserver.Create())
            {
                observer.Start(identity, self.SafeHandle);
                using var child = StartControlled("renderer-pipe", observer.FullName);
                var evidence = await observer.WaitForEvidenceAsync();
                Check(evidence.Status == "OBSERVED", "candidate is accepted before exit observation");
                Release(child);
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
                using var child = StartControlled("renderer-pipe-bad", observer.FullName);
                var evidence = await observer.WaitForEvidenceAsync();
                Check(evidence.Status == "INCONCLUSIVE" && evidence.Reason == "CLIENT_FRAME_INVALID" && evidence.Token == null,
                    "malformed claim cannot collect token evidence");
                Release(child);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Check(child.ExitCode == 0, "malformed-frame helper exited normally");
            }
            using (var observer = RendererPipeObserver.Create())
            using (var otherParent = StartControlled("renderer-hold"))
            {
                if (!GetProcessTimes(otherParent.SafeHandle, out long otherCreated, out _, out _, out _))
                    throw new InvalidOperationException("OTHER_PARENT_CREATION_TIME_UNAVAILABLE");
                var otherIdentity = new ProcessRunner.Identity(otherParent.Id, otherCreated, image, "SYNTHETIC");
                observer.Start(otherIdentity, otherParent.SafeHandle);
                using var child = StartControlled("renderer-pipe", observer.FullName);
                var evidence = await observer.WaitForEvidenceAsync();
                Check(evidence.Status == "INCONCLUSIVE" && evidence.Token == null &&
                    evidence.Reason == "PARENT_SNAPSHOT_MISMATCH",
                    "connected sibling is rejected before token collection");
                Release(child);
                Release(otherParent);
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
            foreach (string mode in new[] { "renderer-claim-parent", "renderer-claim-distinct", "renderer-claim-duplicate", "renderer-claim-oversized" })
            {
                using var observer = RendererPipeObserver.Create();
                string releaseName = @"Local\aegis-p309j-claim-" + Guid.NewGuid().ToString("N");
                using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
                var helper = global::SelfTest.HelperStartInfo(mode, releaseName);
                string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(helper.FileName)));
                var runTask = ProcessRunner.RunAsync(helper.FileName, hash, helper.ArgumentList.ToArray(),
                    AppContext.BaseDirectory, 20000, true, (id, handle) => observer.Start(id, handle),
                    onStdoutLine: observer.AcceptLine);
                try { await observer.WaitForClaimEvidenceAsync().WaitAsync(TimeSpan.FromSeconds(12)); }
                catch { release.Set(); await runTask; throw; }
                finally { release.Set(); }
                var run = await runTask;
                var claim = await observer.FinishAsync();
                Check(run.ProcessExit == "NORMAL" && run.OutputComplete, mode + " helper completed with bounded output");
                if (mode is "renderer-claim-parent" or "renderer-claim-distinct")
                {
                    Check(claim.Status == "OBSERVED" && claim.RoleEvidence == "CLIENT_PID_CLAIM" &&
                        claim.RendererIdentity == "INCONCLUSIVE" && claim.SandboxEnforcement == "INCONCLUSIVE",
                        "live claimed direct-child token remains unverified renderer evidence");
                    Check(claim.AdditionalUnverifiedClaims == (mode == "renderer-claim-distinct" ? 1 : 0),
                        "additional distinct claims remain counted and unverified");
                }
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
