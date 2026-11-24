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
        RequireDisposable();
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

    internal static void RunDisposableDeleteDiagnostic()
    {
        RequireDisposable();
        var runnerTemp = Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? throw new IOException("Runner temporary directory is unavailable.");
        var root = Path.Combine(Path.GetFullPath(runnerTemp), "aegis-delete-observation-" + Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(root, "owned-stage");
        var control = Path.Combine(root, "unheld-control");
        Directory.CreateDirectory(stage); Directory.CreateDirectory(control);
        Directory.Delete(control);
        if (Directory.Exists(control)) throw new Exception("FAIL: unheld delete control did not remove the owned fixture directory");
        Console.WriteLine("DELETE_OBSERVATION: unheld delete control succeeded");
        using var held = DownloadDirectoryObservation.OpenExistingLocalNtfs(stage);
        var id = held.Identity;
        var returned = false;
        try { Directory.Delete(stage); returned = true; }
        catch (IOException ex) { Console.WriteLine("DELETE_OBSERVATION: delete returned IOException hresult=" + ex.HResult); }
        catch (UnauthorizedAccessException ex) { Console.WriteLine("DELETE_OBSERVATION: delete returned access denied hresult=" + ex.HResult); }
        Console.WriteLine("DELETE_OBSERVATION: delete_returned=" + returned + " path_exists=" + Directory.Exists(stage) +
            " delete_pending=" + held.ReadDeletePending() + " retained_identity=" + (id == held.ReadHeldIdentity()));
        if (returned)
        {
            try
            {
                Directory.CreateDirectory(stage);
                using var reopened = DownloadDirectoryObservation.OpenExistingLocalNtfs(stage);
                Console.WriteLine("DELETE_OBSERVATION: path_recreated=true same_identity=" + (id == reopened.Identity));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Console.WriteLine("DELETE_OBSERVATION: path_recreated=false hresult=" + ex.HResult); }
        }
        // Preserve all test-owned objects until the disposable runner is discarded.
    }

    internal static void RunDisposableListAccessDiagnostic()
    {
        RequireDisposable();
        var runnerTemp = Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? throw new IOException("Runner temporary directory is unavailable.");
        var root = Path.Combine(Path.GetFullPath(runnerTemp), "aegis-list-access-" + Guid.NewGuid().ToString("N"));
        var parent = Path.Combine(root, "parent"); var movedParent = Path.Combine(root, "moved-parent");
        var leaf = Path.Combine(parent, "leaf"); var movedLeaf = Path.Combine(parent, "moved-leaf");
        Directory.CreateDirectory(leaf);
        DirectoryIdentity identity;
        using (var heldParent = DownloadDirectoryObservation.OpenWithListAccessForTest(parent))
        using (var heldLeaf = DownloadDirectoryObservation.OpenWithListAccessForTest(leaf))
        {
            identity = heldLeaf.Identity;
            static void ExpectBlocked(Action operation, string label)
            {
                try { operation(); throw new Exception("FAIL: " + label + " succeeded while handle held"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Console.WriteLine("LIST_ACCESS_OBSERVATION: " + label + " blocked hresult=" + ex.HResult); }
            }
            ExpectBlocked(() => Directory.Delete(leaf), "leaf delete");
            ExpectBlocked(() => Directory.Move(leaf, movedLeaf), "leaf rename");
            ExpectBlocked(() => Directory.Move(parent, movedParent), "parent rename");
            if (heldLeaf.ReadHeldIdentity() != identity) throw new Exception("FAIL: held leaf identity changed");
        }
        using (var reopened = DownloadDirectoryObservation.OpenWithListAccessForTest(leaf))
            if (reopened.Identity != identity) throw new Exception("FAIL: fixture path changed identity");
        Directory.Move(leaf, movedLeaf); Directory.Move(movedLeaf, leaf); Directory.Delete(leaf);
        Directory.Move(parent, movedParent); Directory.Move(movedParent, parent);
        Console.WriteLine("LIST_ACCESS_OBSERVATION: rename and delete controls succeeded after release");
        Console.WriteLine("PASS: scoped list-access directory sharing experiment; CEF writes and ACLs not assessed");
    }

    private static void RequireDisposable()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
            Environment.GetEnvironmentVariable("AEGIS_DISPOSABLE_DOWNLOAD_TEST") != "1")
            throw new InvalidOperationException("This experiment requires the acknowledged disposable GitHub-hosted Windows runner.");
    }
}
