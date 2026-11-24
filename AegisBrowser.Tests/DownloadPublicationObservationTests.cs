using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal static class DownloadPublicationObservationTests
{
    internal static void RunDisposable(bool native = false)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8 || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            Environment.GetEnvironmentVariable("AEGIS_DISPOSABLE_DOWNLOAD_TEST") != "1")
            throw new InvalidOperationException("This observation requires an acknowledged disposable GitHub-hosted Windows x64 runner.");
        var parent = Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? throw new IOException("Runner temporary directory is unavailable.");
        var root = Path.Combine(Path.GetFullPath(parent), "aegis-publication-" + Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(root, "stage"); var destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(stage); Directory.CreateDirectory(destination);
        var sourcePath = Path.Combine(stage, "fixture.bin");
        var collisionPath = Path.Combine(destination, "existing.bin");
        var finalPath = Path.Combine(destination, "published.bin");
        var bytes = Encoding.UTF8.GetBytes("Aegis publication fixture\n");
        var sentinel = Encoding.UTF8.GetBytes("Existing owned control\n");
        File.WriteAllBytes(sourcePath, bytes); File.WriteAllBytes(collisionPath, sentinel);
        using var source = Open(sourcePath, 0x80000000 | 0x00010000 | 0x80, 0x1, 0x00200000);
        using var directory = Open(destination, 0x2 | 0x80, 0x1 | 0x2, 0x02000000 | 0x00200000);
        var before = Identity(source);
        var sourceHash = Hash(source);
        if (sourceHash != Convert.ToHexString(SHA256.HashData(bytes))) throw new Exception("FAIL: staged fixture hash mismatch");
        if (RenameRelative(source, directory, "existing.bin", native, out var collisionError)) throw new Exception("FAIL: no-replace rename accepted existing destination");
        if (!File.ReadAllBytes(collisionPath).SequenceEqual(sentinel) || Identity(source) != before)
            throw new Exception("FAIL: collision changed an owned object");
        Console.WriteLine("PUBLICATION_OBSERVATION: api=" + (native ? "NtSetInformationFile" : "SetFileInformationByHandle") + " existing destination rejected status=" + unchecked((uint)collisionError).ToString("X8"));
        if (!RenameRelative(source, directory, "published.bin", native, out var renameError))
            throw new IOException("Handle-relative no-replace rename failed: " + unchecked((uint)renameError).ToString("X8"));
        using var published = Open(finalPath, 0x80000000 | 0x80, 0x1 | 0x2 | 0x4, 0x00200000);
        if (Identity(published) != before || Hash(published) != sourceHash || !File.ReadAllBytes(collisionPath).SequenceEqual(sentinel))
            throw new Exception("FAIL: published identity or hash changed");
        Console.WriteLine("PASS: " + (native ? "NtSetInformationFile" : "SetFileInformationByHandle") + " same-volume handle-relative no-replace rename retained identity and fixture hash");
        // Preserve the test-owned objects for disposal with the runner.
    }

    private static SafeFileHandle Open(string path, uint access, uint share, uint flags)
    {
        var handle = CreateFileW(path, access, share, IntPtr.Zero, 3, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose();
        throw new IOException("Could not open an owned publication fixture object.", new Win32Exception(error));
    }

    // FILE_RENAME_INFO on x64: BOOLEAN at 0, HANDLE at 8, byte length at 16,
    // UTF-16 name at 20. ReplaceIfExists remains false. The target is a single
    // test-owned name relative to the retained destination directory handle.
    private static bool RenameRelative(SafeFileHandle source, SafeFileHandle directory, string name, bool native, out int error)
    {
        if (name.Length == 0 || name.IndexOfAny(['\\', '/', ':']) >= 0 || name is "." or "..")
            throw new ArgumentException("A single relative filename is required.");
        var encoded = Encoding.Unicode.GetBytes(name);
        var size = 20 + encoded.Length;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteIntPtr(buffer, 8, directory.DangerousGetHandle());
            Marshal.WriteInt32(buffer, 16, encoded.Length);
            Marshal.Copy(encoded, 0, buffer + 20, encoded.Length);
            if (native)
            {
                var status = NtSetInformationFile(source, out _, buffer, (uint)size, 10);
                error = status;
                return status >= 0;
            }
            else
            {
                var success = SetFileInformationByHandle(source, 3, buffer, (uint)size);
                error = success ? 0 : Marshal.GetLastWin32Error();
                return success;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static FileIdentity Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 18, out var id, (uint)Marshal.SizeOf<FileIdentity>()))
            throw new IOException("Could not inspect the file identity.", new Win32Exception(Marshal.GetLastWin32Error()));
        return id;
    }
    private static string Hash(SafeFileHandle handle)
    {
        using var borrowed = new SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: false);
        using var stream = new FileStream(borrowed, FileAccess.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct FileIdentity(ulong VolumeSerial, ulong Low, ulong High);
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileIdentity information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, IntPtr information, uint size);
    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatusBlock ioStatus, IntPtr information, uint size, int informationClass);
}
