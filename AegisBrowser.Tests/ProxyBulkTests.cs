using AegisBrowser.Core;

internal static class ProxyBulkTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL bulk proxy drafts: " + name); count++; }
        void Reject(Action action, string name)
        {
            try { action(); } catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { count++; return; }
            throw new Exception("FAIL bulk proxy drafts (accepted): " + name);
        }
        var root = Path.Combine(Path.GetTempPath(), "aegis-proxy-bulk-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profiles = new ProfileRepository(root);
            var first = profiles.Create(new ProfileDraft("first", "First", "private fixture note", [], []));
            var second = profiles.Create("second"); var credentialed = profiles.Create("credentialed");
            var repository = new ProxyRepository(root); var vault = new ProxyCredentialVault(root);
            var editor = new ProxyConfigurationEditor(root, repository, vault); var bulk = new ProxyBulkDrafts(editor);
            var direct = new ProxyDraft(ProxyProtocol.Direct, "", 0, false, null, "", false);
            var proxy = new ProxyDraft(ProxyProtocol.Http, "proxy.example", 8080, false, null, "shared endpoint", true);
            Guid? credentialId = null;
            if (OperatingSystem.IsWindows())
            {
                var credentialDraft = proxy with { AuthenticationRequired = true };
                credentialId = editor.Save(credentialed, credentialDraft, 0, new ProxyCredentials("bulk-fixture-user", "bulk-fixture-secret"))
                    .Configuration.Settings.CredentialId;
            }
            var preview = bulk.Preview([first, second, credentialed], CancellationToken.None);
            Check(preview.Count == 3 && preview.All(item => item.Outcome == ProfileOperationOutcome.Succeeded), "preview loads each closed profile");
            Check(!preview[0].ToString().Contains(first.Notes, StringComparison.Ordinal), "preview string omits profile notes");
            var otherEdit = editor.Save(second, direct, 0, null);
            var results = bulk.Apply(preview, proxy, CancellationToken.None);
            Check(results[0].Outcome == ProfileOperationOutcome.Succeeded && results[1].Outcome == ProfileOperationOutcome.Failed,
                "bulk reports successful and stale targets separately");
            Check(editor.Load(first)?.Settings == proxy && editor.Load(second)?.Revision == otherEdit.Configuration.Revision,
                "stale target is unchanged while another target succeeds");
            if (OperatingSystem.IsWindows())
            {
                Check(results[2].Outcome == ProfileOperationOutcome.Failed && editor.Load(credentialed)?.Settings.CredentialId == credentialId,
                    "credential-bearing profile is skipped without replacing its secret");
                Check(vault.Read(credentialed.Id, credentialId!.Value).Username == "bulk-fixture-user", "protected credential remains readable");
            }
            var fresh = bulk.Preview([first, second], CancellationToken.None);
            Reject(() => bulk.Apply(fresh, proxy with { AuthenticationRequired = true, CredentialId = Guid.NewGuid() }, CancellationToken.None),
                "bulk cannot share credential references");
            Reject(() => bulk.Preview([first, first], CancellationToken.None), "duplicate profile identity rejected");
            Reject(() => bulk.Apply(fresh, proxy with { Port = 0 }, CancellationToken.None), "invalid batch rejected before updates");
            Check(editor.Load(first)?.Settings == proxy, "invalid batch preserves profiles");

            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress(value => { if (value.Completed == 1) cancellation.Cancel(); });
            var partial = bulk.Apply(fresh, direct, cancellation.Token, progress);
            Check(partial[0].Outcome == ProfileOperationOutcome.Succeeded && partial[1].Outcome == ProfileOperationOutcome.Canceled,
                "cancellation stops before the next profile");
            Check(editor.Load(first)?.Settings.Protocol == ProxyProtocol.Direct && editor.Load(second)?.Revision == otherEdit.Configuration.Revision,
                "canceled target remains unchanged");

            IReadOnlyList<ProxyBulkPreview> busy;
            using (var held = new ProfileLease(BrowserPolicy.ProfilePath(root, second.Slug)))
                busy = bulk.Preview([second], CancellationToken.None);
            Check(busy[0].Outcome == ProfileOperationOutcome.Failed, "busy profile is reported in preview");
            Check(bulk.Apply(busy, proxy, CancellationToken.None)[0].Outcome == ProfileOperationOutcome.Failed,
                "failed preview is not silently retried after the lease is released");
            var metadataPreview = bulk.Preview([second], CancellationToken.None);
            profiles.Update(second with { Notes = "changed elsewhere" }, second.Revision);
            Check(bulk.Apply(metadataPreview, proxy, CancellationToken.None)[0].Outcome == ProfileOperationOutcome.Failed,
                "metadata revision change prevents stale batch update");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine($"PASS: {count} bulk proxy draft checks");
    }

    private sealed class InlineProgress(Action<ProfileOperationProgress> report) : IProgress<ProfileOperationProgress>
    {
        public void Report(ProfileOperationProgress value) => report(value);
    }
}
