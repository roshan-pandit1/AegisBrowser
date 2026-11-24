using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AegisBrowser.Core;

/// <summary>
/// Research-only observation of one existing directory handle. This does not
/// validate ancestors, DACLs, a staging area, or authorize a CEF download.
/// </summary>
public sealed class DownloadDirectoryObservation : IDisposable
{
    private const uint FileReadAttributes = 0x80;
    private const uint ShareReadWrite = 0x1 | 0x2; // Deliberately omit FILE_SHARE_DELETE.
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint DirectoryAttribute = 0x10;
    private const uint ReparsePointAttribute = 0x400;
    private const uint OfflineAttribute = 0x1000;
    private const uint RecallOnOpenAttribute = 0x00040000;
    private const uint RecallOnDataAccessAttribute = 0x00400000;
    private readonly SafeFileHandle handle;

    private DownloadDirectoryObservation(SafeFileHandle handle, string path, DirectoryIdentity identity)
    {
        this.handle = handle;
        FinalPath = path;
        Identity = identity;
    }

    public string FinalPath { get; }
    public DirectoryIdentity Identity { get; }

    public static DownloadDirectoryObservation OpenExistingLocalNtfs(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows is required.");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new IOException("A local DOS directory path is required.");
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new IOException("Directory root is unavailable.");
        if (GetDriveTypeW(root) != 3) throw new IOException("The volume is not a fixed local drive.");
        var directory = CreateFileW(fullPath, FileReadAttributes, ShareReadWrite, IntPtr.Zero, OpenExisting,
            BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (directory.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            directory.Dispose();
            throw new IOException("Could not open the directory for observation.", new Win32Exception(error));
        }
        try
        {
            var tag = ReadAttributes(directory);
            if ((tag.Attributes & DirectoryAttribute) == 0 ||
                (tag.Attributes & (ReparsePointAttribute | OfflineAttribute | RecallOnOpenAttribute | RecallOnDataAccessAttribute)) != 0)
                throw new IOException("Directory is a reparse point, placeholder, offline or not a directory.");
            var filesystem = new StringBuilder(64);
            if (!GetVolumeInformationByHandleW(directory, null, 0, out _, out _, out _, filesystem, (uint)filesystem.Capacity))
                throw NativeError("Could not inspect the volume.");
            if (!string.Equals(filesystem.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Only local NTFS is supported for this observation.");
            var final = new StringBuilder(1024);
            var length = GetFinalPathNameByHandleW(directory, final, (uint)final.Capacity, 0);
            if (length == 0 || length >= final.Capacity) throw NativeError("Could not obtain the final directory path.");
            var finalPath = final.ToString();
            if (!finalPath.StartsWith("\\\\?\\", StringComparison.Ordinal) || finalPath.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
                throw new IOException("The final path is not a local DOS path.");
            return new DownloadDirectoryObservation(directory, finalPath, ReadIdentity(directory));
        }
        catch { directory.Dispose(); throw; }
    }

    public DirectoryIdentity ReadHeldIdentity()
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        return ReadIdentity(handle);
    }

    // Observation only: a pending-delete result does not establish whether a
    // particular path can be reopened, replaced, or used by CEF.
    public bool ReadDeletePending()
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        if (!GetFileStandardInformation(handle, 1, out var information, (uint)Marshal.SizeOf<FileStandardInformation>()))
            throw NativeError("Could not inspect directory deletion state.");
        return information.DeletePending != 0;
    }

    public void Dispose() => handle.Dispose();

    private static DirectoryIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileIdInformation(handle, 18, out var information, (uint)Marshal.SizeOf<FileIdInformation>()))
            throw NativeError("Could not inspect directory identity.");
        return new DirectoryIdentity(information.VolumeSerial, information.FileIdLow, information.FileIdHigh);
    }

    private static FileAttributeTagInformation ReadAttributes(SafeFileHandle handle)
    {
        if (!GetFileAttributeTagInformation(handle, 9, out var information, (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
            throw NativeError("Could not inspect directory attributes.");
        return information;
    }

    private static IOException NativeError(string message) => new(message, new Win32Exception(Marshal.GetLastWin32Error()));

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation { public ulong VolumeSerial; public ulong FileIdLow; public ulong FileIdHigh; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation { public uint Attributes; public uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInformation
    {
        public long AllocationSize; public long EndOfFile; public uint NumberOfLinks; public byte DeletePending; public byte Directory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileIdInformation(SafeFileHandle file, int informationClass, out FileIdInformation information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileAttributeTagInformation(SafeFileHandle file, int informationClass, out FileAttributeTagInformation information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileStandardInformation(SafeFileHandle file, int informationClass, out FileStandardInformation information, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(SafeFileHandle file, StringBuilder? name, uint nameSize,
        out uint serial, out uint maximumComponentLength, out uint flags, StringBuilder filesystem, uint filesystemSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);
}

public readonly record struct DirectoryIdentity(ulong VolumeSerial, ulong FileIdLow, ulong FileIdHigh);
