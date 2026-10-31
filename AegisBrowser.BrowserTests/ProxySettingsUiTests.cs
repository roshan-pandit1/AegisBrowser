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
        using (var lease = new ProfileLease(BrowserPolicy.ProfilePath(root, profile.Slug)))
        {
            var blocked = false;
            try { proxyRepository.EnsureRuntimeReady(profile, lease); } catch (IOException) { blocked = true; }
            Check(blocked, "saved proxy draft blocks unsupported browser startup");
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
    }
}
