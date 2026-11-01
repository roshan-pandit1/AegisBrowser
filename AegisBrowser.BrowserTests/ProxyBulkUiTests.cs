using AegisBrowser;
using AegisBrowser.Core;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    private static async Task ProxyBulkUi(string root, ProfileRepository profiles, Form owner)
    {
        T Control<T>(Control parent, string name) where T : Control => parent.Controls.Find(name, true).OfType<T>().Single();
        Check(Control<Button>(owner, "BulkProxyDrafts").AccessibleName == "Edit selected proxy drafts",
            "dashboard exposes accessible bulk proxy drafts");
        var first = profiles.Create("bulk-proxy-a"); var second = profiles.Create("bulk-proxy-b");
        var protectedProfile = profiles.Create("bulk-proxy-protected");
        var repository = new ProxyRepository(root); var vault = new ProxyCredentialVault(root);
        var editor = new ProxyConfigurationEditor(root, repository, vault); var service = new ProxyBulkDrafts(editor);
        var authenticated = new ProxyDraft(ProxyProtocol.Http, "private-proxy.example", 8080, true, null, "", true);
        var protectedConfig = editor.Save(protectedProfile, authenticated, 0, new ProxyCredentials("bulk-ui-user", "bulk-ui-password")).Configuration;
        using var form = new ProxyBulkDraftForm(service, [first, second, protectedProfile], _ => true);
        form.Show(owner);
        var output = Control<TextBox>(form, "BulkProxyResults");
        await Wait(() => Control<Button>(form, "ApplyBulkProxy").Enabled, "bulk proxy preview");
        Check(output.Text.Contains("Contains credentials", StringComparison.Ordinal), "bulk preview identifies credential-bearing profiles");
        Control<Button>(form, "ApplyBulkProxy").PerformClick();
        await Wait(() => output.Text.Contains("Could not apply", StringComparison.Ordinal), "invalid bulk proxy feedback");
        Check(editor.Load(first) is null && editor.Load(second) is null, "invalid bulk draft leaves all profiles unchanged");
        Control<TextBox>(form, "BulkProxyHost").Text = "shared-proxy.example";
        Control<NumericUpDown>(form, "BulkProxyPort").Value = 8080;
        Control<Button>(form, "ApplyBulkProxy").PerformClick();
        await Wait(() => output.Text.Contains("bulk-proxy-a — Succeeded", StringComparison.Ordinal) &&
            output.Text.Contains("bulk-proxy-b — Succeeded", StringComparison.Ordinal) &&
            output.Text.Contains("bulk-proxy-protected — Failed", StringComparison.Ordinal), "bulk proxy results");
        Check(editor.Load(first)?.Settings.Protocol == ProxyProtocol.Http && editor.Load(second)?.Settings.Protocol == ProxyProtocol.Http &&
            editor.Load(protectedProfile)?.Settings.CredentialId == protectedConfig.Settings.CredentialId,
            "bulk drafts save independently and preserve protected credentials");
        Control<Button>(form, "PreviewBulkProxy").PerformClick();
        await Wait(() => Control<Button>(form, "ApplyBulkProxy").Enabled && output.Text.Contains("bulk-proxy-a", StringComparison.Ordinal), "bulk proxy re-preview");
        Control<ComboBox>(form, "BulkProxyMode").SelectedIndex = 3;
        Control<Button>(form, "ApplyBulkProxy").PerformClick();
        await Wait(() => output.Text.Contains("bulk-proxy-a — Succeeded", StringComparison.Ordinal) &&
            output.Text.Contains("bulk-proxy-protected — Failed", StringComparison.Ordinal), "bulk proxy removal results");
        Check(editor.Load(first)?.Settings.Protocol == ProxyProtocol.Direct && editor.Load(second)?.Settings.Protocol == ProxyProtocol.Direct &&
            vault.Read(protectedProfile.Id, protectedConfig.Settings.CredentialId!.Value).Username == "bulk-ui-user",
            "bulk removal restores browser defaults while preserving credentials");
        form.Close();
    }
}
