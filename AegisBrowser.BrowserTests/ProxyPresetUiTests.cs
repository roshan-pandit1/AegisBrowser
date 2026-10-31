using AegisBrowser;
using AegisBrowser.Core;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    private static async Task ProxyPresetUi(string root, ProfileRepository profiles, Form owner)
    {
        T Control<T>(Control parent, string name) where T : Control => parent.Controls.Find(name, true).OfType<T>().Single();
        Check(Control<Button>(owner, "ManageProxyPresets").AccessibleName == "Manage proxy presets", "dashboard exposes accessible proxy preset management");
        var presets = new ProxyPresetRepository(root);
        using (var form = new ProxyPresetsForm(presets, false, _ => true))
        {
            form.Show(owner);
            await Wait(() => Control<Label>(form, "ProxyPresetFeedback").Text.Contains("Select or create", StringComparison.Ordinal), "proxy presets loaded");
            Check(Control<Label>(form, "ProxyPresetWarning").Text.Contains("do not activate", StringComparison.Ordinal) &&
                Control<Label>(form, "ProxyPresetWarning").Text.Contains("unencrypted", StringComparison.Ordinal), "preset manager labels saved settings and export privacy accurately");
            Control<TextBox>(form, "ProxyPresetName").Text = "QA endpoint";
            Control<TextBox>(form, "ProxyPresetDescription").Text = "Controlled test";
            Control<TextBox>(form, "ProxyPresetHost").Text = "proxy.example";
            Control<NumericUpDown>(form, "ProxyPresetPort").Value = 8080;
            Control<Button>(form, "SaveProxyPreset").PerformClick();
            await Wait(() => Control<Label>(form, "ProxyPresetFeedback").Text.Contains("Preset saved", StringComparison.Ordinal), "proxy preset saved");
            var original = presets.List().Single();
            var list = Control<ListBox>(form, "ProxyPresetList");
            var search = Control<TextBox>(form, "ProxyPresetSearch");
            search.Text = "not found"; Check(list.Items.Count == 0, "proxy preset search filters locally");
            search.Clear(); list.SelectedIndex = 0;
            Control<Button>(form, "EditProxyPreset").PerformClick();
            _ = presets.Update(original with { Description = "Changed elsewhere" }, original.Revision);
            Control<TextBox>(form, "ProxyPresetName").Text = "Unsaved local name";
            Control<Button>(form, "SaveProxyPreset").PerformClick();
            await Wait(() => Control<Label>(form, "ProxyPresetFeedback").Text.Contains("changed", StringComparison.OrdinalIgnoreCase), "stale preset warning");
            Check(Control<TextBox>(form, "ProxyPresetName").Text == "Unsaved local name" && presets.List().Single().Name == original.Name,
                "stale proxy preset edit preserves input without overwriting");
            Control<Button>(form, "RefreshProxyPresets").PerformClick();
            await Wait(() => Control<Label>(form, "ProxyPresetFeedback").Text.Contains("refreshed", StringComparison.OrdinalIgnoreCase), "proxy presets refreshed");
            list.SelectedIndex = 0; Control<Button>(form, "DuplicateProxyPreset").PerformClick();
            Control<Button>(form, "SaveProxyPreset").PerformClick();
            await Wait(() => Control<Label>(form, "ProxyPresetFeedback").Text.Contains("Preset saved", StringComparison.Ordinal), "proxy preset duplicated");
            Check(presets.List().Count == 2 && presets.List().Select(item => item.Id).Distinct().Count() == 2, "duplicate preset has a new identity");
            var index = Enumerable.Range(0, list.Items.Count).Single(i => list.Items[i]!.ToString()!.StartsWith("QA endpoint copy", StringComparison.Ordinal));
            list.SelectedIndex = index; Control<Button>(form, "DeleteProxyPreset").PerformClick();
            await Wait(() => Control<Label>(form, "ProxyPresetFeedback").Text.Contains("deleted", StringComparison.OrdinalIgnoreCase), "proxy preset deleted");
            Check(presets.List().Count == 1, "deleting a preset preserves the original");
            form.Close();
        }
        ProxyPreset chosen;
        using (var chooser = new ProxyPresetsForm(presets, true, _ => true))
        {
            chooser.Show(owner);
            await Wait(() => Control<ListBox>(chooser, "ProxyPresetList").Items.Count == 1, "proxy preset chooser loaded");
            Control<ListBox>(chooser, "ProxyPresetList").SelectedIndex = 0;
            Control<Button>(chooser, "UseProxyPreset").PerformClick();
            await Wait(() => chooser.IsDisposed || !chooser.Visible, "proxy preset chosen");
            chosen = chooser.Chosen ?? throw new Exception("Preset chooser did not return a preset.");
        }
        var target = profiles.Create("preset-bulk-target");
        var editor = new ProxyConfigurationEditor(root, new ProxyRepository(root), new ProxyCredentialVault(root));
        using (var bulk = new ProxyBulkDraftForm(new ProxyBulkDrafts(editor), [target], _ => true, presets))
        {
            bulk.Show(owner);
            await Wait(() => Control<Button>(bulk, "ApplyBulkProxy").Enabled, "preset bulk preview");
            bulk.CopyPreset(chosen);
            Check(Control<TextBox>(bulk, "BulkProxyHost").Text == chosen.Settings.Host &&
                Control<Label>(bulk, "BulkProxyPresetStatus").Text.Contains("not linked", StringComparison.Ordinal), "bulk editor copies visible preset values without linking");
            Control<Button>(bulk, "ApplyBulkProxy").PerformClick();
            await Wait(() => Control<TextBox>(bulk, "BulkProxyResults").Text.Contains("Succeeded", StringComparison.Ordinal), "preset bulk apply");
            Check(editor.Load(target)?.Settings == chosen.Settings, "preset values create a non-secret blocked proxy draft");
            bulk.Close();
        }
        var transferRoot = Path.Combine(root, "ui-preset-transfer");
        var transferRepo = new ProxyPresetRepository(transferRoot);
        var transfer = new ProxyPresetTransfer(transferRoot, transferRepo);
        var source = transferRepo.Create("Transfer fixture", "Non-secret test", new ProxyDraft(ProxyProtocol.Http, "proxy.example", 8080, false, null, "", true));
        var exportPath = Path.Combine(Path.GetTempPath(), "aegis-ui-preset-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var allowImport = false; var confirmations = 0;
            using var form = new ProxyPresetsForm(transferRepo, false, _ => { confirmations++; return allowImport; },
                transfer, () => exportPath, () => exportPath);
            form.Show(owner);
            await Wait(() => Control<ListBox>(form, "ProxyPresetList").Items.Count == 1 &&
                Control<Button>(form, "ExportProxyPreset").Enabled, "transfer preset loaded");
            Control<ListBox>(form, "ProxyPresetList").SelectedIndex = 0;
            Check(Control<Button>(form, "ExportProxyPreset").AccessibleName == "Export selected proxy preset" &&
                Control<Button>(form, "ImportProxyPreset").AccessibleName == "Preview and import a proxy preset", "preset transfer controls are accessible");
            Control<Button>(form, "ExportProxyPreset").PerformClick();
            await Wait(() => Control<Label>(form, "ProxyPresetFeedback").Text.Contains("exported", StringComparison.OrdinalIgnoreCase), "preset export completes");
            Check(File.Exists(exportPath), "preset export produces a new file");
            transferRepo.Delete(source.Id, source.Revision);
            Control<Button>(form, "ImportProxyPreset").PerformClick();
            await Wait(() => Control<Label>(form, "ProxyPresetFeedback").Text.Contains("canceled", StringComparison.OrdinalIgnoreCase), "preset import declined");
            Check(confirmations == 1 && transferRepo.List().Count == 0, "declining the preview does not import a preset");
            allowImport = true;
            Control<Button>(form, "ImportProxyPreset").PerformClick();
            await Wait(() => Control<Label>(form, "ProxyPresetFeedback").Text.Contains("imported", StringComparison.OrdinalIgnoreCase), "preset import completes");
            Check(confirmations == 2 && transferRepo.List().Single().Id != source.Id, "confirmed import creates a new preset identity");
            form.Close();
        }
        finally { if (File.Exists(exportPath)) File.Delete(exportPath); }
    }
}
