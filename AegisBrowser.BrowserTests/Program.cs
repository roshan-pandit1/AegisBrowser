using AegisBrowser;
using AegisBrowser.Core;
using CefSharp;
using CefSharp.WinForms;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    private const string Origin = "https://browser-tests.invalid/";
    private static int checks;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "dashboard-launch") return DashboardApplication(args[1]);
        if (args.Length == 2 && args[0] == "proxy-probe") return ProxyProbe(args[1]);
        if (args.Length == 2 && args[0] == "proxy-auth-probe") return ProxyAuthProbe(args[1]);
        if (args.Length != 2 || args[0] is not ("write" or "read")) return 2;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var root = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(root);
        var initialized = false;
        var result = 0;
        ProfileLease? leaseA = null, leaseB = null;
        try
        {
            if (args[0] == "read")
            {
                // The write process created these CEF profiles without metadata. Adopt them
                // before CEF starts, then verify their real cookie/localStorage data below.
                var pathA = BrowserPolicy.ProfilePath(root, "a");
                var pathB = BrowserPolicy.ProfilePath(root, "b");
                Check(Directory.Exists(pathA) && Directory.Exists(pathB), "legacy CEF profiles exist before migration");
                ProfileRepository.CheckSafeProfilePath(root, "a");
                ProfileRepository.CheckSafeProfilePath(root, "b");
                leaseA = new ProfileLease(pathA);
                leaseB = new ProfileLease(pathB);
                var repository = new ProfileRepository(root);
                var a = repository.GetOrMigrate("a", leaseA);
                var b = repository.GetOrMigrate("b", leaseB);
                var idsPath = Path.Combine(root, "metadata-test-ids.txt");
                var ids = a.Id.ToString("D") + "\n" + b.Id.ToString("D");
                if (File.Exists(idsPath)) Check(File.ReadAllText(idsPath) == ids, "profile metadata IDs survive process restart");
                else { Check(a.Id != Guid.Empty && b.Id != Guid.Empty && a.Id != b.Id, "legacy CEF profiles receive distinct IDs"); File.WriteAllText(idsPath, ids); }
            }
            foreach (var dependency in new[] { "CefSharp.BrowserSubprocess.exe", "CefSharp.BrowserSubprocess.runtimeconfig.json", "CefSharp.BrowserSubprocess.Core.dll" })
            {
                var dependencyPath = Path.Combine(AppContext.BaseDirectory, dependency);
                Console.WriteLine(dependency + ": " + File.Exists(dependencyPath));
                if (dependency.EndsWith(".json") && File.Exists(dependencyPath)) Console.WriteLine(File.ReadAllText(dependencyPath));
            }
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(new CefSettings
            {
                RootCachePath = root, CachePath = Path.Combine(root, "global"),
                CommandLineArgsDisabled = true, LogFile = Path.Combine(root, "cef-test.log"), LogSeverity = LogSeverity.Info
            });
            if (!initialized) throw new Exception("CEF initialization failed");
            using var global = Cef.GetGlobalRequestContext();
            global.RegisterSchemeHandlerFactory("https", "aegis-diagnostics.invalid", new DiagnosticsFactory());
            global.RegisterSchemeHandlerFactory("https", "browser-tests.invalid", new Fixture());
            // Local HTTPS fixtures exercise the actual omnibox without contacting search providers.
            foreach (var engine in SearchEngines.All)
                global.RegisterSchemeHandlerFactory("https", new Uri(engine.Template.Replace("{searchTerms}", "test")).Host, new Fixture());
            using var host = new Form { Width = 1000, Height = 700, Text = "Aegis CEF integration tests" };
            host.Shown += async (_, _) =>
            {
                try
                {
                    if (args[0] == "write") { await TabsAndRenderer(); await DashboardUi(root); }
                    await Storage(host, root, args[0]);
                    if (args[0] == "write") await RejectUntrustedTls(host);
                    Console.WriteLine($"PASS: {checks} browser integration checks ({args[0]})");
                }
                catch (Exception ex)
                {
                    result = 1;
                    Console.Error.WriteLine(ex);
                    try
                    {
                        var log = Path.Combine(root, "cef-test.log");
                        if (File.Exists(log)) Console.Error.WriteLine(File.ReadAllText(log));
                    }
                    catch (IOException logError) { Console.Error.WriteLine("Log unavailable: " + logError.Message); }
                }
                finally { host.Close(); }
            };
            Application.Run(host);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); }
            leaseB?.Dispose(); leaseA?.Dispose();
        }
        if (result == 0) Console.WriteLine("PASS: CEF shutdown completed (" + args[0] + ")");
        return result;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        checks++;
        Console.WriteLine("PASS: " + name);
    }

    private static async Task Wait(Func<bool> predicate, string name)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(name);
            await Task.Delay(100);
        }
    }

    private static async Task DashboardUi(string root)
    {
        var profilesRoot = Path.Combine(root, "dashboard-data");
        var repository = new ProfileRepository(profilesRoot);
        var original = repository.Create(new ProfileDraft("ui-test", "Dashboard sample", "old note", ["blue"], ["Work"]));
        using var manager = new ProfileManagerForm(profilesRoot);
        manager.Show();
        T Control<T>(Control parent, string name) where T : Control =>
            parent.Controls.Find(name, true).OfType<T>().Single();
        var grid = Control<DataGridView>(manager, "ProfileGrid");
        await Wait(() => grid.Rows.Cast<DataGridViewRow>().Any(r => (string?)r.Cells["Slug"].Value == "ui-test"), "dashboard rows loaded");
        var row = grid.Rows.Cast<DataGridViewRow>().Single(r => (string?)r.Cells["Slug"].Value == "ui-test");
        Check((string?)row.Cells["DisplayName"].Value == "Dashboard sample" && (string?)row.Cells["ProfileId"].Value == original.Id.ToString("D") &&
            (string?)row.Cells["Tags"].Value == "blue" && (string?)row.Cells["Folders"].Value == "Work", "dashboard renders real metadata");
        grid.ClearSelection(); row.Selected = true;
        Check(Control<TextBox>(manager, "ProfileDetails").Text.Contains(original.Id.ToString("D")) &&
            Control<TextBox>(manager, "ProfileDetails").Text.Contains("old note"), "dashboard shows selected profile details");
        var search = Control<TextBox>(manager, "ProfileSearch");
        search.Text = "BLUE";
        Check(grid.Rows.Count == 1 && (string?)grid.Rows[0].Cells["Slug"].Value == "ui-test", "dashboard search filters rendered rows");
        search.Clear();

        using var supervisor = new ProfileProcessSupervisor(new ProfileLeaseProbe(profilesRoot),
            new ProfileChildStarter(Environment.ProcessPath!, typeof(ProfileManagerForm).Assembly.Location, AppContext.BaseDirectory));
        var dashboard = new ProfileDashboard(profilesRoot, repository, supervisor);
        using (var editor = new ProfileEditorForm(dashboard, original))
        {
            editor.Show(manager);
            Control<TextBox>(editor, "EditDisplayName").Text = "Updated display";
            Control<TextBox>(editor, "EditNotes").Text = "updated note";
            Control<TextBox>(editor, "EditTags").Text = "blue\r\nqa";
            Control<TextBox>(editor, "EditFolders").Text = "Work\r\nClients";
            Control<Button>(editor, "SaveProfile").PerformClick();
            await Wait(() => editor.IsDisposed || !editor.Visible, "dashboard editor save");
        }
        var saved = repository.GetBySlug("ui-test")!;
        Check(saved.Id == original.Id && saved.DisplayName == "Updated display" && saved.Notes == "updated note" &&
            saved.Tags.Contains("qa") && saved.Folders.Contains("Clients"), "native editor persists profile metadata");
        await Wait(() => grid.Rows.Cast<DataGridViewRow>().Any(r => (string?)r.Cells["DisplayName"].Value == "Updated display"), "dashboard refresh after editor save");
        using (var editor = new ProfileEditorForm(dashboard, saved))
        {
            editor.Show(manager);
            repository.Update(saved with { Notes = "changed elsewhere" }, saved.Revision);
            Control<TextBox>(editor, "EditNotes").Text = "stale edit";
            Control<Button>(editor, "SaveProfile").PerformClick();
            await Wait(() => Control<Label>(editor, "EditorFeedback").Text.Contains("changed", StringComparison.OrdinalIgnoreCase), "stale editor warning");
            Check(editor.Visible && Control<TextBox>(editor, "EditNotes").Text == "stale edit" && repository.GetBySlug("ui-test")!.Notes == "changed elsewhere",
                "native editor retains stale input without overwriting");
            editor.Close();
        }
        var latest = repository.GetBySlug("ui-test")!;
        var duplicator = new ProfileDuplicationService(profilesRoot, repository);
        using (var copy = new ProfileCopyForm("Duplicate profile", "UI copy", true,
            (slug, name, notes, privacy, searchSetting) => duplicator.DuplicateClean(latest, slug, name, notes, privacy, searchSetting)))
        {
            copy.Show(manager);
            Control<TextBox>(copy, "CopySlug").Text = "ui-copy";
            Control<Button>(copy, "CopySave").PerformClick();
            await Wait(() => copy.IsDisposed || !copy.Visible, "clean copy dialog");
        }
        var copied = repository.GetBySlug("ui-copy");
        Check(copied is not null && copied.Id != latest.Id &&
            !Directory.Exists(Path.Combine(BrowserPolicy.ProfilePath(profilesRoot, "ui-copy"), "Cache")), "native clean-copy dialog creates fresh storage");
        var templateRepository = new ProfileTemplateRepository(profilesRoot);
        var templateService = new ProfileTemplateService(profilesRoot, repository, templateRepository);
        using (var capture = new TemplateCaptureForm(templateService, latest))
        {
            capture.Show(manager);
            Control<TextBox>(capture, "TemplateName").Text = "UI template";
            Control<Button>(capture, "SaveTemplate").PerformClick();
            await Wait(() => capture.IsDisposed || !capture.Visible, "template capture dialog");
        }
        Check(templateRepository.List().Single().Name == "UI template", "native template dialog persists template");
        using (var templates = new ProfileTemplatesForm(templateRepository, templateService,
            new ProfileTransfer(profilesRoot, repository, templateRepository)))
        {
            templates.Show(manager);
            var templateList = Control<ListBox>(templates, "TemplateList");
            await Wait(() => templateList.Items.Count == 1, "template manager load");
            Control<TextBox>(templates, "TemplateSearch").Text = "no matching template";
            Check(templateList.Items.Count == 0, "native template search filters results");
            templates.Close();
        }
        await Wait(() => grid.Rows.Cast<DataGridViewRow>().Any(r => (string?)r.Cells["Slug"].Value == "ui-copy"), "dashboard refresh after copy");
        Control<Button>(manager, "SelectVisible").PerformClick();
        Check(Control<Label>(manager, "SelectedProfileCount").Text == "2 selected", "dashboard selects registered visible profiles");
        search.Text = "ui-copy";
        Check(grid.Rows.Count == 1 && Control<Label>(manager, "SelectedProfileCount").Text == "2 selected", "profile selection survives filtering");
        search.Clear();
        var selected = new[] { repository.GetBySlug("ui-test")!, repository.GetBySlug("ui-copy")! };
        var bulkService = new ProfileBulkOperations(profilesRoot, repository, duplicator);
        using (var bulk = new ProfileBulkForm(bulkService, selected, false))
        {
            bulk.Show(manager);
            Control<TextBox>(bulk, "BulkLabels").Text = "shared";
            Control<Button>(bulk, "StartBulk").PerformClick();
            await Wait(() => Control<TextBox>(bulk, "BulkResults").Text.Contains("Succeeded", StringComparison.Ordinal), "bulk result display");
            Check(repository.GetBySlug("ui-test")!.Tags.Contains("shared") && repository.GetBySlug("ui-copy")!.Tags.Contains("shared"),
                "native bulk dialog updates selected profiles");
            bulk.Close();
        }
        var transfer = new ProfileTransfer(profilesRoot, repository, templateRepository);
        var exportPath = Path.Combine(root, "dashboard-export.json");
        transfer.Export(exportPath, [repository.GetBySlug("ui-test")!], [], false, false, false);
        var preview = transfer.Preview(exportPath);
        using (var import = new ProfileImportForm(transfer, preview))
        {
            import.Show(manager);
            Check(Control<TextBox>(import, "ImportPreview").Text.Contains(preview.Profiles[0].DestinationSlug), "native import displays destination preview");
            Control<Button>(import, "ConfirmImport").PerformClick();
            await Wait(() => Control<TextBox>(import, "ImportPreview").Text.Contains("Succeeded", StringComparison.Ordinal), "native import result");
            Check(repository.GetBySlug(preview.Profiles[0].DestinationSlug) is not null, "native import creates previewed profile");
            import.Close();
        }
        var backupService = new ProfileBackupService(profilesRoot, repository);
        using (var backupForm = new ProfileBackupsForm(backupService, repository, repository.GetBySlug("ui-test")!))
        {
            backupForm.Show(manager);
            var backupDetails = Control<TextBox>(backupForm, "BackupDetails");
            await Wait(() => backupDetails.Text.Contains("No manual backups"), "backup manager load");
            Control<Button>(backupForm, "CreateBackup").PerformClick();
            var backupList = Control<ListBox>(backupForm, "BackupList");
            await Wait(() => backupList.Items.Count == 1 && backupDetails.Text.Contains("ui-test"), "backup creation and preview");
            Check(backupService.List().Single().Valid, "native backup manager creates and previews a verified snapshot");
            backupForm.Close();
        }
        templateRepository.Create("Settings template", "", [], [], new(null, new SearchSettings("google")));
        var transactionService = new ProfileSettingsTransactions(profilesRoot, repository, templateRepository);
        using (var applyForm = new ProfileTemplateApplyForm(templateRepository, transactionService, [repository.GetBySlug("ui-test")!]))
        {
            applyForm.Show(manager);
            var choices = Control<ListBox>(applyForm, "ApplyTemplateList");
            await Wait(() => choices.Items.Count == 1, "template settings choices load");
            Check(choices.Items[0]!.ToString()!.Contains("Settings template"), "native template application shows settings-capable templates");
            applyForm.Close();
        }
        await ProxySettingsUi(profilesRoot, repository, manager);
        manager.Close();
    }

    private static int DashboardApplication(string executable)
    {
        var root = ProfileCatalog.DefaultRoot;
        var slug = "dash-" + Guid.NewGuid().ToString("N");
        var path = BrowserPolicy.ProfilePath(root, slug);
        var targetTitle = "Aegis Browser — " + slug;
        var repository = new ProfileRepository(root);
        using var supervisor = new ProfileProcessSupervisor(new ProfileLeaseProbe(root),
            new ProfileChildStarter(Path.GetFullPath(executable), "", Path.GetDirectoryName(Path.GetFullPath(executable))!));
        var dashboard = new ProfileDashboard(root, repository, supervisor);
        Process? browser = null;
        try
        {
            var created = dashboard.Create(new ProfileDraft(slug, "Dashboard launch test", "", [], []));
            for (var round = 0; round < 2; round++)
            {
                var row = dashboard.Load().Single(p => p.Slug == slug);
                dashboard.Launch(row);
                var rejected = false;
                try { dashboard.Launch(row); } catch (IOException) { rejected = true; }
                Check(rejected, "dashboard rejects duplicate actual browser launch");
                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (browser is null && DateTime.UtcNow < deadline)
                {
                    foreach (var process in Process.GetProcessesByName("AegisBrowser"))
                    {
                        process.Refresh();
                        if (process.MainWindowTitle == targetTitle && browser is null) browser = process;
                        else process.Dispose();
                    }
                    if (browser is null) Thread.Sleep(250);
                }
                if (browser is null) throw new TimeoutException("Published browser window did not start.");
                Thread.Sleep(2000);
                browser.Refresh();
                if (browser.HasExited) throw new Exception("Published browser exited immediately after startup.");
                Check(supervisor.GetStatus(slug).Activity == ProfileActivity.Running && Directory.Exists(Path.Combine(path, "Cache")),
                    "published browser holds the profile lease and creates its cache");
                // This window was found by enumeration; the supervisor owns the process
                // handle and reports a nonzero exit as Failed below.
                if (!browser.CloseMainWindow() || !browser.WaitForExit(15000))
                    throw new Exception("Published browser did not close normally.");
                browser.Dispose(); browser = null;
                Check(supervisor.GetStatus(slug).Activity == ProfileActivity.Stopped && repository.GetBySlug(slug)?.Id == created.Id,
                    "normal shutdown releases the lease and preserves profile identity");
            }
            Console.WriteLine($"PASS: {checks} dashboard application checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            if (browser is not null)
            {
                if (!browser.HasExited) { browser.CloseMainWindow(); browser.WaitForExit(15000); }
                browser.Dispose();
            }
            Console.WriteLine("Dashboard test profile: " + path);
        }
    }

    private static async Task<string> Js(ChromiumWebBrowser browser, string script)
    {
        var response = await browser.EvaluateScriptAsync(script).WaitAsync(TimeSpan.FromSeconds(10));
        if (!response.Success) throw new Exception("JavaScript failed: " + response.Message);
        return response.Result?.ToString() ?? "";
    }

    private static async Task Ready(ChromiumWebBrowser browser)
    {
        try
        {
            await Wait(() => browser.IsBrowserInitialized && browser.CanExecuteJavascriptInMainFrame && !browser.IsLoading,
                "browser ready (" + browser.Address + ")");
        }
        catch
        {
            Console.Error.WriteLine($"Readiness: initialized={browser.IsBrowserInitialized}, javascript={browser.CanExecuteJavascriptInMainFrame}, loading={browser.IsLoading}, address={browser.Address}");
            throw;
        }
        // A completed JS round trip also proves the live renderer is responsive.
        await Js(browser, "document.readyState");
    }

    private static async Task LoadReady(ChromiumWebBrowser web, string url)
    {
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<FrameLoadEndEventArgs> handler = (_, e) => { if (e.Frame.IsMain && e.Url == url) loaded.TrySetResult(true); };
        web.FrameLoadEnd += handler;
        try { web.Load(url); await loaded.Task.WaitAsync(TimeSpan.FromSeconds(25)); await Ready(web); }
        finally { web.FrameLoadEnd -= handler; }
    }

    private static async Task ReloadReady(ChromiumWebBrowser browser)
    {
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<FrameLoadEndEventArgs> handler = (_, e) => { if (e.Frame.IsMain) loaded.TrySetResult(true); };
        browser.FrameLoadEnd += handler;
        try
        {
            browser.Reload();
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(25));
            await Ready(browser);
        }
        finally { browser.FrameLoadEnd -= handler; }
    }

    private static void Observe(ChromiumWebBrowser web)
    {
        if (web.LifeSpanHandler is not SafePopups) web.LifeSpanHandler = new SafePopups(Console.WriteLine);
        // Subscribe before creating the handle, ensuring CEF's load handler is installed.
        web.FrameLoadEnd += (_, e) => { if (e.Frame.IsMain) Console.WriteLine($"Loaded {e.Url}: HTTP {e.HttpStatusCode}"); };
        web.LoadError += (_, e) => Console.WriteLine($"Load error {e.ErrorCode}: {e.FailedUrl}");
        web.IsBrowserInitializedChanged += (_, _) => Console.WriteLine($"Initialized: {web.IsBrowserInitialized}");
    }

    private static async Task CloseWeb(ChromiumWebBrowser web)
    {
        if (web.IsDisposed) return;
        var life = (SafePopups)web.LifeSpanHandler;
        life.ClosePopups();
        web.GetBrowserHost().CloseBrowser(true);
        await life.OwnerCloseReady.WaitAsync(TimeSpan.FromSeconds(10));
        var release = ((BrowserView)web).ReleaseNativeHostAsync();
        Check(ReferenceEquals(release, ((BrowserView)web).ReleaseNativeHostAsync()), "duplicate host release shares one operation");
        await release;
        Check(life.OwnerClosed.IsCompletedSuccessfully && !web.IsHandleCreated, "native browser closed before adapter disposal");
        web.Dispose();
    }

    private static async Task Storage(Form host, string root, string phase)
    {
        CefSharp.Handler.RequestContextHandler Handler() => new CefSharp.Handler.RequestContextHandler().OnInitialize(context =>
        {
            Console.WriteLine("Context initialized: " + context.CachePath);
            if (!context.RegisterSchemeHandlerFactory("https", "browser-tests.invalid", new Fixture())
                || !context.RegisterSchemeHandlerFactory("https", "provider-tests.invalid", new Fixture()))
                throw new InvalidOperationException("Fixture scheme registration failed");
        });
        using var a = new RequestContext(new RequestContextSettings { CachePath = Path.Combine(root, "profile-a") }, Handler());
        using var b = new RequestContext(new RequestContextSettings { CachePath = Path.Combine(root, "profile-b") }, Handler());
        using var first = new BrowserView(Origin, requestContext: a) { LifeSpanHandler = new CefSharp.Handler.LifeSpanHandler(), Dock = DockStyle.Left, Width = 450 };
        using var second = new BrowserView(Origin, requestContext: b) { LifeSpanHandler = new CefSharp.Handler.LifeSpanHandler(), Dock = DockStyle.Fill };
        var unexpectedHostClose = false;
        FormClosingEventHandler closingHandler = (_, _) => unexpectedHostClose = true;
        host.FormClosing += closingHandler;
        first.RequestHandler = new SafeRequests(Console.WriteLine);
        first.PermissionHandler = new OriginPermissions(Console.WriteLine);
        var popupHandler = new SafePopups(Console.WriteLine);
        first.LifeSpanHandler = popupHandler;
        first.Disposed += (_, _) => popupHandler.ClosePopups();
        foreach (var web in new[] { first, second }) Observe(web);
        host.Controls.Add(second); host.Controls.Add(first);
        await Ready(first); await Ready(second);
        if (phase == "write")
        {
            Check(await Js(first, "document.cookie + localStorage.getItem('marker')") == "null", "fresh profile A");
            Check(await Js(second, "document.cookie + localStorage.getItem('marker')") == "null", "fresh profile B");
            await Js(first, "document.cookie='marker=A; Max-Age=86400; Secure; SameSite=Lax; Path=/'; localStorage.setItem('marker','A'); 'ok'");
            Check(await Js(second, "document.cookie + localStorage.getItem('marker')") == "null", "actual CEF cookie and localStorage isolation");
            await Js(second, "document.cookie='marker=B; Max-Age=86400; Secure; SameSite=Lax; Path=/'; localStorage.setItem('marker','B'); 'ok'");
            using var same = new BrowserView(Origin, requestContext: a) { LifeSpanHandler = new CefSharp.Handler.LifeSpanHandler(), Dock = DockStyle.Bottom, Height = 150 };
            Observe(same); host.Controls.Add(same); await Ready(same);
            Check(await Js(same, "document.cookie + '|' + localStorage.getItem('marker')") == "marker=A|A", "same-profile browser shares storage");
            // User activation is injected through CDP only in this test process. Production has no remote-debugging port.
            await Js(first, "window.oauthDone=false; window.addEventListener('message', e => { if(e.origin===location.origin && e.data==='oauth-ok') window.oauthDone=true; }); 'ok'");
            await Cef.UIThreadTaskFactory.StartNew(() => first.GetBrowserHost().ExecuteDevToolsMethod(0, "Runtime.evaluate",
                new Dictionary<string, object> { ["expression"] = "window.open('https://provider-tests.invalid/authorize','oauth')", ["userGesture"] = true }));
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (await Js(first, "window.oauthDone") != "True")
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("OAuth opener/postMessage round trip");
                await Task.Delay(100);
            }
            Check(true, "native popup preserves opener across provider redirect and postMessage");
            Check(await Js(first, "window.open('about:blank') === null") == "True", "automatic popup denied");
            using var cookiesA = a.GetCookieManager(null);
            using var cookiesB = b.GetCookieManager(null);
            using var flushA = new Completion(); using var flushB = new Completion();
            if (!cookiesA.FlushStore(flushA) || !cookiesB.FlushStore(flushB)) throw new Exception("FlushStore refused");
            await Task.WhenAll(flushA.Done.Task, flushB.Done.Task).WaitAsync(TimeSpan.FromSeconds(10));
            await Wait(() => popupHandler.PopupCount == 0, "OAuth popup self-close completes");
            await CloseWeb(same);
            // Drain both message loops; a queued WM_CLOSE must not escape detection.
            await Cef.UIThreadTaskFactory.StartNew(() => { });
            await Js(second, "document.readyState");
            Check(!unexpectedHostClose && !host.IsDisposed && !second.IsDisposed, "closing one browser preserves its host and sibling");
        }
        Check(await Js(first, "document.cookie + '|' + localStorage.getItem('marker')") == "marker=A|A", phase + " profile A persistence");
        Check(await Js(second, "document.cookie + '|' + localStorage.getItem('marker')") == "marker=B|B", phase + " profile B persistence");
        await CloseWeb(first);
        Check(await Js(second, "document.cookie + '|' + localStorage.getItem('marker')") == "marker=B|B", "sibling storage survives owner disposal");
        await CloseWeb(second);
        Check(!unexpectedHostClose && !host.IsDisposed, "closing all embedded browsers preserves application host");
        host.FormClosing -= closingHandler;
        host.Controls.Remove(first); host.Controls.Remove(second);
    }

    private static async Task RejectUntrustedTls(Form host)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // Schannel on .NET 8 cannot use CreateSelfSigned's ephemeral key.
        // Import a temporary user key container; do not add trust to any store.
        using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx),
            (string?)null, X509KeyStorageFlags.UserKeySet);
        using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serving = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var connection = await server.AcceptTcpClientAsync(stop.Token);
                    using var stream = new SslStream(connection.GetStream());
                    try
                    {
                        await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, stop.Token);
                        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 9\r\nConnection: close\r\n\r\nuntrusted"), stop.Token);
                    }
                    catch (AuthenticationException ex) { Console.WriteLine("TLS fixture handshake: " + ex); }
                    catch (IOException ex) { Console.WriteLine("TLS fixture transport: " + ex.Message); }
                }
            }
            catch (OperationCanceledException) { }
        });
        using var web = new BrowserView("about:blank") { LifeSpanHandler = new CefSharp.Handler.LifeSpanHandler(), Dock = DockStyle.Fill };
        var failure = new TaskCompletionSource<CefErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        web.RequestHandler = new SafeRequests(Console.WriteLine);
        Observe(web);
        web.LoadError += (_, e) => { if (e.Frame.IsMain && e.FailedUrl.StartsWith("https://localhost:")) failure.TrySetResult(e.ErrorCode); };
        host.Controls.Add(web);
        try
        {
            await Ready(web);
            web.Load($"https://localhost:{port}/");
            var code = await failure.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Check(code.ToString().StartsWith("Cert", StringComparison.Ordinal), "untrusted TLS certificate rejected by production handler: " + code);
        }
        finally { stop.Cancel(); await serving; await CloseWeb(web); host.Controls.Remove(web); }
    }

    private static async Task TabsAndRenderer()
    {
        using var form = new MainForm("integration");
        form.Show();
        var tabs = form.Controls.OfType<TabControl>().Single();
        var toolbar = form.Controls.OfType<TableLayoutPanel>().Single();
        var status = form.Controls.OfType<StatusStrip>().Single().Items.OfType<ToolStripStatusLabel>().Last();
        Button Button(string label) => toolbar.Controls.OfType<Button>().Single(b => b.Text == label);
        ChromiumWebBrowser Current() => tabs.SelectedTab!.Controls.OfType<ChromiumWebBrowser>().Single();
        await Wait(() => tabs.TabCount == 1, "initial tab"); await Ready(Current());
        Check(await Js(Current(), "document.querySelector('#result').textContent.includes('userAgent')") == "True", "embedded diagnostics renders in CEF");
        var navigating = Current();
        await LoadReady(navigating, Origin + "one");
        await LoadReady(navigating, Origin + "two");
        Button("←").PerformClick(); await Wait(() => navigating.Address == Origin + "one" && !navigating.IsLoading, "back navigation");
        Button("→").PerformClick(); await Wait(() => navigating.Address == Origin + "two" && !navigating.IsLoading, "forward navigation");
        Check(true, "back and forward navigation buttons");
        var address = toolbar.Controls.OfType<TextBox>().Single();
        address.Focus(); address.Text = "editing.example/path";
        await ReloadReady(navigating);
        Check(address.Text == "editing.example/path", "loading does not overwrite an address being typed");
        var previousUrl = navigating.Address;
        address.Text = "news";
        await Js(navigating, "document.readyState");
        Check(navigating.Address == previousUrl, "typing does not navigate or search");
        Check(form.SubmitAddress(address.Text), "omnibox submission accepted");
        await Wait(() => navigating.Address == SearchEngines.Get("duckduckgo").Search("news") && !navigating.IsLoading, "default search navigation");
        await Ready(navigating);
        Check(await Js(navigating, "location.hostname") == "duckduckgo.com", "search loads selected provider in current tab");
        Button("←").PerformClick(); await Wait(() => navigating.Address == previousUrl && !navigating.IsLoading, "back from search");
        Button("→").PerformClick(); await Wait(() => navigating.Address.StartsWith("https://duckduckgo.com/") && !navigating.IsLoading, "forward to search");
        Check(tabs.TabCount == 1, "search preserves tab count and history");
        Button("Search").PerformClick();
        var searchForm = form.OwnedForms.OfType<SearchSettingsForm>().Single();
        var picker = searchForm.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<ComboBox>().Single();
        picker.SelectedItem = SearchEngines.Get("google"); searchForm.SaveSelection(); searchForm.Close();
        Check(form.SearchEngineId == "google" && form.SubmitAddress("日本語 + café"), "selected search engine applies immediately");
        await Wait(() => navigating.Address == SearchEngines.Get("google").Search("日本語 + café") && !navigating.IsLoading, "encoded Google query");
        await Ready(navigating);
        Check(await Js(navigating, "new URL(location.href).searchParams.get('q')") == "日本語 + café", "search query reaches renderer without double encoding");
        Check(!form.SubmitAddress("javascript:alert(1)"), "omnibox rejects script schemes");
        await LoadReady(navigating, BrowserPolicy.DiagnosticsUrl);
        for (var i = 0; i < 5; i++) { Button("+").PerformClick(); Button("×").PerformClick(); }
        await Wait(() => tabs.TabCount == 1, "rapid native tab closure");
        Check(tabs.TabCount == 1, "rapid tab creation and disposal during loading");
        Button("+").PerformClick(); await Ready(Current());
        var damaged = Current();
        await Cef.UIThreadTaskFactory.StartNew(() => damaged.GetBrowserHost().ExecuteDevToolsMethod(0, "Page.crash", new Dictionary<string, object>()));
        await Wait(() => status.Text?.Contains("Renderer stopped") == true, "renderer termination diagnostic");
        Check(true, "renderer crash reports recoverable per-tab diagnostic");
        await ReloadReady(damaged);
        Check(await Js(damaged, "document.title") == "Aegis · Local diagnostics", "renderer reload recovery");
        tabs.SelectedIndex = 0; await ReloadReady(Current());
        Check(await Js(Current(), "document.title") == "Aegis · Local diagnostics", "remaining tab can recover even if Chromium shared its renderer");
        var owner = Current();
        var ownerPopups = (SafePopups)owner.LifeSpanHandler;
        await Cef.UIThreadTaskFactory.StartNew(() => owner.GetBrowserHost().ExecuteDevToolsMethod(0, "Runtime.evaluate",
            new Dictionary<string, object> { ["expression"] = "window.blankPopup=window.open('about:blank','blank'); window.blankOpener=!!window.blankPopup && window.blankPopup.opener===window;", ["userGesture"] = true }));
        await Wait(() => ownerPopups.PopupCount == 1, "blank popup created");
        Check(await Js(owner, "window.blankOpener") == "True", "user-initiated about:blank preserves opener");
        Button("×").PerformClick();
        Button("×").PerformClick(); // Idempotent while the selected tab is retiring.
        await Wait(() => ownerPopups.PopupCount == 0, "owner tab closes its native popup");
        await Wait(() => tabs.TabCount == 1, "owner tab removed after native close readiness");
        Check(true, "popup lifecycle ends with owner tab");
        var last = Current();
        Button("×").PerformClick();
        await Wait(() => tabs.TabCount == 1 && Current() != last, "last tab replacement created");
        await Ready(Current()); Check(tabs.TabCount == 1, "last-tab replacement");
        for (var i = 0; i < 4; i++) Button("+").PerformClick();
        form.Close();
        form.Close(); // Repeated window-close requests during tab initialization.
        await Wait(() => form.IsDisposed, "asynchronous window shutdown");
        Check(true, "window closes after tab close readiness");
    }

    private sealed class Completion : ICompletionCallback
    {
        public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsDisposed { get; private set; }
        public void OnComplete() => Done.TrySetResult(true);
        public void Dispose() => IsDisposed = true;
    }

    private sealed class Fixture : ISchemeHandlerFactory
    {
        public IResourceHandler Create(IBrowser browser, IFrame frame, string schemeName, IRequest request)
        {
            var uri = new Uri(request.Url);
            var script = uri.Host == "provider-tests.invalid"
                ? "location.replace('https://browser-tests.invalid/callback');"
                : uri.AbsolutePath == "/callback" ? "window.opener.postMessage('oauth-ok','https://browser-tests.invalid');window.close();" : "";
            return ResourceHandler.FromString("<!doctype html><title>CEF storage fixture</title><script>" + script + "</script><p>Local integration fixture</p>", mimeType: "text/html");
        }
    }
}
