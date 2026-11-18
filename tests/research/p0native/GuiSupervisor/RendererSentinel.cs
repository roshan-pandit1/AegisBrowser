using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace P309g;

// Test-only resource; creation and the positive control run outside CEF. A
// denied attempt reported by a renderer is only a claim until authenticated.
internal sealed class RendererSentinel : IDisposable
{
    private const string PipePrefix = @"\\.\pipe\aegis-p309i-";
    private const string FilePrefix = "aegis-p0-sentinel-";
    private const uint GenericRead = 0x80000000, ShareReadWriteDelete = 7, OpenExisting = 3;
    private const uint DaclInformation = 4, ProtectedDacl = 0x80000000;
    private readonly FileStream retained;
    private bool disposed;
    internal string Pathname { get; }
    internal string Identity { get; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Information
    {
        internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh,
            WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr attributes, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out Information info);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl,
        uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetFileSecurityW(string path, uint information, IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr value);

    private RendererSentinel(string path, FileStream retained, string identity)
    { Pathname = path; this.retained = retained; Identity = identity; }

    private static bool IsNonce(string value) => value.Length == 32 &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static string NameForPipe(string pipe)
    {
        if (!pipe.StartsWith(PipePrefix, StringComparison.Ordinal) || !IsNonce(pipe[PipePrefix.Length..]))
            throw new InvalidDataException("SENTINEL_PIPE_NAME_INVALID");
        return FilePrefix + pipe[PipePrefix.Length..] + ".dat";
    }

    internal static string ValidatePath(string path)
    {
        string temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        string full = System.IO.Path.GetFullPath(path);
        string name = System.IO.Path.GetFileName(full);
        if (!System.IO.Path.IsPathFullyQualified(path) ||
            !string.Equals(System.IO.Path.GetDirectoryName(full)?.TrimEnd('\\', '/'), temp.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(FilePrefix, StringComparison.Ordinal) || !name.EndsWith(".dat", StringComparison.Ordinal) ||
            !IsNonce(name[FilePrefix.Length..^4]) || (File.GetAttributes(temp) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("SENTINEL_PATH_INVALID");
        return full;
    }

    private static string IdentityFor(SafeFileHandle file)
    {
        if (!GetFileInformationByHandle(file, out var info) || info.Links != 1 ||
            (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("SENTINEL_IDENTITY_UNAVAILABLE");
        return $"{info.Volume:X8}:{info.IndexHigh:X8}:{info.IndexLow:X8}";
    }

    // The caller must compare this identity with the held controller handle;
    // a basename match alone is not a positive control.
    internal static string OpenIdentity(string path)
    {
        string full = ValidatePath(path);
        using var handle = CreateFileW(full, GenericRead, ShareReadWriteDelete, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SENTINEL_READ_FAILED");
        return IdentityFor(handle);
    }

    internal static RendererSentinel Create(string pipe)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        string path = ValidatePath(System.IO.Path.Combine(temp, NameForPipe(pipe)));
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            stream.WriteByte(0x41); stream.Flush(true);
            string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("SENTINEL_USER_SID_UNAVAILABLE");
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;;GA;;;" + sid + ")", 1, out var descriptor, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!SetFileSecurityW(path, DaclInformation | ProtectedDacl, descriptor))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { LocalFree(descriptor); }
            return new RendererSentinel(path, stream, IdentityFor(stream.SafeFileHandle));
        }
        catch { stream.Dispose(); File.Delete(path); throw; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        retained.Dispose();
        // Exactly the file created above; no recursive deletion or profile data.
        File.Delete(Pathname);
    }
}
