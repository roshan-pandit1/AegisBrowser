using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using AegisBrowser.Core;
using Microsoft.Win32.SafeHandles;

// OS observation only. This never starts CEF or authorizes a download.
// Both processes are guarded to run only on an acknowledged disposable runner.
[SupportedOSPlatform("windows")]
internal static class DownloadAclRaceObservationTests
{
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint ReadAttributes = 0x80;
    private const uint ListDirectory = 0x01;
    private const uint ShareAll = 0x01 | 0x02 | 0x04;
    private const uint DirectoryFlags = 0x02000000 | 0x00200000;
    private const uint DaclSecurityInformation = 0x04;
    private const uint InvalidAttributes = 0xffffffff;
    // Reuse the native download fixture's bounded event deadline. Expiry fails
    // the experiment and triggers cleanup; it is never treated as a result.
    private static readonly TimeSpan EventDeadline = TimeSpan.FromSeconds(25);

    internal static async Task RunDisposable()
    {
        DownloadDirectoryObservationTests.RequireDisposable();
        if (IntPtr.Size != 8) throw new InvalidOperationException("The observation requires Windows x64.");
        var temp = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? throw new IOException("Runner temp is unavailable."));
        var nonce = Guid.NewGuid().ToString("N");
        var root = Path.Combine(temp, "aegis-acl-observation-" + nonce);
        var stage = Path.Combine(root, "owned-stage");
        if (!CreateDirectoryW(root, IntPtr.Zero)) throw new IOException("Could not create a new owned fixture root.", new Win32Exception(Marshal.GetLastWin32Error()));
        using var heldRoot = DownloadDirectoryObservation.OpenWithListAccessForTest(root);
        if (!CreateDirectoryW(stage, IntPtr.Zero)) throw new IOException("Could not create a new owned fixture directory.", new Win32Exception(Marshal.GetLastWin32Error()));
        using var held = DownloadDirectoryObservation.OpenWithListAccessForTest(stage);
        using (var marker = new FileStream(Path.Combine(root, "ownership-marker"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(marker)) writer.Write(nonce);
        if (Directory.EnumerateFileSystemEntries(stage).Any()) throw new Exception("FAIL: fixture directory was not empty");

        using var restoration = OpenSecurityHandle(stage);
        var identity = held.Identity;
        if (Identity(restoration) != identity) throw new Exception("FAIL: restoration handle identity differs");
        var original = ReadDacl(restoration);
        var initialAttributes = Attributes(stage, out var initialError);
        using (var control = OpenList(stage, out var controlError))
        {
            if (initialAttributes == InvalidAttributes || (initialAttributes & 0x10) == 0 || control.IsInvalid)
                throw new Exception($"FAIL: fixture access control failed; attributes={initialError}, list={controlError}");
        }

        var executable = Environment.ProcessPath ?? throw new IOException("Test executable is unavailable.");
        if (!string.Equals(Path.GetFileName(executable), "AegisBrowser.Tests.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The worker must be launched from the built test executable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add("--download-acl-race-worker");
        start.ArgumentList.Add(stage);
        start.ArgumentList.Add(nonce);
        start.ArgumentList.Add(identity.VolumeSerial.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(identity.FileIdLow.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(identity.FileIdHigh.ToString(CultureInfo.InvariantCulture));
        using var worker = new Process { StartInfo = start };
        if (!worker.Start()) throw new IOException("Could not launch the ACL fixture worker.");
        Exception? cleanupError = null;
        try
        {
            var signal = await worker.StandardOutput.ReadLineAsync().WaitAsync(EventDeadline);
            if (signal != "ACL_APPLIED") throw new Exception("FAIL: worker did not apply and verify the fixture DACL");
            if (held.ReadHeldIdentity() != identity || Identity(restoration) != identity)
                throw new Exception("FAIL: held fixture identity changed during ACL observation");

            var attributes = Attributes(stage, out var attributeError);
            using var fresh = OpenList(stage, out var listError);
            var directoryExists = attributes != InvalidAttributes && (attributes & 0x10) != 0;
            var createAttempted = false;
            var createSucceeded = false;
            var createError = 0;
            if (!directoryExists)
            {
                // Only the uniquely created fixture path is tested; no CEF or
                // user destination is involved. Do not delete a replacement.
                if (held.ReadHeldIdentity() != identity) throw new Exception("FAIL: held directory identity changed before control");
                createAttempted = true;
                createSucceeded = CreateDirectoryW(stage, IntPtr.Zero);
                createError = createSucceeded ? 0 : Marshal.GetLastWin32Error();
            }
            Console.WriteLine("ACL_OBSERVATION: fixture_control_attributes=true fixture_control_list_open=true held_identity_unchanged=true");
            Console.WriteLine($"ACL_OBSERVATION: after_deny_directory_exists={directoryExists} attributes_error={attributeError} fresh_list_open={!fresh.IsInvalid} list_error={listError}");
            Console.WriteLine($"ACL_OBSERVATION: create_attempted={createAttempted} create_succeeded={createSucceeded} create_error={createError} cef_fallback_condition={(!directoryExists && createAttempted && !createSucceeded)}");
            if (createSucceeded) throw new Exception("FAIL: a new directory appeared at the fixture path");
        }
        finally
        {
            // The worker restores its saved DACL on normal completion or stdin
            // closure. The parent waits for it to exit, then unconditionally
            // restores and verifies the independently retained original DACL.
            try
            {
                if (!worker.HasExited)
                {
                    await worker.StandardInput.WriteLineAsync("RESTORE");
                    await worker.StandardInput.FlushAsync();
                }
                await worker.WaitForExitAsync().WaitAsync(EventDeadline);
                if (worker.ExitCode != 0) cleanupError = new Exception("Worker failed while restoring its fixture DACL.");
            }
            catch (Exception ex)
            {
                cleanupError = ex;
                try
                {
                    if (!worker.HasExited) worker.Kill(entireProcessTree: true);
                    await worker.WaitForExitAsync().WaitAsync(EventDeadline);
                }
                catch (Exception killError) { cleanupError = new AggregateException(ex, killError); }
            }
            finally
            {
                try
                {
                    WriteDacl(restoration, original.Acl);
                    var restored = ReadDacl(restoration);
                    if (!original.Acl.SequenceEqual(restored.Acl) || original.Protected != restored.Protected ||
                        held.ReadHeldIdentity() != identity || Identity(restoration) != identity)
                        throw new Exception("Fixture DACL or directory identity did not restore exactly.");
                    var attrs = Attributes(stage, out var error);
                    using var reopened = OpenList(stage, out var openError);
                    if (attrs == InvalidAttributes || (attrs & 0x10) == 0 || reopened.IsInvalid)
                        throw new Exception($"Fixture access was not restored; attributes={error}, list={openError}");
                    using var verify = DownloadDirectoryObservation.OpenWithListAccessForTest(stage);
                    if (verify.Identity != identity || Directory.EnumerateFileSystemEntries(stage).Any())
                        throw new Exception("Fixture path identity or empty directory changed.");
                    Console.WriteLine("ACL_OBSERVATION: original_dacl_restored=true protection_restored=true identity_restored=true access_restored=true fixture_empty=true");
                }
                catch (Exception restoreError) { cleanupError = cleanupError is null ? restoreError : new AggregateException(cleanupError, restoreError); }
            }
            if (cleanupError is not null) throw new Exception("FAIL: worker or fixture ACL restoration failed", cleanupError);
        }
        Console.WriteLine("PASS: scoped same-user ACL observation and restoration; no CEF download was performed");
        // Leave only the test-created, restored objects for disposable runner teardown.
    }

    internal static async Task RunWorker(string[] args)
    {
        DownloadDirectoryObservationTests.RequireDisposable();
        if (IntPtr.Size != 8 || !Guid.TryParseExact(args[2], "N", out _)) throw new ArgumentException("Invalid fixture identity.");
        var temp = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? throw new IOException("Runner temp is unavailable."));
        var expectedRoot = Path.Combine(temp, "aegis-acl-observation-" + args[2]);
        var expectedStage = Path.Combine(expectedRoot, "owned-stage");
        if (!string.Equals(Path.GetFullPath(args[1]), expectedStage, StringComparison.OrdinalIgnoreCase) ||
            File.ReadAllText(Path.Combine(expectedRoot, "ownership-marker")) != args[2] ||
            (File.GetAttributes(expectedRoot) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(expectedStage) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Worker fixture path or marker is invalid.");
        var expected = new DirectoryIdentity(ulong.Parse(args[3], CultureInfo.InvariantCulture),
            ulong.Parse(args[4], CultureInfo.InvariantCulture), ulong.Parse(args[5], CultureInfo.InvariantCulture));
        using var security = OpenSecurityHandle(expectedStage);
        if (Identity(security) != expected) throw new IOException("Worker fixture identity differs from the parent.");
        if (Directory.EnumerateFileSystemEntries(expectedStage).Any()) throw new IOException("Worker fixture directory is not empty.");
        var original = ReadDacl(security);
        var currentUser = WindowsIdentity.GetCurrent().User ?? throw new IOException("Worker identity is unavailable.");
        var oldAcl = new RawAcl(original.Acl, 0);
        var changed = new RawAcl(oldAcl.Revision, oldAcl.Count + 1);
        // The worker holds WRITE_DAC before denying the same user. No inherited
        // ACE is applied to other objects: the test-owned directory is empty.
        changed.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessDenied, 0x01 | 0x02 | 0x20 | 0x80, currentUser, false, null));
        for (var i = 0; i < oldAcl.Count; i++) changed.InsertAce(i + 1, oldAcl[i]);
        var changedBytes = new byte[changed.BinaryLength];
        changed.GetBinaryForm(changedBytes, 0);
        try
        {
            WriteDacl(security, changedBytes);
            var observed = ReadDacl(security);
            if (!changedBytes.SequenceEqual(observed.Acl) || Identity(security) != expected)
                throw new Exception("Worker could not verify the test DACL and directory identity.");
            Console.WriteLine("ACL_APPLIED");
            Console.Out.Flush();
            // EOF also enters finally if the parent dies unexpectedly.
            _ = await Console.In.ReadLineAsync();
        }
        finally
        {
            WriteDacl(security, original.Acl);
            var restored = ReadDacl(security);
            if (!original.Acl.SequenceEqual(restored.Acl) || original.Protected != restored.Protected)
                throw new Exception("Worker could not restore the original fixture DACL.");
        }
    }

    private static SafeFileHandle OpenSecurityHandle(string path)
    {
        var result = CreateFileW(path, ReadControl | WriteDac | ReadAttributes, ShareAll, IntPtr.Zero, 3, DirectoryFlags, IntPtr.Zero);
        if (!result.IsInvalid) return result;
        var error = Marshal.GetLastWin32Error(); result.Dispose();
        throw new IOException("Could not open the owned fixture security handle.", new Win32Exception(error));
    }

    private static SafeFileHandle OpenList(string path, out int error)
    {
        var handle = CreateFileW(path, ListDirectory | ReadAttributes, ShareAll, IntPtr.Zero, 3, DirectoryFlags, IntPtr.Zero);
        error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        return handle;
    }

    private static uint Attributes(string path, out int error)
    {
        var attributes = GetFileAttributesW(path);
        error = attributes == InvalidAttributes ? Marshal.GetLastWin32Error() : 0;
        return attributes;
    }

    private static DirectoryIdentity Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 18, out var id, (uint)Marshal.SizeOf<FileIdInformation>()))
            throw new IOException("Could not inspect the fixture identity.", new Win32Exception(Marshal.GetLastWin32Error()));
        return new DirectoryIdentity(id.VolumeSerial, id.Low, id.High);
    }

    private static DaclSnapshot ReadDacl(SafeFileHandle handle)
    {
        var result = GetSecurityInfo(handle, 1, DaclSecurityInformation, out _, out _, out var dacl, out _, out var descriptor);
        if (result != 0) throw new IOException("Could not inspect the fixture DACL.", new Win32Exception((int)result));
        try
        {
            if (descriptor == IntPtr.Zero || dacl == IntPtr.Zero) throw new IOException("A null fixture DACL is not permitted.");
            var bytes = new byte[GetSecurityDescriptorLength(descriptor)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var security = new RawSecurityDescriptor(bytes, 0);
            var acl = security.DiscretionaryAcl ?? throw new IOException("A null fixture DACL is not permitted.");
            var aclBytes = new byte[acl.BinaryLength]; acl.GetBinaryForm(aclBytes, 0);
            return new DaclSnapshot(aclBytes, (security.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0);
        }
        finally { if (descriptor != IntPtr.Zero) LocalFree(descriptor); }
    }

    private static void WriteDacl(SafeFileHandle handle, byte[] acl)
    {
        if (acl.Length == 0) throw new IOException("Refusing to install a null fixture DACL.");
        var buffer = Marshal.AllocHGlobal(acl.Length);
        try
        {
            Marshal.Copy(acl, 0, buffer, acl.Length);
            var result = SetSecurityInfo(handle, 1, DaclSecurityInformation, IntPtr.Zero, IntPtr.Zero, buffer, IntPtr.Zero);
            if (result != 0) throw new IOException("Could not set the fixture DACL.", new Win32Exception((int)result));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private sealed record DaclSnapshot(byte[] Acl, bool Protected);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation { public ulong VolumeSerial; public ulong Low; public ulong High; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileIdInformation information, uint size);
    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint securityInfo, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern uint SetSecurityInfo(SafeFileHandle handle, int objectType, uint securityInfo, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
