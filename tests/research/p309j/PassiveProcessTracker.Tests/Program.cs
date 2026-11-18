using System.Diagnostics;
using System.Runtime.InteropServices;
using AegisBrowser.P309J;
using Microsoft.Win32.SafeHandles;

if (args.Length > 0 && args[0] == "--child")
{
    Process? nested = null;
    try
    {
        if (args.Length == 2 && args[1] == "nested")
        {
            nested = Process.Start(ChildInfo()) ?? throw new InvalidOperationException("nested start failed");
            if (await nested.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) != "READY") return 5;
            Console.WriteLine("NESTED=" + nested.Id);
        }
        Console.WriteLine("READY");
        await Console.In.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
        if (nested != null)
        {
            await nested.StandardInput.WriteLineAsync("exit");
            await nested.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if (nested.ExitCode != 0) return 6;
        }
        return 0;
    }
    finally { nested?.Dispose(); }
}

static ProcessStartInfo ChildInfo(string? extra = null)
{
    string exe = Environment.ProcessPath ?? throw new InvalidOperationException("missing executable");
    var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
        info.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
    info.ArgumentList.Add("--child");
    if (extra != null) info.ArgumentList.Add(extra);
    return info;
}

if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("NOT_RUN: Windows required"); return 2; }
int checks = 0;
void Check(bool ok, string name)
{
    if (!ok) throw new InvalidOperationException("FAIL: " + name);
    checks++; Console.WriteLine("PASS: " + name);
}

