using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using AegisBrowser.P309J;
using Microsoft.Win32.SafeHandles;

namespace P309g;

// Synthetic owned processes only; never a renderer or sandbox acceptance.
internal static class TracePipeTests
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);
    private static int checks;
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); checks++; Console.WriteLine("PASS: " + name); }

    private static Process StartChild()
    {
        var info = global::SelfTest.HelperStartInfo("renderer-hold");
        info.RedirectStandardInput = true;
        return Process.Start(info) ?? throw new InvalidOperationException("CHILD_START_FAILED");
    }
    private static async Task Release(Process child)
    { if (!child.HasExited) { await child.StandardInput.WriteLineAsync("RELEASE"); child.StandardInput.Close(); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
    private static async Task<NamedPipeClientStream> Connect(string full)
    {
        var client = new NamedPipeClientStream(".", full.Substring(@"\\.\pipe\".Length), PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        return client;
    }
    private static async Task Write(Stream stream, string message)
    { await stream.WriteAsync(Encoding.ASCII.GetBytes(message)); await stream.FlushAsync(); }
    private static async Task<string?> Read(Stream stream)
    {
        var bytes = new List<byte>(); var one = new byte[1];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (bytes.Count < 96 && await stream.ReadAsync(one, timeout.Token) != 0)
        { if (one[0] == '\n') return Encoding.ASCII.GetString(bytes.ToArray()); bytes.Add(one[0]); }
        return null;
    }
    internal static async Task<int> Run()
    {
        if (!OperatingSystem.IsWindows()) return 2;
        try
        {
            using var self = Process.GetCurrentProcess();
            if (!GetProcessTimes(self.SafeHandle, out long created, out _, out _, out _)) throw new InvalidOperationException("CREATION_UNAVAILABLE");
            string image = Environment.ProcessPath ?? throw new InvalidOperationException("IMAGE_UNAVAILABLE");
            var identity = new ProcessRunner.Identity(self.Id, created, image, "SYNTHETIC");
            using var child = StartChild();
            using var childBetween = StartChild();
            try
            {
                var tracker = PassiveTrackingSession.Start(self.SafeHandle, (uint)self.Id, created, image, 10000);
                Check(await tracker.FirstScanAsync().WaitAsync(TimeSpan.FromSeconds(5)), "tracker initial scan completed");
                var copied = tracker.DuplicateMatchBeforeBarrier((uint)child.Id, tracker.CaptureBarrier(), out var duplicateHandle);
                using var duplicateLifetime = duplicateHandle;
                Check(copied.Status == "OBSERVED" && duplicateHandle != null && !duplicateHandle.IsInvalid &&
                    GetProcessId(duplicateHandle) == child.Id, "pre-barrier child handle duplicated without reopening PID");
                var missing = tracker.DuplicateMatchBeforeBarrier((uint)child.Id, 0, out var missingHandle);
                Check(missing.Status == "INCONCLUSIVE" && missingHandle == null, "invalid barrier cannot duplicate a handle");
                async Task<TracePipeObserver.Evidence> Scenario(string payload, bool closeChild = false,
                    string? revalidation = null, bool omitRevalidation = false, bool closeBetween = false,
                    bool omitFinalization = false, bool duplicateFinalization = false)
                {
                    using var observer = TracePipeObserver.Create();
                    observer.Start(identity, self.SafeHandle, tracker);
                    using (var client = await Connect(observer.FullName))
                    {
                        await Write(client, "P0TRACE1 HELLO\n");
                        Check(await Read(client) == "P0TRACE1 ACK", "authenticated browser receives pre-trace barrier");
                        if (closeChild)
                        {
                            await Release(child);
                            // Process.HasExited can observe an exit code before
                            // Windows signals the process object. The tracker
                            // deliberately requires the stronger kernel signal.
                            uint wait = WaitForSingleObject(child.SafeHandle, 5000);
                            Check(wait == 0, "released child kernel handle signals exit: wait=" + wait);
                            var direct = tracker.MatchBeforeBarrier((uint)child.Id, tracker.CaptureBarrier());
                            Check(direct.Status == "INCONCLUSIVE" && direct.Reason == "RETAINED_LIFETIME_NOT_LIVE",
                                "tracker confirms child exit before pipe message: status=" + direct.Status + " reason=" + direct.Reason);
                        }
                        await Write(client, payload);
                        if (payload.Contains(" 0123456789ABCDEF0123456789ABCDEF\n", StringComparison.Ordinal))
                        {
                            Check(await Read(client) == "P0TRACE1 RECEIVED", "valid message receipt acknowledged");
                            if (closeBetween)
                            {
                                await Release(childBetween);
                                Check(WaitForSingleObject(childBetween.SafeHandle, 5000) == 0,
                                    "candidate exit is signaled between observations");
                            }
                            if (!omitRevalidation && !payload.Contains("\nP0TRACE1", StringComparison.Ordinal))
                            {
                                await Write(client, revalidation ?? payload.Replace("P0TRACE1 ", "P0TRACE2 ", StringComparison.Ordinal));
                                if (revalidation == null)
                                {
                                    Check(await Read(client) == "P0TRACE2 RECEIVED", "revalidation receipt acknowledged");
                                    if (!omitFinalization)
                                    {
                                        await Write(client, "P0TRACE3 STABLE\n");
                                        Check(await Read(client) == "P0TRACE3 RECEIVED", "final document receipt acknowledged");
                                        if (duplicateFinalization) await Write(client, "P0TRACE3 STABLE\n");
                                    }
                                }
                            }
                        }
                    }
                    return await observer.FinishAsync();
                }
                string valid = "P0TRACE1 " + child.Id + " 0123456789ABCDEF0123456789ABCDEF\n";
                var ok = await Scenario(valid);
                Check(ok.Status == "OBSERVED" && ok.CandidatePid == child.Id && ok.CandidateCreationFileTimeUtc > 0 &&
                    ok.RendererIdentity == "INCONCLUSIVE" && ok.SandboxEnforcement == "INCONCLUSIVE",
                    "browser report binds a pre-barrier live handle without proving renderer role");
                Check(ok.Token?.Status == "OBSERVED" && ok.Token.ProcessId == child.Id &&
                    ok.Token.CreationFileTimeUtc == ok.CandidateCreationFileTimeUtc &&
                    ok.TokenAfter?.Status == "OBSERVED" && ok.TokenAfter.ProcessId == child.Id &&
                    ok.TokenAfter.CreationFileTimeUtc == ok.CandidateCreationFileTimeUtc &&
                    ok.Token.QpcFrequency == Stopwatch.Frequency && ok.TokenAfter.QpcFrequency == Stopwatch.Frequency &&
                    ok.Token.QpcStart > 0 && ok.Token.QpcEnd >= ok.Token.QpcStart &&
                    ok.TokenAfter.QpcStart >= ok.Token.QpcEnd && ok.TokenAfter.QpcEnd >= ok.TokenAfter.QpcStart &&
                    ok.Token.ProcessRole == "NOT_ESTABLISHED" && ok.Token.SandboxEnforcement == "INCONCLUSIVE",
                    "both ordered retained-handle token snapshots match without promoting role");
                using (var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(ok)))
                {
                    var tokenJson = json.RootElement.GetProperty("Token");
                    Check(tokenJson.GetProperty("ProcessId").GetUInt32() == child.Id &&
                        tokenJson.GetProperty("CreationFileTimeUtc").GetInt64() == ok.CandidateCreationFileTimeUtc &&
                        tokenJson.GetProperty("ProcessRole").GetString() == "NOT_ESTABLISHED" &&
                        tokenJson.GetProperty("SandboxEnforcement").GetString() == "INCONCLUSIVE",
                        "serialized token evidence preserves handle identity and inconclusive security status");
                }
                var duplicate = await Scenario(valid + valid);
                Check(duplicate.Reason == "TRACE_DUPLICATE_MESSAGE", "duplicate trace messages rejected: " + duplicate.Reason);
                var omitted = await Scenario(valid, omitRevalidation: true);
                Check(omitted.Reason == "TRACE_REVALIDATION_MISSING", "missing revalidation rejected: " + omitted.Reason);
                var noFinal = await Scenario(valid, omitFinalization: true);
                Check(noFinal.Reason == "TRACE_FINALIZATION_MISSING", "missing final document check rejected: " + noFinal.Reason);
                var duplicateFinal = await Scenario(valid, duplicateFinalization: true);
                Check(duplicateFinal.Reason == "TRACE_DUPLICATE_MESSAGE", "duplicate final document check rejected: " + duplicateFinal.Reason);
                var mismatch = await Scenario(valid, revalidation: "P0TRACE2 1 0123456789ABCDEF0123456789ABCDEF\n");
                Check(mismatch.Reason == "TRACE_REVALIDATION_MISMATCH", "changed candidate rejected: " + mismatch.Reason);
                string between = "P0TRACE1 " + childBetween.Id + " 0123456789ABCDEF0123456789ABCDEF\n";
                var exitedBetween = await Scenario(between, closeBetween: true);
                Check(exitedBetween.Status == "INCONCLUSIVE" && exitedBetween.Reason == "RETAINED_LIFETIME_NOT_LIVE",
                    "candidate exit between observations rejected: " + exitedBetween.Reason);
                var malformed = await Scenario("P0TRACE1 not-a-pid BAD\n");
                Check(malformed.Reason == "TRACE_MESSAGE_INVALID", "malformed trace message rejected");
                var stale = await Scenario(valid, true);
                Check(stale.Status == "INCONCLUSIVE" && stale.Reason == "RETAINED_LIFETIME_NOT_LIVE",
                    "exited process rejected: status=" + stale.Status + " reason=" + stale.Reason);
                var exitedCopy = tracker.DuplicateMatchBeforeBarrier((uint)child.Id, tracker.CaptureBarrier(), out var exitedHandle);
                Check(exitedCopy.Status == "INCONCLUSIVE" && exitedHandle == null, "exited candidate cannot yield a new duplicate");
                await tracker.FinishAsync();
                var afterFinish = tracker.DuplicateMatchBeforeBarrier((uint)child.Id, 1, out var afterFinishHandle);
                Check(afterFinish.Reason == "SESSION_FINISHED" && afterFinishHandle == null,
                    "finished tracking session cannot issue a duplicate handle");
                Check(duplicateHandle != null && !duplicateHandle.IsClosed && GetProcessId(duplicateHandle) == child.Id &&
                    WaitForSingleObject(duplicateHandle, 0) == 0, "owned duplicate outlives tracker and observes the same exited process");
            }
            finally { await Release(child); await Release(childBetween); }

            using var peer = StartChild();
            try
            {
                if (!GetProcessTimes(peer.SafeHandle, out long peerCreated, out _, out _, out _)) throw new InvalidOperationException("PEER_CREATION_UNAVAILABLE");
                var peerIdentity = new ProcessRunner.Identity(peer.Id, peerCreated, image, "SYNTHETIC");
                var tracker = PassiveTrackingSession.Start(peer.SafeHandle, (uint)peer.Id, peerCreated, image, 10000);
                using var observer = TracePipeObserver.Create();
                observer.Start(peerIdentity, peer.SafeHandle, tracker);
                using (var client = await Connect(observer.FullName)) { await Write(client, "P0TRACE1 HELLO\n"); }
                var wrong = await observer.FinishAsync();
                Check(wrong.Status == "INCONCLUSIVE" && wrong.Reason == "TRACE_CLIENT_NOT_RETAINED_BROWSER", "wrong pipe peer rejected by kernel PID");
                await tracker.FinishAsync();
            }
            finally { await Release(peer); }
            Console.WriteLine($"PASS: {checks} synthetic trace correlation checks; renderer role and sandbox INCONCLUSIVE");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("TRACE_SELF_TEST_FAILED: " + ex); return 1; }
    }
}
