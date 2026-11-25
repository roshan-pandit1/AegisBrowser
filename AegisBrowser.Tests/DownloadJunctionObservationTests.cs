using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AegisBrowser.Core;
using Microsoft.Win32.SafeHandles;

// Independent OS fixture only: no CEF is started and no user file is touched.
[SupportedOSPlatform("windows")]
internal static class DownloadJunctionObservationTests
{
    private const uint MountPointTag = 0xA0000003;
    private const uint SetReparsePoint = 0x000900A4;
    private const uint DeleteReparsePoint = 0x000900AC;
    private const uint ReparseAttribute = 0x400;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private static readonly TimeSpan EventDeadline = TimeSpan.FromSeconds(25);

    internal static async Task RunDisposable()
    {
        DownloadDirectoryObservationTests.RequireDisposable();
        if (IntPtr.Size != 8) throw new InvalidOperationException("The observation requires Windows x64.");
        var temp = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? throw new IOException("Runner temp is unavailable."));
        var nonce = Guid.NewGuid().ToString("N");
        var root = Path.Combine(temp, "aegis-junction-observation-" + nonce);
        CreateOwnedDirectory(root);
        using var heldRoot = DownloadDirectoryObservation.OpenWithListAccessForTest(root);
        var target = Path.Combine(root, "target");
        var guarded = Path.Combine(root, "protected-stage");
        var empty = Path.Combine(root, "empty-stage");
        CreateOwnedDirectory(target); CreateOwnedDirectory(guarded); CreateOwnedDirectory(empty);
        using (var file = new FileStream(Path.Combine(root, "ownership-marker"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(file)) writer.Write(nonce);
        using var heldTarget = DownloadDirectoryObservation.OpenWithListAccessForTest(target);

        // This marker is created and held without delete or write sharing. It
        // is a fixture candidate, not the production ownership mechanism.
        var markerPath = Path.Combine(guarded, "ownership-marker");
        using (var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
        {
            marker.WriteByte(1); marker.Flush(flushToDisk: true);
            var blocked = false;
            try { File.Delete(markerPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { blocked = true; }
            if (!blocked) throw new Exception("FAIL: deletion of the held test marker succeeded");
            Console.WriteLine("JUNCTION_OBSERVATION: held_marker_delete_blocked=true");
            await RunCase(guarded, target, nonce, heldTarget.Identity, expectRedirect: false);
        }
        await RunCase(empty, target, nonce, heldTarget.Identity, expectRedirect: true);
        Console.WriteLine("PASS: scoped held-marker and empty-directory junction observations; no CEF download was performed");
        // The original directories and fixture files remain for runner disposal.
    }

    private static async Task RunCase(string stage, string target, string nonce, DirectoryIdentity targetIdentity, bool expectRedirect)
    {
        using var held = DownloadDirectoryObservation.OpenWithListAccessForTest(stage);
        using var repair = OpenDirectory(stage, 0x40000000 | 0x80, follow: false);
        if (Identity(repair) != held.Identity || ReadTag(repair).Tag != 0)
            throw new IOException("Fixture directory identity or initial tag is invalid.");
        var executable = Environment.ProcessPath ?? throw new IOException("Test executable is unavailable.");
        if (!string.Equals(Path.GetFileName(executable), "AegisBrowser.Tests.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The worker must be launched from the built test executable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true
        };
        foreach (var arg in new[] { "--download-junction-worker", stage, target, nonce, Format(held.Identity), Format(targetIdentity) })
            start.ArgumentList.Add(arg);
        using var worker = new Process { StartInfo = start };
        if (!worker.Start()) throw new IOException("Could not launch the junction worker.");
        Exception? cleanupError = null;
        try
        {
            var signal = await worker.StandardOutput.ReadLineAsync().WaitAsync(EventDeadline);
            var applied = signal == "JUNCTION_APPLIED";
            var rejected = signal?.StartsWith("JUNCTION_REJECTED:", StringComparison.Ordinal) == true;
            if (!applied && !rejected) throw new Exception("FAIL: junction worker did not report its OS result");
            var label = expectRedirect ? "empty" : "protected_marker";
            Console.WriteLine("JUNCTION_OBSERVATION: case=" + label + " result=" + signal);
            if (applied)
            {
                var tag = ReadTag(repair);
                if ((tag.Attributes & ReparseAttribute) == 0 || tag.Tag != MountPointTag || held.ReadHeldIdentity() != held.Identity)
                    throw new Exception("FAIL: retained directory did not expose the expected junction tag and identity");
                using var followed = OpenDirectory(stage, 0x01 | 0x80, follow: true);
                if (Identity(followed) != targetIdentity) throw new Exception("FAIL: junction did not resolve to the owned target identity");
                Console.WriteLine("JUNCTION_OBSERVATION: case=" + label + " held_identity_unchanged=true followed_identity_is_target=true");
                if (expectRedirect)
                {
                    var proof = Encoding.ASCII.GetBytes("owned junction observation");
                    using (var created = new FileStream(Path.Combine(stage, "proof.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                        created.Write(proof);
                    using var throughStage = new FileStream(Path.Combine(stage, "proof.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var throughTarget = new FileStream(Path.Combine(target, "proof.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (Identity(throughStage.SafeFileHandle) != Identity(throughTarget.SafeFileHandle) ||
                        !File.ReadAllBytes(Path.Combine(target, "proof.bin")).SequenceEqual(proof))
                        throw new Exception("FAIL: redirected fixture file identity or bytes differ");
                    Console.WriteLine("JUNCTION_OBSERVATION: empty_path_write_reached_owned_target=true file_identity_matches=true");
                }
            }
            if (applied != expectRedirect)
                throw new Exception(expectRedirect ? "FAIL: empty-directory junction control was rejected" : "FAIL: held marker did not prevent junction substitution");
        }
        finally
        {
            try
            {
                if (!worker.HasExited)
                {
                    await worker.StandardInput.WriteLineAsync("RESTORE");
                    await worker.StandardInput.FlushAsync();
                }
                await worker.WaitForExitAsync().WaitAsync(EventDeadline);
                if (worker.ExitCode != 0) cleanupError = new Exception("Junction worker failed during cleanup.");
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
                    var tag = ReadTag(repair);
                    if ((tag.Attributes & ReparseAttribute) != 0)
                    {
                        if (tag.Tag != MountPointTag || Identity(repair) != held.Identity)
                            throw new IOException("Refusing to remove an unknown reparse point.");
                        RemoveMountPoint(repair);
                    }
                    if ((ReadTag(repair).Attributes & ReparseAttribute) != 0 || held.ReadHeldIdentity() != held.Identity)
                        throw new Exception("Fixture directory was not restored.");
                    using var reopened = DownloadDirectoryObservation.OpenWithListAccessForTest(stage);
                    if (reopened.Identity != held.Identity) throw new Exception("Fixture path identity changed.");
                    Console.WriteLine("JUNCTION_OBSERVATION: case=" + (expectRedirect ? "empty" : "protected_marker") + " original_directory_restored=true identity_matches=true");
                }
                catch (Exception restoreError) { cleanupError = cleanupError is null ? restoreError : new AggregateException(cleanupError, restoreError); }
            }
            if (cleanupError is not null) throw new Exception("FAIL: junction fixture cleanup failed", cleanupError);
        }
    }

    internal static async Task RunWorker(string[] args)
    {
        DownloadDirectoryObservationTests.RequireDisposable();
        if (IntPtr.Size != 8 || !Guid.TryParseExact(args[3], "N", out _)) throw new ArgumentException("Invalid fixture identity.");
        var temp = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? throw new IOException("Runner temp is unavailable."));
        var root = Path.Combine(temp, "aegis-junction-observation-" + args[3]);
        var stage = Path.GetFullPath(args[1]);
        var target = Path.GetFullPath(args[2]);
        if ((!string.Equals(stage, Path.Combine(root, "protected-stage"), StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(stage, Path.Combine(root, "empty-stage"), StringComparison.OrdinalIgnoreCase)) ||
            !string.Equals(target, Path.Combine(root, "target"), StringComparison.OrdinalIgnoreCase) ||
            File.ReadAllText(Path.Combine(root, "ownership-marker")) != args[3] ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Worker fixture paths or marker are invalid.");
        using var source = OpenDirectory(stage, 0x40000000 | 0x80, follow: false);
        using var destination = OpenDirectory(target, 0x01 | 0x80, follow: false);
        if (Identity(source) != Parse(args[4]) || Identity(destination) != Parse(args[5]) ||
            (ReadTag(source).Attributes & ReparseAttribute) != 0 || (ReadTag(destination).Attributes & ReparseAttribute) != 0)
            throw new IOException("Worker fixture identities or attributes differ from the parent.");
        var applied = false;
        try
        {
            var buffer = MakeMountPoint(target);
            applied = DeviceIoControl(source, SetReparsePoint, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero);
            if (!applied)
            {
                Console.WriteLine("JUNCTION_REJECTED:" + Marshal.GetLastWin32Error());
                Console.Out.Flush();
                return;
            }
            if (ReadTag(source).Tag != MountPointTag) throw new IOException("Worker could not verify the junction tag.");
            Console.WriteLine("JUNCTION_APPLIED"); Console.Out.Flush();
            _ = await Console.In.ReadLineAsync();
        }
        finally
        {
            if (applied) RemoveMountPoint(source);
        }
    }

    private static byte[] MakeMountPoint(string target)
    {
        if (!Path.IsPathFullyQualified(target) || target.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("The fixture target must be a local path.");
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var display = Encoding.Unicode.GetBytes(target);
        var dataLength = checked((ushort)(8 + substitute.Length + 2 + display.Length + 2));
        var buffer = new byte[8 + dataLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), MountPointTag);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), dataLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)display.Length));
        substitute.CopyTo(buffer, 16);
        display.CopyTo(buffer, 16 + substitute.Length + 2);
        return buffer;
    }

    private static void RemoveMountPoint(SafeFileHandle handle)
    {
        var buffer = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, MountPointTag);
        if (!DeviceIoControl(handle, DeleteReparsePoint, buffer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException("Could not remove the owned fixture junction.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    private static string Format(DirectoryIdentity value) => FormattableString.Invariant($"{value.VolumeSerial}:{value.FileIdLow}:{value.FileIdHigh}");
    private static DirectoryIdentity Parse(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 3) throw new ArgumentException("Invalid fixture file identity.");
        return new DirectoryIdentity(ulong.Parse(parts[0], CultureInfo.InvariantCulture), ulong.Parse(parts[1], CultureInfo.InvariantCulture), ulong.Parse(parts[2], CultureInfo.InvariantCulture));
    }
    private static void CreateOwnedDirectory(string path)
    {
        if (!CreateDirectoryW(path, IntPtr.Zero)) throw new IOException("Could not create a new fixture directory.", new Win32Exception(Marshal.GetLastWin32Error()));
    }
    private static SafeFileHandle OpenDirectory(string path, uint access, bool follow)
    {
        var handle = CreateFileW(path, access, 0x01 | 0x02 | 0x04, IntPtr.Zero, 3, BackupSemantics | (follow ? 0 : OpenReparsePoint), IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose();
        throw new IOException("Could not open a fixture directory.", new Win32Exception(error));
    }
    private static DirectoryIdentity Identity(SafeFileHandle handle)
    {
        if (!GetFileIdInformation(handle, 18, out var id, (uint)Marshal.SizeOf<FileIdInformation>()))
            throw new IOException("Could not inspect fixture identity.", new Win32Exception(Marshal.GetLastWin32Error()));
        return new DirectoryIdentity(id.VolumeSerial, id.Low, id.High);
    }
    private static AttributeTag ReadTag(SafeFileHandle handle)
    {
        if (!GetFileAttributeTagInformation(handle, 9, out var tag, (uint)Marshal.SizeOf<AttributeTag>()))
            throw new IOException("Could not inspect fixture reparse tag.", new Win32Exception(Marshal.GetLastWin32Error()));
        return tag;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation { public ulong VolumeSerial; public ulong Low; public ulong High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint Tag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileIdInformation(SafeFileHandle handle, int informationClass, out FileIdInformation info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileAttributeTagInformation(SafeFileHandle handle, int informationClass, out AttributeTag info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, uint inputSize, IntPtr output, uint outputSize, out uint bytesReturned, IntPtr overlapped);
}
