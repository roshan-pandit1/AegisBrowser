// Research helper for synthetic command processes only. It creates no WFP objects.
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AegisContainmentResearch
{
    public sealed class SuspendedSyntheticProcess : IDisposable
    {
        private IntPtr process, thread;
        private bool resumed;
        public uint Id { get; private set; }
        public string ExpectedPackageSid { get; private set; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public ushort ShowWindow, Reserved2Size;
            public IntPtr Reserved2, StandardInput, StandardOutput, StandardError;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityCapabilities { public IntPtr AppContainerSid, Capabilities; public uint Count, Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        private static extern int CreateAppContainerProfile(string name, string displayName, string description,
            IntPtr capabilities, uint count, out IntPtr sid);
        [DllImport("advapi32.dll")]
        private static extern IntPtr FreeSid(IntPtr sid);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr text);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value,
            IntPtr size, IntPtr previous, IntPtr returnSize);
        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern bool CreateProcess(string executable, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory,
            ref StartupInfoEx startup, out ProcessInformation information);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private SuspendedSyntheticProcess(ProcessInformation info, string expectedSid)
        { process = info.Process; thread = info.Thread; Id = info.ProcessId; ExpectedPackageSid = expectedSid; }

        private static IntPtr Derive(string name)
        {
            IntPtr sid;
            int hr = DeriveAppContainerSidFromAppContainerName(name, out sid);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            if (sid == IntPtr.Zero) throw new InvalidOperationException("SID derivation returned no SID.");
            return sid;
        }

        private static string FormatSid(IntPtr sid)
        {
            IntPtr text;
            if (!ConvertSidToStringSidW(sid, out text)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return Marshal.PtrToStringUni(text); }
            finally { LocalFree(text); }
        }

        public static string DeriveSyntheticSid(string name)
        {
            IntPtr sid = Derive(name);
            try { return FormatSid(sid); }
            finally { FreeSid(sid); }
        }

        // Run only on a disposable, isolated test host; Windows creates per-user profile data.
        // Deliberately no deletion: the disposable host owns cleanup of the synthetic identity.
        public static string RegisterSyntheticIdentity(string name)
        {
            IntPtr sid;
            int hr = CreateAppContainerProfile(name, name, "Aegis isolated research identity", IntPtr.Zero, 0, out sid);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            if (sid == IntPtr.Zero) throw new InvalidOperationException("Profile registration returned no SID.");
            try { return FormatSid(sid); }
            finally { FreeSid(sid); }
        }

        public static SuspendedSyntheticProcess Start(string name)
        {
            IntPtr sid = IntPtr.Zero, attributes = IntPtr.Zero, capabilities = IntPtr.Zero;
            bool initialized = false;
            try
            {
                sid = Derive(name);
                string expected = FormatSid(sid);
                IntPtr length = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref length);
                if (length == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Attribute list size query failed.");
                attributes = Marshal.AllocHGlobal(length);
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref length))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Attribute list initialization failed.");
                initialized = true;
                var sc = new SecurityCapabilities { AppContainerSid = sid, Capabilities = IntPtr.Zero, Count = 0, Reserved = 0 };
                capabilities = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SecurityCapabilities)));
                Marshal.StructureToPtr(sc, capabilities, false);
                const int SecurityCapabilitiesAttribute = 0x00020009;
                if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)SecurityCapabilitiesAttribute, capabilities,
                    (IntPtr)Marshal.SizeOf(typeof(SecurityCapabilities)), IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Security capabilities attribute failed.");
                var startup = new StartupInfoEx { Startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfoEx)) }, Attributes = attributes };
                var executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                var command = new StringBuilder("\"" + executable + "\" /d /c exit 0");
                ProcessInformation info;
                const uint SuspendedExtendedNoWindow = 0x00000004 | 0x00080000 | 0x08000000;
                if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, SuspendedExtendedNoWindow,
                    IntPtr.Zero, Environment.SystemDirectory, ref startup, out info))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Suspended synthetic process creation failed.");
                return new SuspendedSyntheticProcess(info, expected);
            }
            finally
            {
                if (initialized) DeleteProcThreadAttributeList(attributes);
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                if (capabilities != IntPtr.Zero) Marshal.FreeHGlobal(capabilities);
                if (sid != IntPtr.Zero) FreeSid(sid);
            }
        }

        // The process is a fixed, disposable `cmd /c exit` control, never a browser or user profile.
        public uint ResumeAndWait()
        {
            if (!resumed)
            {
                if (ResumeThread(thread) == UInt32.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
                resumed = true;
            }
            uint wait = WaitForSingleObject(process, 15000);
            if (wait != 0) throw new InvalidOperationException("Synthetic process did not exit; wait result " + wait);
            uint code;
            if (!GetExitCodeProcess(process, out code)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return code;
        }

        public void Dispose()
        {
            if (thread != IntPtr.Zero && !resumed) { ResumeThread(thread); resumed = true; }
            if (process != IntPtr.Zero) { WaitForSingleObject(process, 15000); CloseHandle(process); process = IntPtr.Zero; }
            if (thread != IntPtr.Zero) { CloseHandle(thread); thread = IntPtr.Zero; }
        }
    }
}
