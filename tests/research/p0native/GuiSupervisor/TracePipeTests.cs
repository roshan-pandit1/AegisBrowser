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
            try
            {
                var tracker = PassiveTrackingSession.Start(self.SafeHandle, (uint)self.Id, created, image, 10000);
                Check(await tracker.FirstScanAsync().WaitAsync(TimeSpan.FromSeconds(5)), "tracker initial scan completed");
                async Task<TracePipeObserver.Evidence> Scenario(string payload, bool closeChild = false)
                {
                    using var observer = TracePipeObserver.Create();
                    observer.Start(identity, self.SafeHandle, tracker);
                    using (var client = await Connect(observer.FullName))
                    {
                        await Write(client, "P0TRACE1 HELLO\n");
                        Check(await Read(client) == "P0TRACE1 ACK", "authenticated browser receives pre-trace barrier");
                        if (closeChild) await Release(child);
                        await Write(client, payload);
                        if (payload.Contains(" 0123456789ABCDEF0123456789ABCDEF\n", StringComparison.Ordinal))
                            Check(await Read(client) == "P0TRACE1 RECEIVED", "valid message receipt acknowledged");
                    }
                    return await observer.FinishAsync();
                }
                string valid = "P0TRACE1 " + child.Id + " 0123456789ABCDEF0123456789ABCDEF\n";
                var ok = await Scenario(valid);
                Check(ok.Status == "OBSERVED" && ok.CandidatePid == child.Id && ok.CandidateCreationFileTimeUtc > 0 &&
                    ok.RendererIdentity == "INCONCLUSIVE" && ok.SandboxEnforcement == "INCONCLUSIVE",
                    "browser report binds a pre-barrier live handle without proving renderer role");
                var duplicate = await Scenario(valid + valid);
                Check(duplicate.Reason == "TRACE_DUPLICATE_MESSAGE", "duplicate trace messages rejected: " + duplicate.Reason);
                var malformed = await Scenario("P0TRACE1 not-a-pid BAD\n");
                Check(malformed.Reason == "TRACE_MESSAGE_INVALID", "malformed trace message rejected");
                var stale = await Scenario(valid, true);
                Check(stale.Status == "INCONCLUSIVE" && stale.Reason == "RETAINED_LIFETIME_NOT_LIVE", "exited process rejected");
                await tracker.FinishAsync();
            }
            finally { await Release(child); }

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
