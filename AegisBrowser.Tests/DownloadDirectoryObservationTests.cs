using AegisBrowser.Core;

internal static class DownloadDirectoryObservationTests
{
    internal static void Run()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP: Windows directory observation unavailable"); return; }
        var count = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); count++; }
        var root = Path.Combine(Path.GetTempPath(), "aegis-directory-observation-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first"); var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        try
        {
            using var a = DownloadDirectoryObservation.OpenExistingLocalNtfs(first);
            using var b = DownloadDirectoryObservation.OpenExistingLocalNtfs(second);
            Check(a.Identity == a.ReadHeldIdentity(), "retained handle has the same identity");
            Check(a.Identity != b.Identity && a.Identity.VolumeSerial == b.Identity.VolumeSerial, "distinct directories on the same volume have distinct identities");
            Check(a.FinalPath.EndsWith("\\first", StringComparison.OrdinalIgnoreCase), "final path corresponds to the fixture directory");
            try { using var invalid = DownloadDirectoryObservation.OpenExistingLocalNtfs(@"\\server\share"); throw new Exception("accepted UNC"); }
            catch (IOException) { count++; }
            try { using var invalid = DownloadDirectoryObservation.OpenExistingLocalNtfs(Path.Combine(root, "missing")); throw new Exception("accepted missing directory"); }
            catch (IOException) { count++; }
        }
        finally { Directory.Delete(first); Directory.Delete(second); Directory.Delete(root); }
        Console.WriteLine($"PASS: {count} directory handle observation checks");
    }

    // This performs rename and delete attempts. Never run it on the everyday host.
    internal static void RunDisposableSharingObservation()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            Environment.GetEnvironmentVariable("AEGIS_DISPOSABLE_DOWNLOAD_TEST") != "1")
            throw new InvalidOperationException("This experiment requires the acknowledged disposable GitHub-hosted Windows runner.");
        var runnerTemp = Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? throw new IOException("Runner temporary directory is unavailable.");
        var root = Path.Combine(Path.GetFullPath(runnerTemp), "aegis-download-handles-" + Guid.NewGuid().ToString("N"));
        var ancestor = Path.Combine(root, "ancestor");
        var movedAncestor = Path.Combine(root, "moved-ancestor");
        var directory = Path.Combine(ancestor, "owned");
        var movedDirectory = Path.Combine(ancestor, "moved-owned");
        Directory.CreateDirectory(directory);
        try
        {
            DirectoryIdentity heldIdentity;
            using (var leaf = DownloadDirectoryObservation.OpenExistingLocalNtfs(directory))
            {
                var identity = leaf.Identity;
                heldIdentity = identity;
                try { Directory.Delete(directory); throw new Exception("FAIL: held directory deletion succeeded"); }
                catch (IOException ex) { Console.WriteLine("HANDLE_EXPERIMENT: leaf delete blocked hresult=" + ex.HResult); }
                try { Directory.Move(directory, movedDirectory); throw new Exception("FAIL: held directory rename succeeded"); }
                catch (IOException ex) { Console.WriteLine("HANDLE_EXPERIMENT: leaf rename blocked hresult=" + ex.HResult); }
                if (leaf.ReadHeldIdentity() != identity) throw new Exception("FAIL: held identity changed");

                var ancestorMoved = false;
                try
                {
                    Directory.Move(ancestor, movedAncestor);
                    ancestorMoved = true;
                    Console.WriteLine("HANDLE_EXPERIMENT: ancestor rename succeeded with leaf held");
                }
                catch (IOException ex) { Console.WriteLine("HANDLE_EXPERIMENT: ancestor rename blocked with leaf held hresult=" + ex.HResult); }
                finally { if (ancestorMoved) Directory.Move(movedAncestor, ancestor); }

                using var parent = DownloadDirectoryObservation.OpenExistingLocalNtfs(ancestor);
                try { Directory.Move(ancestor, movedAncestor); throw new Exception("FAIL: held ancestor rename succeeded"); }
                catch (IOException ex) { Console.WriteLine("HANDLE_EXPERIMENT: held ancestor rename blocked hresult=" + ex.HResult); }
                if (leaf.ReadHeldIdentity() != identity) throw new Exception("FAIL: leaf identity changed during observation");
            }
            using (var verification = DownloadDirectoryObservation.OpenExistingLocalNtfs(directory))
                if (verification.Identity != heldIdentity) throw new Exception("FAIL: directory path no longer identifies the fixture object");
            Directory.Move(directory, movedDirectory);
            Directory.Move(movedDirectory, directory);
            Directory.Delete(directory);
            Directory.Move(ancestor, movedAncestor);
            Directory.Move(movedAncestor, ancestor);
            Console.WriteLine("HANDLE_EXPERIMENT: same owned directory rename and delete controls succeeded after handle release");
            Console.WriteLine("PASS: scoped directory sharing experiment; CEF writes and ACLs not assessed");
        }
        finally
        {
            // Preserve all fixture objects for the disposable VM to discard.
            // A path alone is not sufficient evidence for cleanup.
        }
    }
}
