using AegisBrowser.Core;
using CefSharp;
using CefSharp.WinForms;

namespace AegisBrowser;

public sealed class MainForm : Form
{
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill, ShowToolTips = true, AccessibleName = "Browser tabs" };
    private readonly TextBox address = new() { Dock = DockStyle.Fill, AccessibleName = "Address" };
    private readonly ToolStripStatusLabel status = new() { Text = "Ready", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button back;
    private readonly Button forward;
    private readonly Button reload;
    private bool closing;
    private bool closeComplete;
    private readonly PrivacySession privacy;
    private readonly PrivacySettingsStore? privacyStore;
    private readonly bool privacyLoadBlocksTabs;
    private readonly ThirdPartyCookiePolicy.Result cookiePolicy;
    private readonly bool cookiePolicyBlocksTabs;
    private readonly WebRtcPrivacyPolicy.Result webRtcPolicy;
    private readonly bool webRtcPolicyBlocksTabs;
    private readonly bool letterboxingAtStartup;
    private readonly bool disableWebGlAtStartup;
    private readonly bool disableRemoteFontsAtStartup;
    private readonly System.Windows.Forms.Timer letterboxZoomTimer = new() { Interval = 250 };
    private readonly Dictionary<ChromiumWebBrowser, double> letterboxZoomLevels = new();
    private readonly Dictionary<ChromiumWebBrowser, ViewportZoomQuery> letterboxZoomQueries = new();
    private readonly Dictionary<ChromiumWebBrowser, (long Generation, double Zoom)> letterboxZoomConfirmations = new();
    private readonly Func<ChromiumWebBrowser, Task<double>> letterboxZoomQuery;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Action<string>? ViewportZoomTrace { get; set; }
    private PrivacyForm? privacyWindow;
    private SearchSettings searchSettings = new();
    private readonly SearchSettingsStore? searchStore;
    private SearchSettingsForm? searchWindow;
    internal string SearchEngineId => searchSettings.EngineId;
    private readonly HashSet<TabPage> retiring = new();
    private ChromiumWebBrowser? Current => tabs.SelectedTab?.Controls.OfType<ChromiumWebBrowser>().FirstOrDefault();

    public MainForm(string profile, string? profilePath = null) : this(profile, profilePath, null) { }

    internal MainForm(string profile, string? profilePath, Func<ChromiumWebBrowser, Task<double>>? zoomQuery,
        Action<string>? privacyLoadWarning = null)
    {
        letterboxZoomQuery = zoomQuery ?? (browser =>
            (browser.GetBrowserHost() ?? throw new InvalidOperationException("CEF browser host unavailable.")).GetZoomLevelAsync());
        var configuration = new PrivacyConfiguration();
        string? privacyWarning = null;
        if (profilePath is not null)
        {
            searchStore = new(profilePath);
            try { searchSettings = searchStore.Load(); }
            catch (IOException ex) { Shown += (_, _) => MessageBox.Show(this, ex.Message, "Search settings warning"); }
            privacyStore = new(profilePath, requireExistingDirectory: true);
            try { configuration = privacyStore.Load(); }
            catch (IOException ex) { privacyWarning = ex.Message; }
        }
        privacyLoadBlocksTabs = privacyWarning is not null;
        privacy = new(configuration);
        letterboxingAtStartup = configuration.LetterboxWebContent;
        disableWebGlAtStartup = configuration.DisableWebGl;
        disableRemoteFontsAtStartup = configuration.DisableRemoteFonts;
        cookiePolicy = ThirdPartyCookiePolicy.ApplyAtStartup(configuration.BlockThirdPartyCookies);
        cookiePolicyBlocksTabs = ThirdPartyCookiePolicy.PreventBrowsing(configuration.BlockThirdPartyCookies, cookiePolicy);
        webRtcPolicy = WebRtcPrivacyPolicy.ApplyAtStartup(configuration.RestrictWebRtcNonProxiedUdp);
        webRtcPolicyBlocksTabs = WebRtcPrivacyPolicy.PreventBrowsing(configuration.RestrictWebRtcNonProxiedUdp, webRtcPolicy);
        if (privacyWarning is not null) Shown += (_, _) =>
        {
            var message = privacyWarning + "\n\nNo browser tabs will open until the settings are repaired or renamed and this profile is restarted. " +
                "Renaming the file starts the profile with default privacy settings; review the privacy controls before browsing.";
            if (privacyLoadWarning is null) MessageBox.Show(this, message, "Privacy settings unavailable");
            else privacyLoadWarning(message);
        };
        if (!cookiePolicy.Succeeded) Shown += (_, _) => MessageBox.Show(this,
            cookiePolicy.Error + (cookiePolicyBlocksTabs
                ? "\n\nNo browser tabs will open. Disable the setting in Privacy and restart this profile."
                : "\n\nThe existing Chromium cookie policy remains in effect; it could not be restored to the default."),
            "Cookie protection could not be applied");
        if (!webRtcPolicy.Succeeded) Shown += (_, _) => MessageBox.Show(this,
            webRtcPolicy.Error + (webRtcPolicyBlocksTabs
                ? "\n\nNo browser tabs will open. Disable the setting in Privacy and restart this profile."
                : "\n\nThe existing Chromium WebRTC policy could not be restored to the default."),
            "WebRTC privacy setting could not be applied");
        Text = $"Aegis Browser — {profile}";
        DesktopStyle.Apply(this);
        ClientSize = new(1150, 740);
        MinimumSize = new(570, 340);

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        shell.RowStyles.Add(new(SizeType.Absolute, 48));
        shell.RowStyles.Add(new(SizeType.Absolute, 52));
        shell.RowStyles.Add(new(SizeType.Percent, 100));
        var tabActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new(8, 4, 8, 4),
            WrapContents = false, BackColor = DesktopStyle.Header
        };
        tabActions.Controls.Add(MakeButton("New tab", "New tab (Ctrl+T)", () => NewTab(BrowserPolicy.DiagnosticsUrl)));
        tabActions.Controls.Add(MakeButton("Close tab", "Close current tab (Ctrl+W)", CloseTab));
        shell.Controls.Add(tabActions, 0, 0);

        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1, Margin = Padding.Empty,
            Padding = new(8, 4, 8, 4), BackColor = DesktopStyle.Surface
        };
        bar.ColumnStyles.Add(new(SizeType.Absolute, 42));
        bar.ColumnStyles.Add(new(SizeType.Absolute, 42));
        bar.ColumnStyles.Add(new(SizeType.Absolute, 68));
        bar.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++) bar.ColumnStyles.Add(new(SizeType.AutoSize));
        back = MakeButton("←", "Back (Alt+Left)", () => Current?.Back());
        forward = MakeButton("→", "Forward (Alt+Right)", () => Current?.Forward());
        reload = MakeButton("Reload", "Reload (F5)", () => { if (Current?.IsLoading == true) Current.Stop(); else Current?.Reload(); });
        bar.Controls.Add(back, 0, 0);
        bar.Controls.Add(forward, 1, 0);
        bar.Controls.Add(reload, 2, 0);
        address.Dock = DockStyle.None;
        address.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        address.Margin = new(8, 0, 8, 0);
        bar.Controls.Add(address, 3, 0);
        bar.Controls.Add(MakeButton("Privacy", "Privacy settings", () =>
        {
            if (privacyWindow is null || privacyWindow.IsDisposed)
                privacyWindow = new PrivacyForm(privacy, Current?.Address, config => privacy.Apply(config, privacyStore is null ? null : privacyStore.Save), cookiePolicy.Status, letterboxingAtStartup, disableWebGlAtStartup, disableRemoteFontsAtStartup, webRtcPolicy.Status);
            privacyWindow.Show(this); privacyWindow.BringToFront();
        }), 4, 0);
        var profiles = MakeButton("Profiles", $"Current profile: {profile}. Open browser profiles", () =>
        {
            var manager = new ProfileManagerForm(ProfileCatalog.DefaultRoot);
            manager.Show(this);
        });
        bar.Controls.Add(profiles, 5, 0);
        var menu = new ContextMenuStrip();
        var more = MakeButton("More ▾", "More browser actions", () => menu.Show(bar.GetControlFromPosition(6, 0)!, new Point(0, bar.Height - bar.Padding.Top)));
        menu.Items.Add("Diagnostics", null, (_, _) => NewTab(BrowserPolicy.DiagnosticsUrl)).Name = "OpenDiagnostics";
        menu.Items.Add("Developer tools (F12)", null, (_, _) => Current?.ShowDevTools());
        menu.Items.Add("Search settings", null, (_, _) =>
        {
            if (searchWindow is null || searchWindow.IsDisposed)
                searchWindow = new SearchSettingsForm(searchSettings, settings =>
                {
                    searchStore?.Save(settings);
                    searchSettings = settings;
                    address.PlaceholderText = "Search " + SearchEngines.Get(settings.EngineId).Name + " or enter address";
                });
            searchWindow.Show(this); searchWindow.BringToFront();
        });
        menu.Items[^1].Name = "OpenSearchSettings";
        more.Name = "BrowserMore";
        more.ContextMenuStrip = menu;
        bar.Controls.Add(more, 6, 0);
        Disposed += (_, _) => menu.Dispose();
        shell.Controls.Add(bar, 0, 1);
        shell.Dock = DockStyle.Top;
        shell.Height = 100;
        shell.RowCount = 2;
        shell.RowStyles.RemoveAt(2);
        tabs.Margin = Padding.Empty;
        address.Name = "BrowserAddress";
        address.AccessibleName = "Search or enter address";
        address.PlaceholderText = "Search " + SearchEngines.Get(searchSettings.EngineId).Name + " or enter address";
        address.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            SubmitAddress(address.Text);
        };
        address.Leave += (_, _) => UpdateNavigation();
        var bottom = new StatusStrip();
        bottom.Items.Add(new ToolStripStatusLabel($"Profile: {profile} | Permissions: ask by origin |"));
        bottom.Items.Add(status);
        Controls.Add(tabs);
        Controls.Add(shell);
        Controls.Add(bottom);
        tabs.SelectedIndexChanged += (_, _) =>
        {
            UpdateNavigation();
            if (letterboxingAtStartup)
            {
                // A result captured before a tab was backgrounded may no
                // longer reflect its current origin or zoom when reselected.
                var selected = Current;
                foreach (var (owner, query) in letterboxZoomQueries)
                    if (owner != selected) query.Invalidate();
                RefreshLetterboxZoom();
            }
        };
        if (letterboxingAtStartup)
        {
            // CEF has no zoom-changed event in this binding. Poll only the
            // selected live browser, without blocking the WinForms or CEF UI thread.
            letterboxZoomTimer.Tick += (_, _) => RefreshLetterboxZoom();
            letterboxZoomTimer.Start();
        }
        Disposed += (_, _) =>
        {
            letterboxZoomTimer.Stop();
            letterboxZoomTimer.Dispose();
            foreach (var query in letterboxZoomQueries.Values) query.Retire();
            letterboxZoomQueries.Clear();
            letterboxZoomConfirmations.Clear();
        };
        Shown += (_, _) => NewTab(BrowserPolicy.DiagnosticsUrl);
        FormClosing += (_, e) =>
        {
            if (closeComplete) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            letterboxZoomTimer.Stop();
            foreach (var query in letterboxZoomQueries.Values) query.Retire();
            letterboxZoomQueries.Clear();
            letterboxZoomConfirmations.Clear();
            foreach (var page in tabs.TabPages.Cast<TabPage>().Concat(retiring).Distinct().ToArray())
            {
                retiring.Add(page);
                var web = page.Controls.OfType<ChromiumWebBrowser>().FirstOrDefault();
                if (web is null || web.IsDisposed) { retiring.Remove(page); continue; }
                ((SafePopups)web.LifeSpanHandler).ClosePopups();
                if (web.IsBrowserInitialized) web.GetBrowserHost().CloseBrowser(true);
            }
            if (retiring.Count == 0) { closeComplete = true; BeginInvoke((Action)Close); }
        };
        FormClosed += (_, _) =>
        {
            foreach (var page in retiring.ToArray()) page.Dispose();
            retiring.Clear();
        };
    }

    internal bool SubmitAddress(string input)
    {
        var result = AddressInputResolver.Resolve(input, searchSettings.EngineId);
        if (result.Kind == AddressInputKind.Empty) return false;
        if (result.Kind == AddressInputKind.Rejected)
        {
            status.Text = "Address rejected. Use HTTP(S), aegis:diagnostics or about:blank; credentials and other schemes are not supported.";
            return false;
        }
        Current?.Load(result.Url!);
        return true;
    }

    private static Button MakeButton(string text, string accessibleName, Action action)
    {
        var button = new Button { Text = text, Dock = DockStyle.Fill, AccessibleName = accessibleName, AutoSize = true };
        DesktopStyle.Button(button);
        button.Click += (_, _) => action();
        return button;
    }

    // CEF callbacks arrive off the WinForms thread and can race tab/window disposal.
    private void Post(Action action)
    {
        if (closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((Action)(() => { if (!closing && !IsDisposed) action(); })); }
        catch (InvalidOperationException) { /* Handle destroyed during shutdown. */ }
    }

    private void PostLifetime(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke((Action)(() => { if (!IsDisposed) action(); })); }
        catch (InvalidOperationException) { }
    }

    private void ResizeLetterboxedTab(TabPage page, ChromiumWebBrowser browser)
    {
        if (page.IsDisposed || browser.IsDisposed) return;
        var area = page.DisplayRectangle;
        var zoom = letterboxZoomLevels.GetValueOrDefault(browser);
        var rect = ViewportLetterboxing.Calculate(Math.Max(0, area.Width), Math.Max(0, area.Height), zoom);
        var bounds = new Rectangle(area.X + rect.X, area.Y + rect.Y, rect.Width, rect.Height);
        if (browser.Bounds != bounds) browser.Bounds = bounds;
        ViewportZoomTrace?.Invoke($"layout zoom={zoom:R} available={area} bounds={browser.Bounds}");
    }

    // The CEF binding has no zoom-changed notification. This explicit
    // invalidation is used when a caller controls and has confirmed a zoom
    // change; ordinary Chromium-initiated changes remain eventually polled.
    internal long InvalidateLetterboxZoom(ChromiumWebBrowser browser)
    {
        if (!letterboxingAtStartup || closing || IsDisposed || browser != Current || browser.IsDisposed)
            throw new InvalidOperationException("A selected live letterboxed browser is required.");
        if (!letterboxZoomQueries.TryGetValue(browser, out var query))
            letterboxZoomQueries[browser] = query = new ViewportZoomQuery();
        query.Invalidate();
        letterboxZoomConfirmations.Remove(browser);
        ViewportZoomTrace?.Invoke($"invalidate generation={query.Generation}");
        return query.Generation;
    }

    internal bool IsLetterboxZoomConfirmed(ChromiumWebBrowser browser, long generation, double zoom) =>
        letterboxZoomConfirmations.TryGetValue(browser, out var value) &&
        letterboxZoomQueries.TryGetValue(browser, out var query) && query.IsCurrentGeneration(value.Generation) &&
        value.Generation >= generation && value.Zoom == zoom;

    private void RefreshLetterboxZoom()
    {
        if (closing || IsDisposed || !IsHandleCreated) return;
        var selectedBrowser = Current;
        foreach (var (owner, pending) in letterboxZoomQueries)
        {
            pending.Expire();
            if (owner != selectedBrowser) pending.DiscardCompletedBackground();
        }
        if (
            tabs.SelectedTab is not { } page || Current is not { } browser ||
            retiring.Contains(page) || page.IsDisposed || browser.IsDisposed || !browser.IsBrowserInitialized) return;
        try
        {
            if (!letterboxZoomQueries.TryGetValue(browser, out var query))
                letterboxZoomQueries[browser] = query = new ViewportZoomQuery();
            // GetBrowserHost returns a browser-owned wrapper; it must not be
            // disposed by this poller. Poll never waits on an incomplete task.
            var result = query.Poll(() => letterboxZoomQuery(browser));
            if (result.Status != ViewportZoomQueryStatus.Pending)
                ViewportZoomTrace?.Invoke($"poll status={result.Status} requestGeneration={result.Generation} currentGeneration={query.Generation} zoom={result.Zoom:R}");
            if (result.Warn) status.Text = result.Status is ViewportZoomQueryStatus.TimedOut or ViewportZoomQueryStatus.Blocked
                ? "Page zoom query timed out; close and reopen this tab to restore letterboxing."
                : "Could not read page zoom; letterboxing will retry.";
            if (result.Status != ViewportZoomQueryStatus.Succeeded) return;
            var zoom = result.Zoom;
            letterboxZoomLevels[browser] = zoom;
            ResizeLetterboxedTab(page, browser);
            letterboxZoomConfirmations[browser] = (result.Generation, zoom);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or TaskCanceledException)
        {
            // CEF and the WinForms tab can be destroyed while the query is in flight.
        }
        catch (Exception)
        {
            // Keep the current bounds and surface an unavailable zoom adjustment
            // rather than letting an async timer exception terminate the UI.
            if (!closing && !IsDisposed) status.Text = "Could not adjust letterboxing for page zoom; close and reopen this tab.";
        }
    }

    private void NewTab(string url)
    {
        if (closing) return;
        if (privacyLoadBlocksTabs)
        {
            status.Text = "Privacy settings unavailable; repair or rename privacy.json and restart this profile.";
            return;
        }
        if (cookiePolicyBlocksTabs)
        {
            status.Text = "Cookie protection unavailable; change the setting in Privacy and restart.";
            return;
        }
        if (webRtcPolicyBlocksTabs)
        {
            status.Text = "WebRTC privacy setting unavailable; change the setting in Privacy and restart.";
            return;
        }
        var page = new TabPage("New tab") { Tag = "Loading…" };
        var browser = new BrowserView(url) { Dock = letterboxingAtStartup ? DockStyle.None : DockStyle.Fill };
        // Apply browser-native restrictions before this control gets a native CEF host.
        // The default-off path deliberately leaves BrowserSettings untouched.
        var exposureSettings = FingerprintSurfacePolicy.CreateSettings(disableWebGlAtStartup, disableRemoteFontsAtStartup);
        if (exposureSettings is not null) browser.BrowserSettings = exposureSettings;
        if (letterboxingAtStartup)
        {
            // Keep Chromium itself inside a centered size bucket. No JS or browser
            // identity overrides; normal tabs retain the existing Dock=Fill path.
            browser.Bounds = Rectangle.Empty;
            page.BackColor = Color.FromArgb(32, 37, 46);
            page.ClientSizeChanged += (_, _) => ResizeLetterboxedTab(page, browser);
            page.VisibleChanged += (_, _) => { if (page.Visible) ResizeLetterboxedTab(page, browser); };
            page.Disposed += (_, _) =>
            {
                letterboxZoomLevels.Remove(browser);
                letterboxZoomConfirmations.Remove(browser);
                if (letterboxZoomQueries.Remove(browser, out var query)) query.Retire();
            };
        }
        void Report(string message) => Post(() =>
        {
            if (page.IsDisposed) return;
            page.Tag = message;
            if (tabs.SelectedTab == page) status.Text = message;
        });
        browser.PermissionHandler = new OriginPermissions(Report);
        browser.RequestHandler = new SafeRequests(Report, privacy.Evaluate);
        var popups = new SafePopups(Report, () => PostLifetime(async () =>
        {
            try { await browser.ReleaseNativeHostAsync(); }
            catch (TimeoutException) { Report("Browser shutdown timed out; close the window to retry."); return; }
            retiring.Remove(page);
            if (closing)
            {
                // Keep all tab parents alive during window shutdown. Removing one page
                // can select another still-closing tab and recreate its browser HWND.
                // The form disposes the pages after every native owner has closed.
                if (retiring.Count == 0) { closeComplete = true; Close(); }
                return;
            }
            tabs.TabPages.Remove(page);
            page.Dispose();
            if (tabs.TabCount == 0) NewTab(BrowserPolicy.DiagnosticsUrl);
        }));
        browser.IsBrowserInitializedChanged += (_, _) => PostLifetime(() =>
        {
            if (retiring.Contains(page) && browser.IsBrowserInitialized)
                browser.GetBrowserHost().CloseBrowser(true);
        });
        browser.LifeSpanHandler = popups;
        page.Disposed += (_, _) => popups.ClosePopups();
        browser.DownloadHandler = new DenyDownloads(Report);
        browser.KeyboardHandler = new BrowserKeys(key => Post(() => HandleShortcut(key)));
        browser.AddressChanged += (_, _) => Post(() => { if (tabs.SelectedTab == page) UpdateNavigation(); });
        browser.LoadingStateChanged += (_, _) => Post(() => { if (tabs.SelectedTab == page) UpdateNavigation(); });
        browser.TitleChanged += (_, e) => Post(() =>
        {
            if (page.IsDisposed) return;
            var title = string.IsNullOrWhiteSpace(e.Title) ? "New tab" : e.Title;
            page.Text = title.Length > 28 ? title[..28] + "…" : title;
            page.ToolTipText = title;
        });
        browser.FrameLoadStart += (_, e) =>
        {
            if (!e.Frame.IsMain) return;
            if (letterboxingAtStartup) Post(() =>
            {
                if (letterboxZoomQueries.TryGetValue(browser, out var query)) query.Invalidate();
            });
            Report("Loading…");
        };
        browser.FrameLoadEnd += (_, e) =>
        {
            if (!e.Frame.IsMain) return;
            if (e.HttpStatusCode >= 400) Report($"HTTP {e.HttpStatusCode} from {BrowserPolicy.DisplayOrigin(e.Url)}. Reload to retry.");
            else if (e.HttpStatusCode is >= 200 and < 400) Report("Ready");
        };
        browser.LoadError += (_, e) =>
        {
            if (e.Frame.IsMain && e.ErrorCode != CefErrorCode.Aborted) Report($"Load failed: {e.ErrorCode} at {BrowserPolicy.DisplayOrigin(e.FailedUrl)}. Reload to retry.");
        };
        page.Controls.Add(browser);
        tabs.TabPages.Add(page);
        tabs.SelectedTab = page;
        if (letterboxingAtStartup) ResizeLetterboxedTab(page, browser);
    }

    private void UpdateNavigation()
    {
        var browser = Current;
        // Loading events must not overwrite an address the user is typing.
        if (!address.Focused) address.Text = browser?.Address ?? "";
        status.Text = tabs.SelectedTab?.Tag as string ?? "Ready";
        back.Enabled = browser?.CanGoBack == true;
        forward.Enabled = browser?.CanGoForward == true;
        reload.Text = browser?.IsLoading == true ? "Stop" : "Reload";
        reload.AccessibleName = browser?.IsLoading == true ? "Stop loading (Esc)" : "Reload (F5)";
        reload.Enabled = browser is not null;
    }

    private void CloseTab()
    {
        if (tabs.SelectedTab is not { } page) return;
        var browser = Current!;
        if (!retiring.Add(page)) return;
        if (letterboxZoomQueries.Remove(browser, out var query)) query.Retire();
        letterboxZoomLevels.Remove(browser);
        letterboxZoomConfirmations.Remove(browser);
        ((SafePopups)browser.LifeSpanHandler).ClosePopups();
        // Keep the TabPage parent HWND alive until CEF reports DoClose readiness; disposing during creation can destroy a window CEF still uses.
        if (browser.IsBrowserInitialized) browser.GetBrowserHost().CloseBrowser(true);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        HandleShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);

    private bool HandleShortcut(Keys keys)
    {
        switch (keys)
        {
            case Keys.Control | Keys.L: address.Focus(); address.SelectAll(); break;
            case Keys.Control | Keys.T: NewTab(BrowserPolicy.DiagnosticsUrl); break;
            case Keys.Control | Keys.W: CloseTab(); break;
            case Keys.F12: Current?.ShowDevTools(); break;
            case Keys.F5: Current?.Reload(); break;
            case Keys.Escape: Current?.Stop(); break;
            case Keys.Alt | Keys.Left: Current?.Back(); break;
            case Keys.Alt | Keys.Right: Current?.Forward(); break;
            case Keys.Control | Keys.Tab:
                if (tabs.TabCount > 0) tabs.SelectedIndex = (tabs.SelectedIndex + 1) % tabs.TabCount;
                break;
            default: return false;
        }
        return true;
    }
}

internal sealed class BrowserKeys(Action<Keys> dispatch) : CefSharp.Handler.KeyboardHandler
{
    protected override bool OnPreKeyEvent(IWebBrowser control, IBrowser browser, KeyType type,
        int windowsKeyCode, int nativeKeyCode, CefEventFlags modifiers, bool isSystemKey, ref bool isKeyboardShortcut)
    {
        if (type != KeyType.RawKeyDown) return false;
        var keys = (Keys)windowsKeyCode;
        if (modifiers.HasFlag(CefEventFlags.ControlDown)) keys |= Keys.Control;
        if (modifiers.HasFlag(CefEventFlags.AltDown)) keys |= Keys.Alt;
        if (modifiers.HasFlag(CefEventFlags.ShiftDown)) keys |= Keys.Shift;
        Keys[] shortcuts = [Keys.Control | Keys.L, Keys.Control | Keys.T, Keys.Control | Keys.W,
            Keys.Control | Keys.Tab, Keys.F12, Keys.F5, Keys.Escape, Keys.Alt | Keys.Left, Keys.Alt | Keys.Right];
        if (!shortcuts.Contains(keys)) return false;
        dispatch(keys);
        return true;
    }
}
