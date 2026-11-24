using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AegisBrowser.Core;

// Experimental observation only. This does not validate ancestors, bind the
// directory to the lease's original identity, or authorize bookmark I/O.
internal sealed class BookmarkDirectoryObservation : IDisposable
{
    private const uint ListAndAttributes = 0x1 | 0x80;
    private const uint ShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint UnsupportedAttributes = 0x400 | 0x1000 | 0x40000 | 0x400000;
    private readonly SafeFileHandle handle;

    private BookmarkDirectoryObservation(SafeFileHandle handle, Identity identity, string finalPath)
        => (this.handle, DirectoryIdentity, FinalPath) = (handle, identity, finalPath);

    internal readonly record struct Identity(ulong VolumeSerial, ulong Low, ulong High);
    internal Identity DirectoryIdentity { get; }
    internal string FinalPath { get; }

    internal static BookmarkDirectoryObservation Open(string path, ProfileLease lease)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ArgumentNullException.ThrowIfNull(lease);
        BookmarkDirectoryObservation? result = null;
        if (!lease.TryInvokeWhileHeld(() =>
        {
            if (!lease.IsHeldFor(path)) throw new IOException("A matching profile lease is required.");
            var root = Path.GetPathRoot(path);
            if (root is null || root.StartsWith(@"\\", StringComparison.Ordinal) || GetDriveTypeW(root) != 3)
                throw new IOException("Only fixed local volumes are supported.");
            var candidate = CreateFileW(path, ListAndAttributes, ShareReadWrite, IntPtr.Zero, OpenExisting,
                BackupSemantics | OpenReparsePoint, IntPtr.Zero);
            if (candidate.IsInvalid) { var error = Error("Could not open the profile directory."); candidate.Dispose(); throw error; }
            try
            {
                if (!GetTag(candidate, 9, out var tag, (uint)Marshal.SizeOf<TagInfo>())) throw Error("Could not inspect directory attributes.");
                if ((tag.Attributes & 0x10) == 0 || (tag.Attributes & UnsupportedAttributes) != 0)
                    throw new IOException("The directory is a reparse point, placeholder, offline or not a directory.");
                var fileSystem = new StringBuilder(64);
                if (!GetVolumeInformationByHandleW(candidate, null, 0, out _, out _, out _, fileSystem, (uint)fileSystem.Capacity))
                    throw Error("Could not inspect the volume.");
                if (!string.Equals(fileSystem.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Only NTFS is supported.");
                var final = new StringBuilder(32768);
                var length = GetFinalPathNameByHandleW(candidate, final, (uint)final.Capacity, 0);
                if (length == 0 || length >= final.Capacity) throw Error("Could not inspect the final directory path.");
                var finalPath = final.ToString();
                if (!finalPath.StartsWith(@"\\?\", StringComparison.Ordinal) || finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The opened directory is not on a local DOS volume.");
                result = new BookmarkDirectoryObservation(candidate, ReadIdentity(candidate), finalPath);
            }
            catch { candidate.Dispose(); throw; }
        })) throw new IOException("A live profile lease is required.");
        return result!;
    }

    internal Identity ReadHeldIdentity() => ReadIdentity(handle);
    public void Dispose() => handle.Dispose();

    private static Identity ReadIdentity(SafeFileHandle file)
    {
        if (!GetId(file, 18, out var id, (uint)Marshal.SizeOf<IdInfo>())) throw Error("Could not inspect directory identity.");
        return new(id.VolumeSerial, id.Low, id.High);
    }
    private static IOException Error(string message) => new(message, new Win32Exception(Marshal.GetLastWin32Error()));

    [StructLayout(LayoutKind.Sequential)] private struct IdInfo { public ulong VolumeSerial, Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct TagInfo { public uint Attributes, ReparseTag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetId(SafeFileHandle file, int kind, out IdInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTag(SafeFileHandle file, int kind, out TagInfo info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumeInformationByHandleW(SafeFileHandle file, StringBuilder? name,
        uint nameSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder fileSystem, uint fileSystemSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);
}
