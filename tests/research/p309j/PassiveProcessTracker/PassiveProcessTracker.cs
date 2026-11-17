using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AegisBrowser.P309J;

// A bounded, passive, point-in-time discovery aid. It is NOT a process
// creation subscription: children can start and exit between snapshots or
// before the first scan. Parentage can be spoofed by same-user code and PIDs
// may be recycled. Handles stabilize only the lifetimes opened after a scan;
// mapped-image hashes and renderer roles are not established here.
public sealed class PassiveProcessTracker : IDisposable
{
    public sealed record ProcessRecord(uint Pid, long CreationFileTimeUtc,
        uint ParentPid, int Depth, string Role, string Exit, uint? ExitCode);
    public sealed record Report(string Discovery, string Accounting, string Cleanup,
        int Snapshots, int CandidatesRejected, bool LimitReached,
        string LastError, IReadOnlyList<ProcessRecord> Processes);

    private const uint QueryLimited = 0x1000, Synchronize = 0x100000;
    private const uint WaitObject0 = 0, WaitTimeout = 258;
    private const int MaxEntries = 32768;
    private const int MaxProcesses = 32;
    private const int MaxScans = 128;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        internal uint Size, Usage, Pid;
        internal UIntPtr Heap;
        internal uint Module, Threads, ParentPid;
        internal int Priority;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Image;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInformation
    {
        internal IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority;
        internal UIntPtr UniqueProcessId, InheritedFromUniqueProcessId;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation,
        out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags,
        StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass,
        out BasicInformation information, int length, out int returned);

    private sealed record Held(uint Pid, long Created, uint ParentPid, int Depth, SafeProcessHandle Handle);
    private readonly SafeProcessHandle root;
    private readonly uint rootPid;
    private readonly long rootCreated;
    private readonly string expectedImage;
    private readonly string expectedName;
    private readonly Dictionary<uint, Held> held = new();
    private int scans, successfulScans, rejected;
    private bool limit, disposed;
    private string lastError = "";

