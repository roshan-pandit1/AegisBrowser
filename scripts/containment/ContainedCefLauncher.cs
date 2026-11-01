// Research only: this launcher accepts a disposable test executable and never installs WFP policy.
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AegisContainmentResearch
{
    public sealed class ContainedCefLauncher : IDisposable
    {
        private IntPtr process, thread;
        private bool resumed, verified;
        public uint Id { get; private set; }
        public string ExpectedSid { get; private set; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
            public ushort Show, ReservedSize;
            public IntPtr Reserved2, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)] private struct SecurityCapabilities { public IntPtr Sid, Capabilities; public uint Count, Reserved; }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint Id, ThreadId; }

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
        [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr text);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int type, IntPtr data, int size, out int returned);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern bool CreateProcess(string executable, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation info);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

        public static ContainedCefLauncher Start(string identity, string executable, string syntheticRoot)
        { return StartMode(identity, executable, syntheticRoot, "appcontainer-probe"); }

        public static ContainedCefLauncher StartObserver(string identity, string executable, string syntheticRoot)
        { return StartMode(identity, executable, syntheticRoot, "appcontainer-observer"); }

        private static ContainedCefLauncher StartMode(string identity, string executable, string syntheticRoot, string mode)
        {
            executable = Path.GetFullPath(executable);
            syntheticRoot = Path.GetFullPath(syntheticRoot);
            if (!String.Equals(Path.GetFileName(executable), "AegisBrowser.BrowserTests.exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(executable) || !Directory.Exists(syntheticRoot))
                throw new ArgumentException("An existing synthetic CEF fixture and root are required.");
            IntPtr sid = IntPtr.Zero, attributes = IntPtr.Zero, caps = IntPtr.Zero, text = IntPtr.Zero, environment = IntPtr.Zero;
            bool initialized = false;
            try
            {
                int hr = DeriveAppContainerSidFromAppContainerName(identity, out sid);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                if (sid == IntPtr.Zero || !ConvertSidToStringSidW(sid, out text)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read AppContainer SID.");
                string expected = Marshal.PtrToStringUni(text);
                IntPtr size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                if (size == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot size process attributes.");
                attributes = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot initialize process attributes.");
                initialized = true;
                var capability = new SecurityCapabilities { Sid = sid, Capabilities = IntPtr.Zero, Count = 0, Reserved = 0 };
                caps = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SecurityCapabilities)));
                Marshal.StructureToPtr(capability, caps, false);
                if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020009, caps, (IntPtr)Marshal.SizeOf(typeof(SecurityCapabilities)), IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot apply AppContainer security capabilities.");
                var startup = new StartupInfoEx { Startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfoEx)) }, Attributes = attributes };
                var command = new StringBuilder("\"" + executable + "\" " + mode + " \"" + syntheticRoot + "\"");
                ProcessInformation info;
                // Do not inherit CI secrets or the real user profile. These paths are synthetic.
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string env = "LOCALAPPDATA=" + syntheticRoot + "\0PATH=" + Environment.SystemDirectory + "\0SystemRoot=" + windows +
                    "\0TEMP=" + syntheticRoot + "\0TMP=" + syntheticRoot + "\0USERPROFILE=" + syntheticRoot + "\0WINDIR=" + windows + "\0\0";
                environment = Marshal.StringToHGlobalUni(env);
                const uint flags = 0x00000004 | 0x00080000 | 0x08000000 | 0x00000400;
                if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, flags, environment, Path.GetDirectoryName(executable), ref startup, out info))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create suspended CEF fixture.");
                return new ContainedCefLauncher { process = info.Process, thread = info.Thread, Id = info.Id, ExpectedSid = expected };
            }
            finally
            {
                if (initialized) DeleteProcThreadAttributeList(attributes);
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                if (caps != IntPtr.Zero) Marshal.FreeHGlobal(caps);
                if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
                if (text != IntPtr.Zero) LocalFree(text);
                if (sid != IntPtr.Zero) FreeSid(sid);
            }
        }

        // Verify the retained process handle itself before executing any browser code.
        public string Verify(string requestedSid)
        {
            if (resumed) throw new InvalidOperationException("Already resumed.");
            verified = false;
            if (!String.Equals(requestedSid, ExpectedSid, StringComparison.Ordinal)) throw new InvalidOperationException("Requested identity differs from launch identity.");
            IntPtr token;
            if (!OpenProcessToken(process, 0x0008, out token)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open suspended CEF token.");
            try
            {
                int length;
                IntPtr flag = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    if (!GetTokenInformation(token, 29, flag, sizeof(int), out length)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query AppContainer status.");
                    if (Marshal.ReadInt32(flag) == 0) throw new InvalidOperationException("Suspended CEF process is not an AppContainer.");
                }
                finally { Marshal.FreeHGlobal(flag); }
                GetTokenInformation(token, 31, IntPtr.Zero, 0, out length);
                if (length <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot size AppContainer SID.");
                IntPtr info = Marshal.AllocHGlobal(length), sidText = IntPtr.Zero;
                try
                {
                    if (!GetTokenInformation(token, 31, info, length, out length)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query AppContainer SID.");
                    IntPtr sid = Marshal.ReadIntPtr(info);
                    if (sid == IntPtr.Zero || !ConvertSidToStringSidW(sid, out sidText)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot format AppContainer SID.");
                    string observed = Marshal.PtrToStringUni(sidText);
                    if (!String.Equals(observed, ExpectedSid, StringComparison.Ordinal)) throw new InvalidOperationException("Suspended CEF token SID does not match the requested identity.");
                }
                finally { if (sidText != IntPtr.Zero) LocalFree(sidText); Marshal.FreeHGlobal(info); }
            }
            finally { if (!CloseHandle(token)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot close token handle."); }
            verified = true;
            return ExpectedSid;
        }
        public void Resume()
        {
            if (!verified || resumed) throw new InvalidOperationException("A verified suspended CEF process is required.");
            if (ResumeThread(thread) == UInt32.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resume verified CEF fixture.");
            resumed = true;
        }
        public bool Wait(uint milliseconds) { uint result = WaitForSingleObject(process, milliseconds); if (result == 0) return true; if (result == 258) return false; throw new Win32Exception(Marshal.GetLastWin32Error(), "Wait for CEF fixture failed."); }
        public string CreationUtc() { long creation, exit, kernel, user; if (!GetProcessTimes(process, out creation, out exit, out kernel, out user)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query CEF process creation time."); return DateTime.FromFileTimeUtc(creation).ToString("O"); }
        public uint ExitCode() { uint code; if (!GetExitCodeProcess(process, out code)) throw new Win32Exception(Marshal.GetLastWin32Error()); return code; }
        public void StopTimedOutTest()
        {
            if (!resumed) throw new InvalidOperationException("Only a resumed synthetic test can time out.");
            if (!TerminateProcess(process, 1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot stop timed-out synthetic CEF process.");
            if (WaitForSingleObject(process, 15000) != 0) throw new InvalidOperationException("Timed-out synthetic CEF process did not stop.");
        }
        public void Dispose()
        {
            // Never resume an unverified process. Terminate only this retained synthetic test handle.
            Exception failure = null;
            try
            {
                if (process != IntPtr.Zero)
                {
                    uint state = WaitForSingleObject(process, 0);
                    if (state != 0 && state != 258) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect synthetic process exit.");
                    if (state == 258)
                    {
                        if (!TerminateProcess(process, 1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot terminate owned synthetic process.");
                        if (WaitForSingleObject(process, 15000) != 0) throw new InvalidOperationException("Owned synthetic process did not stop.");
                    }
                }
            }
            catch (Exception ex) { failure = ex; }
            if (thread != IntPtr.Zero) { if (!CloseHandle(thread) && failure == null) failure = new Win32Exception(Marshal.GetLastWin32Error()); thread = IntPtr.Zero; }
            if (process != IntPtr.Zero) { if (!CloseHandle(process) && failure == null) failure = new Win32Exception(Marshal.GetLastWin32Error()); process = IntPtr.Zero; }
            if (failure != null) throw failure;
        }
    }
}
