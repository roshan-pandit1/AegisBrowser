using AegisBrowser.Core;

internal static class ProfileBulkTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL bulk profiles: " + name); count++; }
        void Reject(Action action, string name)
        {
            try { action(); } catch (Exception ex) when (ex is IOException or ArgumentException) { count++; return; }
            throw new Exception("FAIL bulk profiles (accepted): " + name);
        }
        var root = Path.Combine(Path.GetTempPath(), "aegis-bulk-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new ProfileRepository(root);
            var a = repository.Create(new ProfileDraft("alpha", "Alpha", "private note", ["old"], ["Work"]));
            var b = repository.Create(new ProfileDraft("beta", "Beta", "untouched", [], []));
            var service = new ProfileBulkOperations(root, repository, new ProfileDuplicationService(root, repository));
            var changedElsewhere = repository.Update(b with { Notes = "changed elsewhere" }, b.Revision);
            var partial = service.Run([a, b], ProfileBulkAction.AddTags, ["qa"], CancellationToken.None);
            Check(partial[0].Outcome == ProfileOperationOutcome.Succeeded && partial[1].Outcome == ProfileOperationOutcome.Failed,
                "per-profile success and revision conflict reported");
            Check(repository.GetBySlug("alpha")!.Tags.Contains("qa") && repository.GetBySlug("beta")!.Notes == "changed elsewhere" &&
                !repository.GetBySlug("beta")!.Tags.Contains("qa"), "stale bulk target remains unchanged");
            var latestA = repository.GetBySlug("alpha")!;
            var latestB = repository.GetBySlug("beta")!;
            var folders = service.Run([latestA, latestB], ProfileBulkAction.AddFolders, ["Team"], CancellationToken.None);
            Check(folders.All(r => r.Outcome == ProfileOperationOutcome.Succeeded) && repository.GetBySlug("beta")!.Folders.Contains("Team"), "folders assigned");
            latestA = repository.GetBySlug("alpha")!; latestB = repository.GetBySlug("beta")!;
            var remove = service.Run([latestA, latestB], ProfileBulkAction.RemoveFolders, ["team"], CancellationToken.None);
            Check(remove.All(r => r.Outcome == ProfileOperationOutcome.Succeeded) && !repository.GetBySlug("beta")!.Folders.Contains("Team"), "folders removed case-insensitively");
            latestA = repository.GetBySlug("alpha")!;
            service.Run([latestA], ProfileBulkAction.RemoveTags, ["OLD"], CancellationToken.None);
            Check(!repository.GetBySlug("alpha")!.Tags.Contains("old"), "tags removed case-insensitively");
            Reject(() => service.Run([latestA, latestA], ProfileBulkAction.AddTags, ["tag"], CancellationToken.None), "duplicate selected identity rejected");
            Reject(() => service.Run([latestA], ProfileBulkAction.AddTags, ["tag", "TAG"], CancellationToken.None), "duplicate labels rejected");
            latestA = repository.GetBySlug("alpha")!; latestB = repository.GetBySlug("beta")!;
            using var cancel = new CancellationTokenSource();
            var progress = new CallbackProgress(p => { if (p.Completed == 1) cancel.Cancel(); });
            var canceled = service.Run([latestA, latestB], ProfileBulkAction.AddTags, ["once"], cancel.Token, progress);
            Check(canceled[0].Outcome == ProfileOperationOutcome.Succeeded && canceled[1].Outcome == ProfileOperationOutcome.Canceled &&
                !repository.GetBySlug("beta")!.Tags.Contains("once"), "cancellation stops before next profile");
            latestA = repository.GetBySlug("alpha")!; latestB = repository.GetBySlug("beta")!;
            var copies = service.Run([latestA, latestB], ProfileBulkAction.DuplicateClean, [], CancellationToken.None);
            Check(copies.All(r => r.Outcome == ProfileOperationOutcome.Succeeded), "selected clean copies succeed");
            var copy = repository.GetBySlug("alpha-copy")!;
            Check(copy.Id != latestA.Id && copy.Notes == "" && !Directory.Exists(Path.Combine(BrowserPolicy.ProfilePath(root, copy.Slug), "Cache")),
                "bulk clean copy excludes notes and browser data");
            var again = service.Run([latestA], ProfileBulkAction.DuplicateClean, [], CancellationToken.None);
            Check(again.Single().Outcome == ProfileOperationOutcome.Succeeded && repository.GetBySlug("alpha-copy-2") is not null,
                "duplicate slug generation avoids collisions");
            Check(changedElsewhere.Id == repository.GetBySlug("beta")!.Id, "bulk operations preserve existing identities");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"PASS: {count} bulk profile checks");
    }

    private sealed class CallbackProgress(Action<ProfileOperationProgress> callback) : IProgress<ProfileOperationProgress>
    { public void Report(ProfileOperationProgress value) => callback(value); }
}
