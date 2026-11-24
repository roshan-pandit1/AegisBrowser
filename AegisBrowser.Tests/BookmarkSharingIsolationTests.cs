using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Observes test-owned directories only. This is not a storage authorization test.
internal static class BookmarkSharingIsolationTests
{
    private const uint ReadAttributes = 0x80, ListDirectory = 0x1;
    private const uint ReadWrite = 0x3, ReadWriteDelete = 0x7;
    private const uint BackupAndReparse = 0x02000000 | 0x00200000;

    internal static void Run()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP: Windows sharing isolation unavailable"); return; }
        var root = Path.Combine(Path.GetTempPath(), "aegis-bookmark-share-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var count = 0;
        try
        {
            foreach (var useLock in new[] { false, true })
            foreach (var mode in new[] { "none", "metadata", "list", "metadata-delete-share", "list-delete-share" })
            foreach (var target in new[] { "profile", "ancestor" })
            foreach (var guard in target == "ancestor" ? new[] { "profile", "ancestor" } : new[] { "profile" })
            {
                var owned = Path.Combine(root, Guid.NewGuid().ToString("N"));
                var ancestor = Path.Combine(owned, "ancestor");
                var profile = Path.Combine(ancestor, "profile");
                Directory.CreateDirectory(profile);
                var targetPath = target == "profile" ? profile : ancestor;
                var movedPath = targetPath + "-moved";
                FileStream? lockFile = null;
                SafeFileHandle? blocker = null;
                SafeFileHandle? observer = null;
                var moved = false;
                try
                {
                    if (useLock) lockFile = new FileStream(Path.Combine(profile, ".profile.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    if (mode != "none")
                    {
                        var access = ReadAttributes | (mode.StartsWith("list", StringComparison.Ordinal) ? ListDirectory : 0);
                        var share = mode.EndsWith("delete-share", StringComparison.Ordinal) ? ReadWriteDelete : ReadWrite;
                        blocker = Open(guard == "profile" ? profile : ancestor, access, share);
                    }
                    observer = Open(targetPath, ReadAttributes, ReadWriteDelete);
                    var before = Identity(observer);
                    var beforePath = FinalPath(observer);
                    var outcome = "renamed";
                    try { Directory.Move(targetPath, movedPath); moved = true; }
                    catch (IOException ex) { outcome = "io-" + (ex.HResult & 0xffff); }
                    catch (UnauthorizedAccessException ex) { outcome = "access-" + (ex.HResult & 0xffff); }
                    var after = Identity(observer);
                    var afterPath = FinalPath(observer);
                    if (before != after) throw new Exception("A held test-directory handle changed identity.");
                    if (mode == "none" && !useLock && !moved) throw new Exception("The unheld rename control failed: " + outcome);
                    Console.WriteLine($"BOOKMARK_SHARING: lock={useLock} mode={mode} target={target} guard={guard} outcome={outcome} volume={before.Volume:X} id={before.High:X16}{before.Low:X16} before={beforePath} after={afterPath}");
                    count++;
                }
                finally
                {
                    observer?.Dispose(); blocker?.Dispose(); lockFile?.Dispose();
                    if (moved) Directory.Move(movedPath, targetPath);
                }
            }
            foreach (var useLock in new[] { false, true })
            foreach (var mode in new[] { "none", "metadata", "list", "metadata-delete-share", "list-delete-share" })
            {
                var owned = Path.Combine(root, Guid.NewGuid().ToString("N"));
                var profile = Path.Combine(owned, "profile");
                Directory.CreateDirectory(profile);
                FileStream? lockFile = null;
                SafeFileHandle? blocker = null;
                SafeFileHandle? observer = null;
                try
                {
                    if (useLock) lockFile = new FileStream(Path.Combine(profile, ".profile.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    if (mode != "none")
                    {
                        var access = ReadAttributes | (mode.StartsWith("list", StringComparison.Ordinal) ? ListDirectory : 0);
                        blocker = Open(profile, access, mode.EndsWith("delete-share", StringComparison.Ordinal) ? ReadWriteDelete : ReadWrite);
                    }
                    observer = Open(profile, ReadAttributes, ReadWriteDelete);
                    var original = Identity(observer);
                    var originalPath = FinalPath(observer);
                    var removed = false;
                    var result = "removed";
                    try { Directory.Delete(profile); removed = true; }
                    catch (IOException ex) { result = "io-" + (ex.HResult & 0xffff); }
                    catch (UnauthorizedAccessException ex) { result = "access-" + (ex.HResult & 0xffff); }
                    if (mode == "none" && !useLock && !removed) throw new Exception("The unheld delete control failed: " + result);
                    if (removed)
                    {
                        try
                        {
                            Directory.CreateDirectory(profile);
                            using var replacement = Open(profile, ReadAttributes, ReadWriteDelete);
                            var next = Identity(replacement);
                            result += next != original ? "/recreated-different-id" : "/recreated-same-id";
                            Console.WriteLine($"BOOKMARK_REPLACEMENT: volume={next.Volume:X} id={next.High:X16}{next.Low:X16} final={FinalPath(replacement)}");
                        }
                        catch (IOException ex) { result += "/recreate-io-" + (ex.HResult & 0xffff); }
                        catch (UnauthorizedAccessException ex) { result += "/recreate-access-" + (ex.HResult & 0xffff); }
                    }
                    if (Identity(observer) != original) throw new Exception("The removed test-directory handle changed identity.");
                    Console.WriteLine($"BOOKMARK_REMOVAL: lock={useLock} mode={mode} outcome={result} volume={original.Volume:X} id={original.High:X16}{original.Low:X16} before={originalPath}");
                    count++;
                }
                finally { observer?.Dispose(); blocker?.Dispose(); lockFile?.Dispose(); }
            }
        }
        finally
        {
            // root is a unique test-created directory and no external path is traversed.
            Directory.Delete(root, recursive: true);
            if (Directory.Exists(root)) throw new IOException("The test-owned sharing fixture was not removed.");
        }
        Console.WriteLine($"PASS: {count} bookmark directory sharing observations (not confinement qualification)");
    }

    private readonly record struct FileIdentity(ulong Volume, ulong Low, ulong High);
    [StructLayout(LayoutKind.Sequential)] private struct IdInfo { public ulong Volume, Low, High; }
    private static SafeFileHandle Open(string path, uint access, uint share)
    {
        var result = CreateFileW(path, access, share, IntPtr.Zero, 3, BackupAndReparse, IntPtr.Zero);
        if (!result.IsInvalid) return result;
        var error = new Win32Exception(Marshal.GetLastWin32Error()); result.Dispose();
        throw new IOException("Could not open a test directory handle.", error);
    }
    private static FileIdentity Identity(SafeFileHandle handle)
    {
        if (!GetId(handle, 18, out var id, (uint)Marshal.SizeOf<IdInfo>())) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(id.Volume, id.Low, id.High);
    }
    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        return buffer.ToString();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetId(SafeFileHandle handle, int kind, out IdInfo info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
