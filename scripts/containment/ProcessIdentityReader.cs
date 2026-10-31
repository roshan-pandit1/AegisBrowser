// Research-only, read-only snapshots. A process ID or snapshot is not an enforcement identity.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AegisContainmentResearch
{
    public sealed class ProcessLink
    {
        public uint Id { get; set; }
        public uint ParentId { get; set; }
        public string ImageName { get; set; }
    }

    public sealed class IdentityObservation
    {
        public uint Id { get; set; }
        public string CreationUtc { get; set; }
        public bool IsAppContainer { get; set; }
        public string PackageSid { get; set; }
        public ushort ProcessMachine { get; set; }
        public ushort NativeMachine { get; set; }
        public string Error { get; set; }
    }

    public static class ProcessIdentityReader
    {
        private const uint SnapshotProcess = 0x00000002;
        private const uint QueryLimitedInformation = 0x1000;
        private const uint TokenQuery = 0x0008;
        private const int TokenIsAppContainer = 29;
        private const int TokenAppContainerSid = 31;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            public uint Size, Usage, ProcessId;
            public UIntPtr DefaultHeapId;
            public uint ModuleId, Threads, ParentProcessId;
            public int BasePriority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit,
            out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process2(SafeProcessHandle process, out ushort processMachine, out ushort nativeMachine);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int tokenClass, IntPtr data, int size, out int returned);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        // Descendants are a point-in-time observation. Short-lived children, alternate parents,
        // and PID reuse can invalidate this graph; do not use it to enforce access or terminate.
        public static ProcessLink[] SnapshotDescendants(uint root)
        {
            var all = new List<ProcessLink>();
            using (var snapshot = CreateToolhelp32Snapshot(SnapshotProcess, 0))
            {
                if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
                if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
                do { all.Add(new ProcessLink { Id = entry.ProcessId, ParentId = entry.ParentProcessId, ImageName = entry.ExeFile }); }
                while (Process32Next(snapshot, ref entry));
                int error = Marshal.GetLastWin32Error();
                if (error != 18) throw new Win32Exception(error); // ERROR_NO_MORE_FILES
            }
            var seen = new HashSet<uint> { root };
            var result = new List<ProcessLink>();
            bool added;
            do
            {
                added = false;
                foreach (var item in all)
                    if (seen.Contains(item.ParentId) && seen.Add(item.Id)) { result.Add(item); added = true; }
            } while (added);
            return result.ToArray();
        }

        public static IdentityObservation Observe(uint id)
        {
            var result = new IdentityObservation { Id = id, CreationUtc = "", PackageSid = "", Error = "" };
            using (var process = OpenProcess(QueryLimitedInformation, false, id))
            {
                if (process.IsInvalid) { result.Error = "OpenProcess: " + Marshal.GetLastWin32Error(); return result; }
                long creation, exit, kernel, user;
                if (!GetProcessTimes(process, out creation, out exit, out kernel, out user))
                { result.Error = "GetProcessTimes: " + Marshal.GetLastWin32Error(); return result; }
                result.CreationUtc = DateTime.FromFileTimeUtc(creation).ToString("O");
                ushort processMachine, nativeMachine;
                if (!IsWow64Process2(process, out processMachine, out nativeMachine))
                { result.Error = "IsWow64Process2: " + Marshal.GetLastWin32Error(); return result; }
                result.ProcessMachine = processMachine; result.NativeMachine = nativeMachine;
                SafeAccessTokenHandle token;
                if (!OpenProcessToken(process, TokenQuery, out token))
                { result.Error = "OpenProcessToken: " + Marshal.GetLastWin32Error(); return result; }
                using (token)
                {
                    IntPtr value = Marshal.AllocHGlobal(sizeof(int));
                    try
                    {
                        int returned;
                        if (!GetTokenInformation(token, TokenIsAppContainer, value, sizeof(int), out returned))
                        { result.Error = "TokenIsAppContainer: " + Marshal.GetLastWin32Error(); return result; }
                        result.IsAppContainer = Marshal.ReadInt32(value) != 0;
                    }
                    finally { Marshal.FreeHGlobal(value); }
                    if (!result.IsAppContainer) return result;
                    int length;
                    GetTokenInformation(token, TokenAppContainerSid, IntPtr.Zero, 0, out length);
                    if (length <= 0) { result.Error = "TokenAppContainerSid size: " + Marshal.GetLastWin32Error(); return result; }
                    IntPtr info = Marshal.AllocHGlobal(length);
                    try
                    {
                        if (!GetTokenInformation(token, TokenAppContainerSid, info, length, out length))
                        { result.Error = "TokenAppContainerSid: " + Marshal.GetLastWin32Error(); return result; }
                        IntPtr sid = Marshal.ReadIntPtr(info), text;
                        if (sid == IntPtr.Zero || !ConvertSidToStringSidW(sid, out text))
                        { result.Error = "ConvertSidToStringSid: " + Marshal.GetLastWin32Error(); return result; }
                        try { result.PackageSid = Marshal.PtrToStringUni(text); }
                        finally { LocalFree(text); }
                    }
                    finally { Marshal.FreeHGlobal(info); }
                }
            }
            return result;
        }
    }
}
