using AegisBrowser;
using AegisBrowser.Core;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    private static async Task ProxySettingsUi(string root, ProfileRepository profiles, Form owner)
    {
        T Control<T>(Control parent, string name) where T : Control => parent.Controls.Find(name, true).OfType<T>().Single();
        Check(Control<Button>(owner, "EditProxyDraft").AccessibleName == "Edit saved proxy draft", "dashboard exposes an accessible proxy-draft action");
        var profile = profiles.Create("ui-proxy");
        var proxyRepository = new ProxyRepository(root);
        var editor = new ProxyConfigurationEditor(root, proxyRepository, new ProxyCredentialVault(root));
        using (var declined = new ProxySettingsForm(editor, profile, null, _ => false))
        {
            declined.Show(owner);
            Control<ComboBox>(declined, "ProxyProtocol").SelectedIndex = (int)ProxyProtocol.Http;
            Control<TextBox>(declined, "ProxyHost").Text = "proxy.example";
            Control<Button>(declined, "SaveProxyDraft").PerformClick();
            Check(declined.Visible && editor.Load(profile) is null, "declining a proxy draft leaves the profile unchanged");
            Control<ComboBox>(declined, "ProxyProtocol").SelectedIndex = (int)ProxyProtocol.Socks5;
            Control<CheckBox>(declined, "ProxyAuthentication").Checked = true;
            Control<TextBox>(declined, "ProxyUsername").Text = "ui-fixture-user";
            Control<TextBox>(declined, "ProxyPassword").Text = "ui-fixture-password";
            Control<Button>(declined, "SaveProxyDraft").PerformClick();
            Check(Control<Label>(declined, "ProxyFeedback").Text.Contains("SOCKS5", StringComparison.Ordinal) && editor.Load(profile) is null,
                "unsupported SOCKS5 authentication is not saved");
            declined.Close();
        }
        using (var form = new ProxySettingsForm(editor, profile, null, _ => true))
        {
            form.Show(owner);
            Check(Control<Label>(form, "ProxyAvailability").Text.Contains("blocks this profile", StringComparison.OrdinalIgnoreCase) &&
                Control<TextBox>(form, "ProxyPassword").UseSystemPasswordChar, "proxy editor labels inactive drafts and masks the password");
            Control<ComboBox>(form, "ProxyProtocol").SelectedIndex = (int)ProxyProtocol.Http;
            Control<TextBox>(form, "ProxyHost").Text = "proxy.example";
            Control<NumericUpDown>(form, "ProxyPort").Value = 8080;
            Control<CheckBox>(form, "ProxyAuthentication").Checked = true;
            Control<TextBox>(form, "ProxyUsername").Text = "ui-fixture-user";
            Control<TextBox>(form, "ProxyPassword").Text = "ui-fixture-password";
            Control<Button>(form, "SaveProxyDraft").PerformClick();
            await Wait(() => form.IsDisposed || !form.Visible, "proxy draft save");
        }
        var saved = editor.Load(profile) ?? throw new Exception("Proxy draft did not persist.");
        var secret = saved.Settings.CredentialId ?? throw new Exception("Proxy credential reference is missing.");
        Check(saved.Settings.Protocol == ProxyProtocol.Http && saved.Settings.AuthenticationRequired,
            "native proxy editor saves a protected, profile-scoped draft");
        var grid = Control<DataGridView>(owner, "ProfileGrid");
        await Wait(() => grid.Rows.Cast<DataGridViewRow>().Any(row => (row.Tag as DashboardProfile)?.Slug == profile.Slug &&
            (row.Tag as DashboardProfile)?.ProxyState == SavedProxyState.RequiredDraft), "dashboard proxy draft status");
        var blockedRow = grid.Rows.Cast<DataGridViewRow>().Single(row => (row.Tag as DashboardProfile)?.Slug == profile.Slug);
        Check((blockedRow.Cells["SavedProxy"].Value as string)?.Contains("blocked", StringComparison.OrdinalIgnoreCase) == true &&
            (blockedRow.Tag as DashboardProfile)?.CanLaunch == false, "dashboard renders the blocked proxy draft without claiming an active route");
        using (var lease = new ProfileLease(BrowserPolicy.ProfilePath(root, profile.Slug)))
        {
            var blocked = false;
            try { proxyRepository.EnsureRuntimeReady(profile, lease); } catch (IOException) { blocked = true; }
            Check(blocked, "saved proxy draft blocks unsupported browser startup");
        }
        await ProxyConfigurationTransferUi(root, profiles, owner, editor, proxyRepository, profile, saved, secret);
        var diagnostics = new FakeProxySavedDiagnostics();
        using (var form = new ProxySettingsForm(editor, profile, saved, _ => true, diagnostics))
        {
            form.Show(owner);
            Check(Control<Label>(form, "ProxyTestWarning").Text.Contains(".NET", StringComparison.Ordinal) &&
                Control<Label>(form, "ProxyTestWarning").Text.Contains("does not test Chromium", StringComparison.Ordinal),
                "proxy test explains the transport and browser limitations");
            var endpoint = Control<TextBox>(form, "ProxyDiagnosticEndpoint");
            var test = Control<Button>(form, "TestSavedProxy");
            endpoint.Text = "http://example.invalid/ip";
            test.PerformClick();
            Check(diagnostics.Calls == 0 && Control<Label>(form, "ProxyTestResult").Text.Contains("HTTPS", StringComparison.Ordinal),
                "invalid endpoint does not start a diagnostic");
            Control<TextBox>(form, "ProxyHost").Text = "unsaved.example";
            endpoint.Text = "https://example.invalid/ip";
            test.PerformClick();
            await Wait(() => test.Enabled && Control<Label>(form, "ProxyTestResult").Text.Contains("203.0.113.42", StringComparison.Ordinal), "saved proxy preflight result");
            Check(diagnostics.Calls == 1 && diagnostics.LastConfiguration == saved && diagnostics.LastEndpoint == endpoint.Text,
                "the diagnostic uses the saved revision only after an explicit action");
            diagnostics.WaitForCancellation = true;
            test.PerformClick();
            await Wait(() => diagnostics.Calls == 2 && Control<Button>(form, "CancelProxyTest").Enabled, "proxy diagnostic starts");
            Control<Button>(form, "CancelProxyTest").PerformClick();
            await Wait(() => test.Enabled && Control<Label>(form, "ProxyTestResult").Text.Contains("canceled", StringComparison.OrdinalIgnoreCase), "proxy diagnostic cancellation");
            Check(Control<TextBox>(form, "ProxyHost").Text == "unsaved.example", "canceling a diagnostic preserves unsaved editor input");
            form.Close();
        }
        using (var stale = new ProxySettingsForm(editor, profile, saved, _ => true))
        {
            stale.Show(owner);
            var direct = new ProxyDraft(ProxyProtocol.Direct, "", 0, false, null, "", false);
            var removed = editor.Save(profile, direct, saved.Revision, null);
            Check(removed.CleanupWarning is null && !File.Exists(Path.Combine(root, ProxyCredentialVault.DirectoryName,
                profile.Id.ToString("N"), secret.ToString("N") + ".bin")), "explicit browser defaults remove the protected credential");
            Control<TextBox>(stale, "ProxyLabel").Text = "stale draft";
            Control<Button>(stale, "SaveProxyDraft").PerformClick();
            await Wait(() => Control<Label>(stale, "ProxyFeedback").Text.Contains("changed", StringComparison.OrdinalIgnoreCase), "stale proxy edit warning");
            Check(stale.Visible && Control<TextBox>(stale, "ProxyLabel").Text == "stale draft", "stale proxy editor preserves input without overwriting");
            stale.Close();
        }
        using (var lease = new ProfileLease(BrowserPolicy.ProfilePath(root, profile.Slug)))
        {
            proxyRepository.EnsureRuntimeReady(profile, lease);
            Check(proxyRepository.Get(profile, lease)?.Settings.Protocol == ProxyProtocol.Direct, "explicit browser defaults restore startup readiness");
        }
        await Wait(() => grid.Rows.Cast<DataGridViewRow>().Any(row => (row.Tag as DashboardProfile)?.Slug == profile.Slug &&
            (row.Tag as DashboardProfile)?.ProxyState == SavedProxyState.BrowserDefaults), "dashboard browser-default status");
        Check(grid.Rows.Cast<DataGridViewRow>().Single(row => (row.Tag as DashboardProfile)?.Slug == profile.Slug).Cells["SavedProxy"].Value as string == "Browser defaults",
            "dashboard refreshes saved proxy status after draft removal");
    }

    private static async Task ProxyConfigurationTransferUi(string root, ProfileRepository profiles, Form owner,
        ProxyConfigurationEditor editor, ProxyRepository repository, ProfileDefinition source, ProxyConfiguration saved, Guid sourceSecret)
    {
        T Control<T>(Control parent, string name) where T : Control => parent.Controls.Find(name, true).OfType<T>().Single();
        var transfer = new ProxyConfigurationTransfer(root, repository);
        var file = Path.Combine(Path.GetTempPath(), "aegis-ui-proxy-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var form = new ProxySettingsForm(editor, source, saved, _ => true, transfer: transfer,
                chooseExportPath: () => file, chooseImportPath: () => file))
            {
                form.Show(owner);
                Check(Control<Button>(form, "ExportSavedProxy").AccessibleName == "Export saved proxy configuration" &&
                    Control<Button>(form, "ImportProxyConfiguration").AccessibleName == "Preview and load proxy configuration", "proxy configuration transfer controls are accessible");
                Control<Button>(form, "ExportSavedProxy").PerformClick();
                await Wait(() => Control<Label>(form, "ProxyFeedback").Text.Contains("exported", StringComparison.OrdinalIgnoreCase), "saved proxy export");
                var text = File.ReadAllText(file);
                Check(!text.Contains("ui-fixture-user", StringComparison.Ordinal) && !text.Contains("ui-fixture-password", StringComparison.Ordinal) &&
                    !text.Contains(sourceSecret.ToString("D"), StringComparison.OrdinalIgnoreCase), "proxy export contains no source credential or reference");
                Control<Button>(form, "ImportProxyConfiguration").PerformClick();
                await Wait(() => Control<Label>(form, "ProxyFeedback").Text.Contains("loaded for review", StringComparison.OrdinalIgnoreCase), "same-profile proxy import preview");
                Control<Button>(form, "SaveProxyDraft").PerformClick();
                await Wait(() => Control<Label>(form, "ProxyFeedback").Text.Contains("Enter new credentials", StringComparison.OrdinalIgnoreCase), "same-profile imported credentials required");
                Check(editor.Load(source) == saved, "imported settings never silently reuse an existing credential even for the same profile");
                form.Close();
            }
            var target = profiles.Create("ui-proxy-import");
            var allow = false;
            using (var form = new ProxySettingsForm(editor, target, null, _ => allow, transfer: transfer,
                chooseExportPath: () => file, chooseImportPath: () => file))
            {
                form.Show(owner);
                Control<ComboBox>(form, "ProxyProtocol").SelectedIndex = (int)ProxyProtocol.Http;
                Control<TextBox>(form, "ProxyHost").Text = "unsaved.example";
                Control<Button>(form, "ImportProxyConfiguration").PerformClick();
                await Wait(() => Control<Label>(form, "ProxyFeedback").Text.Contains("canceled", StringComparison.OrdinalIgnoreCase), "proxy import declined");
                Check(Control<TextBox>(form, "ProxyHost").Text == "unsaved.example" && editor.Load(target) is null,
                    "declining proxy import leaves editor input and target unchanged");
                allow = true;
                Control<Button>(form, "ImportProxyConfiguration").PerformClick();
                await Wait(() => Control<Label>(form, "ProxyFeedback").Text.Contains("loaded for review", StringComparison.OrdinalIgnoreCase), "proxy import preview accepted");
                Check(Control<CheckBox>(form, "ProxyAuthentication").Checked && Control<TextBox>(form, "ProxyUsername").Text.Length == 0 &&
                    Control<TextBox>(form, "ProxyPassword").Text.Length == 0 && editor.Load(target) is null,
                    "import preserves authentication requirement without applying settings or importing credentials");
                Control<Button>(form, "SaveProxyDraft").PerformClick();
                await Wait(() => Control<Label>(form, "ProxyFeedback").Text.Contains("Enter new credentials", StringComparison.OrdinalIgnoreCase), "imported proxy requires credentials");
                Check(editor.Load(target) is null, "missing imported credentials leave the target configuration unchanged");
                Control<TextBox>(form, "ProxyUsername").Text = "import-fixture-user";
                Control<TextBox>(form, "ProxyPassword").Text = "import-fixture-password";
                Control<Button>(form, "SaveProxyDraft").PerformClick();
                await Wait(() => form.IsDisposed || !form.Visible, "imported proxy saved with new credentials");
            }
            var imported = editor.Load(target) ?? throw new Exception("Imported proxy did not persist.");
            var vault = new ProxyCredentialVault(root);
            Check(imported.Settings.AuthenticationRequired && imported.Settings.CredentialId is Guid importedSecret && importedSecret != sourceSecret &&
                vault.Read(target.Id, importedSecret).Username == "import-fixture-user" && vault.Read(source.Id, sourceSecret).Username == "ui-fixture-user",
                "import creates a separate protected credential without sharing the source secret");
            using (var lease = new ProfileLease(BrowserPolicy.ProfilePath(root, target.Slug)))
            {
                var blocked = false;
                try { repository.EnsureRuntimeReady(target, lease); } catch (IOException) { blocked = true; }
                Check(blocked, "imported required proxy remains blocked before Chromium startup");
            }
            _ = editor.Save(target, new ProxyDraft(ProxyProtocol.Direct, "", 0, false, null, "", false), imported.Revision, null);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }

    private sealed class FakeProxySavedDiagnostics : IProxySavedDiagnostics
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public bool WaitForCancellation { get; set; }
        public ProxyConfiguration? LastConfiguration { get; private set; }
        public string? LastEndpoint { get; private set; }
        public async Task<ProxyTransportResult> TestAsync(ProfileDefinition profile, ProxyConfiguration expected, string endpoint, CancellationToken cancellation)
        {
            LastConfiguration = expected; LastEndpoint = endpoint;
            Interlocked.Increment(ref calls);
            if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellation);
            return new(expected.Settings.Protocol, ProxyTransportOutcome.Connected, DateTimeOffset.UtcNow,
                TimeSpan.FromMilliseconds(1), "203.0.113.42", "Fixture result.");
        }
    }
}
