using System.Diagnostics;
using System.Reflection;
using AegisBrowser.Core;

internal static class ProfileDashboardTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL dashboard: " + name); count++; }
        void Reject(Action action, string name)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { count++; return; }
            throw new Exception("FAIL dashboard (accepted): " + name);
        }
        var root = Path.Combine(Path.GetTempPath(), "aegis-dashboard-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new ProfileRepository(root);
            var fakeProbe = new FakeProbe();
            var fakeStarter = new FakeStarter();
            using var supervisor = new ProfileProcessSupervisor(fakeProbe, fakeStarter);
            var dashboard = new ProfileDashboard(root, repository, supervisor);
            var legacyPath = BrowserPolicy.ProfilePath(root, "legacy");
            Directory.CreateDirectory(legacyPath);
            File.WriteAllText(Path.Combine(legacyPath, "sentinel"), "legacy browser data");
            Check(dashboard.Load().Any(p => p.Slug == "legacy" && p.Metadata is null) && !File.Exists(Path.Combine(root, ProfileRepository.DatabaseFileName)),
                "legacy listing is read-only");
            using (var lease = new ProfileLease(legacyPath)) Reject(() => dashboard.PrepareEdit("legacy"), "editing a busy legacy profile does not migrate it");
            var legacy = dashboard.PrepareEdit("legacy");
            Check(legacy.Id != Guid.Empty && File.ReadAllText(Path.Combine(legacyPath, "sentinel")) == "legacy browser data" &&
                Directory.GetFiles(Path.Combine(root, ProfileRepository.BackupDirectoryName), "*.sqlite3").Length == 1, "legacy edit creates a backup and preserves cache");
            var a = dashboard.Create(new ProfileDraft("Alpha", "Zebra", "initial notes", ["qa", "green"], ["Work"]));
            var b = dashboard.Create(new ProfileDraft("beta", "Apple", "", ["sales"], ["Personal"]));
            Reject(() => dashboard.Create(new ProfileDraft("invalid", " ", "", [], [])), "invalid draft rejected");
            Check(!Directory.Exists(BrowserPolicy.ProfilePath(root, "invalid")), "invalid creation does not create a directory");
            Reject(() => dashboard.Create(new ProfileDraft("ALPHA", "Another", "", [], [])), "duplicate slug rejected");
            var cache = Path.Combine(BrowserPolicy.ProfilePath(root, "alpha"), "Cache");
            Directory.CreateDirectory(cache); File.WriteAllText(Path.Combine(cache, "sentinel"), "persistent");
            var saved = dashboard.Save(a with { DisplayName = "Renamed", Notes = "updated note", Tags = ["qa", "blue"], Folders = ["Clients", "Work"] });
            var reopened = new ProfileRepository(root).GetBySlug("alpha")!;
            Check(reopened.Id == a.Id && reopened.Slug == "alpha" && reopened.DisplayName == "Renamed" && reopened.Notes == "updated note" &&
                reopened.Tags.SequenceEqual(["blue", "qa"]) && reopened.Folders.SequenceEqual(["Clients", "Work"]) &&
                File.ReadAllText(Path.Combine(cache, "sentinel")) == "persistent", "edit persists without moving browser data");
            Reject(() => dashboard.Save(a with { Notes = "stale" }), "stale editor rejected");
            Check(new ProfileRepository(root).GetBySlug("alpha")!.Notes == "updated note", "stale edit does not overwrite");
            fakeProbe.Status["alpha"] = new(ProfileActivity.InUse);
            var list = dashboard.Load();
            Check(list.Single(p => p.Slug == "alpha").DisplayName == "Renamed" && list.Single(p => p.Slug == "legacy").Metadata?.Id == legacy.Id,
                "listing renders persisted and migrated metadata");
            Check(ProfileDashboard.Filter(list, "BLUE", DashboardStatusFilter.All, null, DashboardSort.Name).Single().Slug == "alpha", "search tags");
            Check(ProfileDashboard.Filter(list, "client", DashboardStatusFilter.All, null, DashboardSort.Name).Single().Slug == "alpha", "search folders");
            Check(ProfileDashboard.Filter(list, "renam", DashboardStatusFilter.All, null, DashboardSort.Name).Single().Slug == "alpha", "search display name");
            Check(ProfileDashboard.Filter(list, "", DashboardStatusFilter.Running, null, DashboardSort.Name).Single().Slug == "alpha", "filter active status");
            Check(ProfileDashboard.Filter(list, "", DashboardStatusFilter.All, "personal", DashboardSort.Name).Single().Slug == "beta", "filter folder");
            var sorted = ProfileDashboard.Filter(list, "", DashboardStatusFilter.All, null, DashboardSort.Name);
            Check(sorted.Select(p => p.DisplayName).SequenceEqual(sorted.Select(p => p.DisplayName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)), "sort display names");
            var dates = new[] { list.Single(p => p.Slug == "beta") with { Metadata = b with { UpdatedUtc = DateTimeOffset.UtcNow.AddDays(-1) } },
                list.Single(p => p.Slug == "alpha") with { Metadata = saved with { UpdatedUtc = DateTimeOffset.UtcNow } } };
            Check(ProfileDashboard.Filter(dates, "", DashboardStatusFilter.All, null, DashboardSort.Updated)[0].Slug == "alpha", "sort by newest metadata");
            Check(repository.GetBySlug("alpha")!.Revision == saved.Revision, "search and filtering do not write metadata");
            var proxyRepository = new ProxyRepository(root);
            var proxyDashboard = new ProfileDashboard(root, repository, supervisor, proxyRepository);
            var betaPath = BrowserPolicy.ProfilePath(root, "beta");
            ProxyConfiguration betaProxy;
            using (var lease = new ProfileLease(betaPath))
                betaProxy = proxyRepository.Save(b, lease, new ProxyDraft(ProxyProtocol.Http, "proxy.example", 8080, false, null, "", true), 0);
            var blockedRow = proxyDashboard.Load().Single(p => p.Slug == "beta");
            Check(blockedRow.ProxyState == SavedProxyState.RequiredDraft && !blockedRow.CanLaunch, "saved proxy draft is visible and blocks dashboard launch");
            Reject(() => proxyDashboard.Launch(blockedRow), "dashboard rechecks saved proxy state before launch");
            using (var lease = new ProfileLease(betaPath))
                proxyRepository.Save(b, lease, new ProxyDraft(ProxyProtocol.Direct, "", 0, false, null, "", false), betaProxy.Revision);
            Check(proxyDashboard.Load().Single(p => p.Slug == "beta").ProxyState == SavedProxyState.BrowserDefaults,
                "dashboard shows browser defaults after explicit draft removal");
            Reject(() => dashboard.Launch(list.Single(p => p.Slug == "alpha")), "external held lease prevents launch");
            fakeProbe.Status["unknown"] = new(ProfileActivity.Unknown, "No access to lock");
            Reject(() => supervisor.Launch("unknown"), "unknown lock status prevents launch");
            using (var failing = new ProfileProcessSupervisor(fakeProbe, new FailingStarter()))
                Reject(() => failing.Launch("beta"), "process start failure surfaced");

            var probe = new ProfileLeaseProbe(root);
            var starter = new LeaseChildStarter(root);
            using var realSupervisor = new ProfileProcessSupervisor(probe, starter);
            Check(probe.Inspect("beta").Activity == ProfileActivity.Stopped, "unheld profile lock is stopped");
            var lockPath = Path.Combine(BrowserPolicy.ProfilePath(root, "beta"), ".profile.lock");
            using (var blocker = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                var waiting = Task.Run(() => new ProfileLease(BrowserPolicy.ProfilePath(root, "beta")));
                Thread.Sleep(80);
                blocker.Dispose();
                using var acquired = waiting.GetAwaiter().GetResult();
                Check(true, "lease retries a transient dashboard probe");
            }
            realSupervisor.Launch("beta");
            Check(realSupervisor.GetStatus("beta").Activity == ProfileActivity.Running, "owned child holding real OS lease is running");
            Reject(() => realSupervisor.Launch("beta"), "duplicate owned launch rejected");
            using (var another = new ProfileProcessSupervisor(probe, fakeStarter))
            {
                Check(another.GetStatus("beta").Activity == ProfileActivity.InUse, "other manager sees held lease without claiming process identity");
                Reject(() => another.Launch("beta"), "other manager rejects held lease");
            }
            starter.Current!.Stop("ok");
            Check(realSupervisor.GetStatus("beta").Activity == ProfileActivity.Stopped && probe.Inspect("beta").Activity == ProfileActivity.Stopped,
                "child exit releases profile lease");
            realSupervisor.Launch("beta");
            starter.Current!.Stop("fail");
            Check(realSupervisor.GetStatus("beta").Activity == ProfileActivity.Failed, "failed child exit reported");
            realSupervisor.Launch("beta");
            starter.Current!.Stop("ok");
            Check(realSupervisor.GetStatus("beta").Activity == ProfileActivity.Stopped, "profile can reopen after exit");
            using (var held = new ProfileLease(legacyPath)) Check(probe.Inspect("legacy").Activity == ProfileActivity.InUse, "existing external lease detected");
            Check(probe.Inspect("../escape").Activity == ProfileActivity.Unknown, "unsafe slug reported as unknown");
            repository.Create("orphan");
            Directory.Delete(BrowserPolicy.ProfilePath(root, "orphan"));
            var realDashboard = new ProfileDashboard(root, repository, realSupervisor);
            var orphan = realDashboard.Load().Single(p => p.Slug == "orphan");
            Check(orphan.Status.Activity == ProfileActivity.Missing && !orphan.CanLaunch, "missing registered directory cannot launch");
            Reject(() => realDashboard.Launch(orphan), "missing profile is not recreated");
            var badPath = BrowserPolicy.ProfilePath(root, "badlock");
            Directory.CreateDirectory(Path.Combine(badPath, ".profile.lock"));
            Check(probe.Inspect("badlock").Activity == ProfileActivity.Unknown, "unreadable lock is not reported stopped");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"PASS: {count} profile dashboard checks");
    }

    private sealed class FakeProbe : IProfileLeaseProbe
    {
        public Dictionary<string, ProfileStatus> Status { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ProfileStatus Inspect(string slug) => Status.GetValueOrDefault(slug) ?? new(ProfileActivity.Stopped);
    }
    private sealed class FakeStarter : IProfileChildStarter
    {
        public IProfileChild Start(string slug) => new FakeChild();
        private sealed class FakeChild : IProfileChild { public bool HasExited => false; public int ExitCode => 0; public void Dispose() { } }
    }
    private sealed class FailingStarter : IProfileChildStarter
    { public IProfileChild Start(string slug) => throw new IOException("Test process could not start."); }
    private sealed class LeaseChildStarter(string root) : IProfileChildStarter
    {
        public LeaseChild? Current { get; private set; }
        public IProfileChild Start(string slug)
        {
            var executable = Environment.ProcessPath ?? throw new IOException("Process path unavailable.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--dashboard-lease-child"); start.ArgumentList.Add(BrowserPolicy.ProfilePath(root, slug));
            var process = Process.Start(start) ?? throw new IOException("Cannot start lease child.");
            var ready = process.StandardOutput.ReadLineAsync();
            if (!ready.Wait(TimeSpan.FromSeconds(15)) || ready.Result != "READY")
            { if (!process.HasExited) process.Kill(true); process.Dispose(); throw new IOException("Lease child did not start."); }
            Current = new LeaseChild(process); return Current;
        }
    }
    private sealed class LeaseChild(Process process) : IProfileChild
    {
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        public void Stop(string result)
        {
            process.StandardInput.WriteLine(result); process.StandardInput.Flush();
            if (!process.WaitForExit(15000)) throw new Exception("Lease child did not exit.");
        }
        public void Dispose() => process.Dispose();
    }
}