    // Caller retains ownership of the root handle until tracking ends. Do not
    // call Scan concurrently or after disposing that handle. The host may call
    // Finish after root exit, but may not infer that all descendants exited.
    public PassiveProcessTracker(SafeProcessHandle root, uint rootPid,
        long rootCreated, string expectedImage)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (root == null || root.IsInvalid || root.IsClosed || rootPid == 0 || rootCreated <= 0 ||
            string.IsNullOrWhiteSpace(expectedImage) || !Path.IsPathFullyQualified(expectedImage))
            throw new ArgumentException("ROOT_IDENTITY_INVALID");
        this.root = root;
        this.rootPid = rootPid;
        this.rootCreated = rootCreated;
        this.expectedImage = Path.GetFullPath(expectedImage);
        expectedName = Path.GetFileName(this.expectedImage);
        if (!Matches(root, rootPid, rootCreated, this.expectedImage))
            throw new ArgumentException("ROOT_IDENTITY_MISMATCH_OR_NOT_LIVE");
    }

    // One point-in-time snapshot; no loop, no network, no process termination.
    // Only entries with a live tracked parent and matching executable basename
    // are opened. Exact image and parent are rechecked on each opened handle.
    public bool Scan()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (scans >= MaxScans) { limit = true; lastError = "SCAN_LIMIT"; return false; }
        if (!Matches(root, rootPid, rootCreated, expectedImage))
        { lastError = "ROOT_NOT_LIVE_OR_MISMATCH"; return false; }
        scans++;
        var entries = new List<(uint Pid, uint Parent)>();
        using (var snapshot = CreateToolhelp32Snapshot(2, 0))
        {
            if (snapshot.IsInvalid) { lastError = "SNAPSHOT_FAILED"; return false; }
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Image = "" };
            if (!Process32FirstW(snapshot, ref entry)) { lastError = "SNAPSHOT_FIRST_FAILED"; return false; }
            int count = 0;
            do
            {
                if (++count > MaxEntries) { limit = true; lastError = "SNAPSHOT_ENTRY_LIMIT"; return false; }
                if (entry.Pid != 0 && string.Equals(entry.Image, expectedName, StringComparison.OrdinalIgnoreCase))
                    entries.Add((entry.Pid, entry.ParentPid));
            } while (Process32NextW(snapshot, ref entry));
            // ERROR_NO_MORE_FILES is expected, other enumeration failures are
            // never hidden as complete coverage.
            if (Marshal.GetLastWin32Error() != 18) { lastError = "SNAPSHOT_NEXT_FAILED"; return false; }
        }
        // The snapshot order is unspecified; bounded passes can discover a
        // nested child only after its live parent has itself been retained.
        var rejectedThisScan = new HashSet<uint>();
        for (int pass = 0; pass <= MaxProcesses; pass++)
        {
            bool added = false;
            foreach (var entry in entries)
            {
                if (entry.Pid == rootPid || held.ContainsKey(entry.Pid) || rejectedThisScan.Contains(entry.Pid)) continue;
                SafeProcessHandle? parentHandle;
                long parentCreated;
                int depth;
                if (entry.Parent == rootPid) { parentHandle = root; parentCreated = rootCreated; depth = 1; }
                else if (held.TryGetValue(entry.Parent, out var known))
                { parentHandle = known.Handle; parentCreated = known.Created; depth = known.Depth + 1; }
                else continue;
                if (!Matches(parentHandle, entry.Parent, parentCreated, expectedImage)) { rejected++; rejectedThisScan.Add(entry.Pid); continue; }
                if (held.Count >= MaxProcesses) { limit = true; lastError = "PROCESS_LIMIT"; return false; }
                var child = OpenProcess(QueryLimited | Synchronize, false, entry.Pid);
                bool retain = false;
                try
                {
                    if (child.IsInvalid || !GetProcessTimes(child, out long created, out _, out _, out _) ||
                        created < parentCreated || !Matches(child, entry.Pid, created, expectedImage) ||
                        !HasParent(child, entry.Pid, entry.Parent) ||
                        !Matches(parentHandle, entry.Parent, parentCreated, expectedImage))
                    { rejected++; rejectedThisScan.Add(entry.Pid); continue; }
                    held.Add(entry.Pid, new Held(entry.Pid, created, entry.Parent, depth, child));
                    retain = true;
                    added = true;
                }
                finally { if (!retain) child.Dispose(); }
            }
            if (!added) break;
        }
        lastError = "";
        successfulScans++;
        return true;
    }

    public Report Finish()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var records = new List<ProcessRecord>();
        foreach (var process in held.Values.OrderBy(p => p.Pid))
        {
            uint wait = WaitForSingleObject(process.Handle, 0);
            string status = wait == WaitTimeout ? "STILL_RUNNING" : wait == WaitObject0 ? "EXITED_CODE_UNAVAILABLE" : "UNAVAILABLE";
            uint? code = null;
            if (wait == WaitObject0 && GetExitCodeProcess(process.Handle, out uint observed))
            { status = "EXITED"; code = observed; }
            records.Add(new(process.Pid, process.Created, process.ParentPid, process.Depth, "UNKNOWN", status, code));
        }
        // Even every retained child exiting cannot cover undetected lifetimes.
        return new(successfulScans > 0 ? "PARTIAL" : "INCONCLUSIVE",
            successfulScans > 0 ? "PARTIAL" : "INCONCLUSIVE", "INCONCLUSIVE", scans,
            rejected, limit, lastError, records);
    }

    private static bool HasParent(SafeProcessHandle child, uint pid, uint parentPid)
    {
        int size = Marshal.SizeOf<BasicInformation>();
        return NtQueryInformationProcess(child, 0, out var info, size, out int returned) == 0 &&
               returned == size && info.UniqueProcessId.ToUInt64() == pid &&
               info.InheritedFromUniqueProcessId.ToUInt64() == parentPid;
    }

    private static bool Matches(SafeProcessHandle process, uint pid, long created, string image)
    {
        if (process.IsInvalid || process.IsClosed || WaitForSingleObject(process, 0) != WaitTimeout ||
            GetProcessId(process) != pid || !GetProcessTimes(process, out long actual, out _, out _, out _) || actual != created)
            return false;
        var name = new StringBuilder(32768);
        int size = name.Capacity;
        return QueryFullProcessImageNameW(process, 0, name, ref size) &&
               string.Equals(name.ToString(), image, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var process in held.Values) process.Handle.Dispose();
        held.Clear();
    }
}
