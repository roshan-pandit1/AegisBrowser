using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AegisBrowser.WebView2Feasibility;

// Source-only experiment. No production browser integration or network access
// is authorized by this file; see README before any build or execution.
internal sealed class PrototypeForm : Form
{
    private const string LocalPage = "<!doctype html><title>Local fixture</title><p>Local prototype fixture</p>";
    private readonly WebView2[] views = { new(), new() };
    private readonly Label status = new() { Dock = DockStyle.Bottom, AutoSize = true, Text = "Not initialized" };
    private bool closing;

    internal PrototypeForm()
    {
        Text = "WebView2 feasibility — local fixture";
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        var local = new Button { Text = "Local fixture", AutoSize = true };
        var storage = new Button { Text = "Synthetic storage check", AutoSize = true };
        local.Click += (_, _) =>
        {
            if (tabs.SelectedIndex >= 0) views[tabs.SelectedIndex].CoreWebView2?.NavigateToString(LocalPage);
        };
        storage.Click += async (_, _) => await CheckSyntheticStorageAsync();
        toolbar.Controls.Add(local);
        toolbar.Controls.Add(storage);
        for (var i = 0; i < views.Length; i++)
        {
            var tab = new TabPage(i == 0 ? "Profile A" : "Profile B");
            views[i].Dock = DockStyle.Fill;
            tab.Controls.Add(views[i]);
            tabs.TabPages.Add(tab);
        }
        Controls.Add(tabs);
        Controls.Add(status);
        Controls.Add(toolbar);
        Shown += async (_, _) => await InitializeAsync();
        FormClosing += (_, _) => { closing = true; foreach (var view in views) view.Dispose(); };
    }

    private async Task InitializeAsync()
    {
        try
        {
            string runtimeVersion = "unknown";
            for (var i = 0; i < views.Length && !closing; i++)
            {
                // Separate disposable data folders beside the experimental output.
                var profile = Path.Combine(AppContext.BaseDirectory, "disposable-profiles", i == 0 ? "a" : "b");
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
                runtimeVersion = environment.BrowserVersionString;
                if (closing) return;
                await views[i].EnsureCoreWebView2Async(environment);
                if (closing) return;
                var browser = views[i].CoreWebView2;
                browser.DownloadStarting += (_, e) => e.Cancel = true;
                browser.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                browser.NewWindowRequested += (_, e) => e.Handled = true;
                browser.NavigationStarting += (_, e) =>
                {
                    if (!string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
                };
                browser.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                browser.WebResourceRequested += (_, e) =>
                {
                    // Deny all resource requests; never log the request URL or body.
                    e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                };
                browser.NavigateToString(LocalPage);
            }
            if (!closing) status.Text = $"Runtime {runtimeVersion}; browser processes {views[0].CoreWebView2.BrowserProcessId}, {views[1].CoreWebView2.BrowserProcessId}; behavior not yet qualified.";
        }
        catch (Exception error)
        {
            if (!closing && !IsDisposed) status.Text = $"Initialization failed (0x{error.HResult:X8}).";
        }
    }

    private async Task CheckSyntheticStorageAsync()
    {
        if (closing || views[0].CoreWebView2 is null || views[1].CoreWebView2 is null) return;
        try
        {
            for (var i = 0; i < views.Length && !closing; i++)
            {
                var browser = views[i].CoreWebView2;
                var manager = browser.CookieManager;
                var marker = i == 0 ? "alpha" : "beta";
                manager.AddOrUpdateCookie(manager.CreateCookie("aegis-fixture", marker, "aegis.invalid", "/"));
                // This exercises an API only; about:blank storage behavior is unverified.
                await browser.ExecuteScriptAsync(i == 0
                    ? "localStorage.setItem('aegis-fixture','alpha'); true;"
                    : "localStorage.setItem('aegis-fixture','beta'); true;");
            }
            for (var i = 0; i < views.Length && !closing; i++)
            {
                var browser = views[i].CoreWebView2;
                var marker = i == 0 ? "alpha" : "beta";
                // The URL selects a cookie store; it is not navigated to or logged.
                var cookies = await browser.CookieManager.GetCookiesAsync("https://aegis.invalid/");
                var found = false;
                foreach (var cookie in cookies)
                    if (cookie.Name == "aegis-fixture" && cookie.Value == marker) found = true;
                var stored = await browser.ExecuteScriptAsync("localStorage.getItem('aegis-fixture')");
                if (!found || stored != "\"" + marker + "\"") throw new InvalidOperationException();
            }
            if (!closing) status.Text = "Synthetic checks completed; restart and OS isolation not established.";
        }
        catch (Exception error)
        {
            if (!closing && !IsDisposed) status.Text = $"Synthetic storage check failed (0x{error.HResult:X8}).";
        }
    }
}
