using System;
using System.Collections.Generic;
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
    private readonly Button local = new() { Text = "Local fixture", AutoSize = true };
    private readonly Button storage = new() { Text = "Synthetic storage check", AutoSize = true };
    private readonly LifecycleCoordinator lifecycle = new();
    private readonly ProfileLaunchConfig configuration;
    private readonly string runDirectory;
    private readonly NavigationAttempt?[] navigations = new NavigationAttempt?[2];
    private bool closing;
    private bool closeReady;
    private bool closeWaitInProgress;
    private string? disposalFailureMessage;

    internal PrototypeForm(ProfileLaunchConfig configuration)
    {
        this.configuration = configuration;
        runDirectory = Path.Combine(configuration.ProfileRoot, "aegis-webview2-" + Guid.NewGuid().ToString("N"));
        Text = "WebView2 feasibility — local fixture";
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        local.Click += (_, _) =>
        {
            if (!closing && tabs.SelectedIndex >= 0) StartNavigation(tabs.SelectedIndex);
        };
        storage.Click += async (_, _) =>
        {
            if (!lifecycle.TryBeginOperation(out var lease)) return;
            storage.Enabled = false;
            try { await CheckSyntheticStorageAsync(); }
            finally
            {
                lease?.Dispose();
                if (!closing && !IsDisposed) storage.Enabled = true;
            }
        };
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
        FormClosing += CloseAfterPendingOperations;
    }

    private async void CloseAfterPendingOperations(object? sender, FormClosingEventArgs e)
    {
        if (closeReady) return;
        e.Cancel = true;
        if (closeWaitInProgress) return;
        if (closing && lifecycle.State == PrototypeLifecycleState.Failed)
        {
            status.Text = disposalFailureMessage ?? "Shutdown remains incomplete after disposal failure.";
            return;
        }
        if (!closing)
        {
            if (!lifecycle.TryBeginClose()) return;
            closing = true;
            local.Enabled = false;
            storage.Enabled = false;
        }
        closeWaitInProgress = true;
        status.Text = "Waiting for browser operations to finish.";
        try
        {
            // This deadline reports incomplete shutdown; it does not cancel
            // native work or authorize disposing controls while work is active.
            if (!await lifecycle.WaitForQuiescenceAsync(Task.Delay(configuration.ShutdownTimeoutMilliseconds)))
            {
                status.Text = "Shutdown timed out; browser work is still pending. Close again to recheck.";
                return;
            }
            var failures = lifecycle.DisposeAll(new Action[] { views[0].Dispose, views[1].Dispose });
            if (failures is null)
            {
                status.Text = "Shutdown incomplete: disposal was not permitted.";
                return;
            }
            if (failures.Count != 0)
            {
                var summaries = new List<string>();
                foreach (var failure in failures) summaries.Add($"{failure.Category}:0x{failure.HResult:X8}");
                disposalFailureMessage = "Shutdown incomplete: " + string.Join(", ", summaries);
                status.Text = disposalFailureMessage;
                return;
            }
            closeReady = true;
            if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)Close);
        }
        catch (Exception error)
        {
            if (!IsDisposed) status.Text = $"Shutdown failed ({error.GetType().Name}, 0x{error.HResult:X8}).";
        }
        finally { closeWaitInProgress = false; }
    }

    private async Task InitializeAsync()
    {
        if (!lifecycle.TryBeginInitialization()) return;
        var initialized = false;
        int? failureCode = null;
        try
        {
            if (Directory.Exists(runDirectory)) throw new IOException();
            string runtimeVersion = "unknown";
            for (var i = 0; i < views.Length && !closing; i++)
            {
                // The root was explicitly selected and checked before creating controls.
                var profile = Path.Combine(runDirectory, i == 0 ? "a" : "b");
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
                runtimeVersion = environment.BrowserVersionString;
                if (closing) return;
                await views[i].EnsureCoreWebView2Async(environment);
                if (closing) return;
                var browser = views[i].CoreWebView2;
                browser.DownloadStarting += (_, e) => e.Cancel = true;
                browser.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                browser.NewWindowRequested += (_, e) => e.Handled = true;
                var slot = i;
                browser.NavigationStarting += (_, e) => OnNavigationStarting(slot, e);
                browser.NavigationCompleted += (_, e) => OnNavigationCompleted(slot, e);
                browser.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                browser.WebResourceRequested += (_, e) =>
                {
                    // Deny all resource requests; never log the request URL or body.
                    e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                };
                StartNavigation(i);
            }
            initialized = !closing;
            if (!closing) status.Text = $"Runtime {runtimeVersion}; browser processes {views[0].CoreWebView2.BrowserProcessId}, {views[1].CoreWebView2.BrowserProcessId}; behavior not yet qualified.";
        }
        catch (Exception error)
        {
            failureCode = error.HResult;
            if (!closing && !IsDisposed) status.Text = $"Initialization failed ({error.GetType().Name}, 0x{error.HResult:X8}).";
        }
        finally { lifecycle.FinishInitialization(initialized, failureCode); }
    }

    private void StartNavigation(int index)
    {
        var browser = views[index].CoreWebView2;
        if (browser is null || navigations[index] is not null || !lifecycle.TryBeginNavigation(out var lease)) return;
        var attempt = new NavigationAttempt(lease!);
        navigations[index] = attempt;
        status.Text = $"Profile {index + 1}: navigation requested.";
        try
        {
            browser.NavigateToString(LocalPage);
            _ = ReportNavigationTimeoutAsync(index, attempt);
        }
        catch (Exception error)
        {
            navigations[index] = null;
            attempt.Lease.Dispose();
            if (!closing && !IsDisposed) status.Text = $"Navigation request failed ({error.GetType().Name}, 0x{error.HResult:X8}).";
        }
    }

    private void OnNavigationStarting(int index, CoreWebView2NavigationStartingEventArgs e)
    {
        if (closing || !string.Equals(e.Uri, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            if (!IsDisposed) status.Text = $"Profile {index + 1}: navigation cancelled by policy.";
            return;
        }
        var attempt = navigations[index];
        if (attempt is null || attempt.NavigationId is not null)
        {
            e.Cancel = true;
            if (!IsDisposed) status.Text = $"Profile {index + 1}: untracked navigation cancelled.";
            return;
        }
        attempt.NavigationId = e.NavigationId;
        if (!IsDisposed) status.Text = $"Profile {index + 1}: navigation started.";
    }

    private void OnNavigationCompleted(int index, CoreWebView2NavigationCompletedEventArgs e)
    {
        var attempt = navigations[index];
        if (attempt is null || attempt.NavigationId != e.NavigationId) return;
        navigations[index] = null;
        attempt.Lease.Dispose();
        if (IsDisposed) return;
        status.Text = e.IsSuccess
            ? $"Profile {index + 1}: navigation completed; rendering not yet verified."
            : $"Profile {index + 1}: navigation failed ({e.WebErrorStatus}).";
    }

    private async Task ReportNavigationTimeoutAsync(int index, NavigationAttempt attempt)
    {
        await Task.Delay(configuration.ShutdownTimeoutMilliseconds);
        if (ReferenceEquals(navigations[index], attempt) && !IsDisposed)
            status.Text = $"Profile {index + 1}: navigation timed out; completion remains unconfirmed.";
    }

    private sealed class NavigationAttempt
    {
        internal NavigationAttempt(IDisposable lease) => Lease = lease;
        internal IDisposable Lease { get; }
        internal ulong? NavigationId { get; set; }
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