try
{
    using var self = Process.GetCurrentProcess();
    string image = Environment.ProcessPath ?? throw new InvalidOperationException("missing executable");
    if (!Native.GetProcessTimes(self.SafeHandle, out long created, out _, out _, out _))
        throw new InvalidOperationException("creation unavailable");
    bool rejected = false;
    try { using var bad = new PassiveProcessTracker(self.SafeHandle, (uint)self.Id, created + 1, image); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "stale parent creation time rejected");
    rejected = false;
    try { using var bad = new PassiveProcessTracker(self.SafeHandle, (uint)self.Id, created, image + ".wrong"); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "wrong parent image rejected");

    using var tracker = new PassiveProcessTracker(self.SafeHandle, (uint)self.Id, created, image);
    var initial = tracker.Finish();
    Check(initial.Accounting == "INCONCLUSIVE" && initial.Cleanup == "INCONCLUSIVE", "no scan cannot establish accounting");
    using var child = Process.Start(ChildInfo("nested")) ?? throw new InvalidOperationException("child start failed");
    using var sibling = Process.Start(ChildInfo()) ?? throw new InvalidOperationException("sibling start failed");
    try
    {
        string? first = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(first != null && first.StartsWith("NESTED=", StringComparison.Ordinal), "nested helper launched");
        int nestedPid = int.Parse(first![7..]);
        Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "READY", "child ready barrier");
        Check(await sibling.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "READY", "second child ready barrier");
        Check(tracker.Scan(), "bounded real snapshot completed");
        var live = tracker.Finish();
        Check(live.Processes.Any(p => p.Pid == child.Id && p.ParentPid == self.Id && p.Depth == 1 && p.Exit == "STILL_RUNNING"), "live direct child discovered");
        Check(live.Processes.Any(p => p.Pid == sibling.Id && p.ParentPid == self.Id && p.Depth == 1), "second direct child discovered");
        Check(live.Processes.Any(p => p.Pid == nestedPid && p.ParentPid == child.Id && p.Depth == 2), "nested child discovered from live parent");
        Check(live.Processes.All(p => p.Role == "UNKNOWN"), "snapshot does not invent process roles");
        Check(live.Accounting == "PARTIAL" && live.Cleanup == "INCONCLUSIVE", "live snapshot cannot claim complete accounting or cleanup");
        Check(tracker.Scan(), "repeat scan completed");
        Check(tracker.Finish().Processes.Count(p => p.Pid == child.Id) == 1, "repeat scan does not duplicate a lifetime");
        await child.StandardInput.WriteLineAsync("exit");
        await sibling.StandardInput.WriteLineAsync("exit");
        await Task.WhenAll(child.WaitForExitAsync(), sibling.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(8));
        Check(child.ExitCode == 0 && sibling.ExitCode == 0, "owned helpers exited normally");
        var exited = tracker.Finish();
        Check(exited.Processes.Any(p => p.Pid == child.Id && p.Exit == "EXITED" && p.ExitCode == 0), "retained direct child handle observes exit zero");
        Check(exited.Processes.Any(p => p.Pid == nestedPid && p.Exit == "EXITED" && p.ExitCode == 0), "retained nested child handle observes exit zero");
        Check(exited.Accounting == "PARTIAL" && exited.Cleanup == "INCONCLUSIVE", "known exits do not establish complete cleanup");
    }
    finally
    {
        foreach (var owned in new[] { child, sibling })
            if (!owned.HasExited) { owned.StandardInput.Close(); await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
    }
    tracker.Dispose();
    rejected = false;
    try { tracker.Scan(); } catch (ObjectDisposedException) { rejected = true; }
    Check(rejected, "disposed observer refuses further scans");
    var session = PassiveTrackingSession.Start(self.SafeHandle, (uint)self.Id, created, image);
    Check(await session.FirstScanAsync().WaitAsync(TimeSpan.FromSeconds(5)), "first scan completes without a timing sleep");
    var sessionReport = await session.FinishAsync();
    Check(sessionReport.Snapshots > 0 && sessionReport.Accounting == "PARTIAL" && sessionReport.Cleanup == "INCONCLUSIVE",
        "asynchronous session reports bounded passive coverage");
    rejected = false;
    try { await session.FinishAsync(); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "session completion is single-use");

    // The supervisor may dispose its original parent handle after the test
    // parent exits. The session's duplicate must survive through FinishAsync.
    using (var rootChild = Process.Start(ChildInfo("nested")) ?? throw new InvalidOperationException("root child start failed"))
    {
        try
        {
            string? childPidLine = await rootChild.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(childPidLine != null && childPidLine.StartsWith("NESTED=", StringComparison.Ordinal), "session-root nested helper launched");
            Check(await rootChild.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "READY", "session root ready barrier");
            if (!Native.GetProcessTimes(rootChild.SafeHandle, out long rootChildCreated, out _, out _, out _))
                throw new InvalidOperationException("session root creation unavailable");
            var exitingSession = PassiveTrackingSession.Start(rootChild.SafeHandle, (uint)rootChild.Id, rootChildCreated, image);
            Check(await exitingSession.FirstScanAsync().WaitAsync(TimeSpan.FromSeconds(5)), "session scans while root lives");
            await rootChild.StandardInput.WriteLineAsync("exit");
            await rootChild.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Check(rootChild.ExitCode == 0, "session root exited normally");
            rootChild.Dispose();
            var exitReport = await exitingSession.FinishAsync();
            Check(exitReport.Accounting == "PARTIAL" && exitReport.Cleanup == "INCONCLUSIVE", "root exit and original-handle disposal never imply complete cleanup");
            Check(exitReport.Processes.Any(p => p.Pid == int.Parse(childPidLine![7..]) && p.Exit == "EXITED" && p.ExitCode == 0), "duplicated root ownership preserves observed nested exit");
        }
        finally
        {
            // The normal test path has already disposed the handle. On an
            // earlier failure, close only the owned helper's input to allow it
            // to exit; never terminate an unrelated process.
            try { if (!rootChild.HasExited) { rootChild.StandardInput.Close(); await rootChild.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); } }
            catch (InvalidOperationException) { }
        }
    }
    Console.WriteLine($"PASS: {checks} passive process-discovery checks; snapshots do not prove complete accounting");
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e); return 1; }

static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessTimes(SafeProcessHandle process, out long creation,
        out long exit, out long kernel, out long user);
}
