using AegisBrowser.Core;
using CefSharp;
using CefSharp.WinForms;

namespace AegisBrowser;

public sealed class MainForm : Form
{
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly TextBox address = new() { Dock = DockStyle.Fill, AccessibleName = "Address" };
    private readonly ToolStripStatusLabel status = new() { Text = "Ready", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button back;
    private readonly Button forward;
    private readonly Button reload;
    private bool closing;
    private bool closeComplete;
    private readonly PrivacySession privacy;
    private readonly PrivacySettingsStore? privacyStore;
    private PrivacyForm? privacyWindow;
    private SearchSettings searchSettings = new();
    private readonly SearchSettingsStore? searchStore;
    private SearchSettingsForm? searchWindow;
    internal string SearchEngineId => searchSettings.EngineId;
    private readonly HashSet<TabPage> retiring = new();
    private readonly HashSet<TabPage> retiredDuringClose = new();
    private readonly Dictionary<TabPage, RetirementRequest> retirementRequests = new();
    private ChromiumWebBrowser? Current => tabs.SelectedTab?.Controls.OfType<ChromiumWebBrowser>().FirstOrDefault();

    public MainForm(string profile, string? profilePath = null)
    {
        var configuration = new PrivacyConfiguration();
        string? privacyWarning = null;
        if (profilePath is not null)
        {
            searchStore = new(profilePath);
            try { searchSettings = searchStore.Load(); }
            catch (IOException ex) { Shown += (_, _) => MessageBox.Show(this, ex.Message, "Search settings warning"); }
            privacyStore = new(profilePath);
            try { configuration = privacyStore.Load(); }
            catch (IOException ex) { privacyWarning = ex.Message; }
        }
        privacy = new(configuration);
        if (privacyWarning is not null) Shown += (_, _) => MessageBox.Show(this, privacyWarning, "Privacy settings warning");
        Text = $"Aegis Browser — {profile}";
        Width = 1250;
        Height = 850;
        MinimumSize = new Size(750, 450);
        var bar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 40, ColumnCount = 11 };
        for (var i = 0; i < 11; i++)
            bar.ColumnStyles.Add(new ColumnStyle(i == 3 ? SizeType.Percent : SizeType.Absolute,
                i == 3 ? 100 : i >= 6 ? 100 : 48));
        back = MakeButton("←", () => Current?.Back());
        forward = MakeButton("→", () => Current?.Forward());
        reload = MakeButton("Reload", () => { if (Current?.IsLoading == true) Current.Stop(); else Current?.Reload(); });
        bar.Controls.Add(back, 0, 0);
        bar.Controls.Add(forward, 1, 0);
        bar.Controls.Add(reload, 2, 0);
        bar.Controls.Add(address, 3, 0);
        bar.Controls.Add(MakeButton("+", () => NewTab(BrowserPolicy.DiagnosticsUrl)), 4, 0);
        bar.Controls.Add(MakeButton("×", CloseTab), 5, 0);
        bar.Controls.Add(MakeButton("Diagnostics", () => NewTab(BrowserPolicy.DiagnosticsUrl)), 6, 0);
        bar.Controls.Add(MakeButton("DevTools", () => Current?.ShowDevTools()), 7, 0);
        bar.Controls.Add(MakeButton("Privacy", () =>
        {
            if (privacyWindow is null || privacyWindow.IsDisposed)
                privacyWindow = new PrivacyForm(privacy, Current?.Address, config => privacy.Apply(config, privacyStore is null ? null : privacyStore.Save));
            privacyWindow.Show(this); privacyWindow.BringToFront();
        }), 8, 0);
        bar.Controls.Add(MakeButton("Search", () =>
        {
            if (searchWindow is null || searchWindow.IsDisposed)
                searchWindow = new SearchSettingsForm(searchSettings, settings =>
                {
                    searchStore?.Save(settings); // Publish only after persistence succeeds.
                    searchSettings = settings;
                    address.PlaceholderText = "Search " + SearchEngines.Get(settings.EngineId).Name + " or enter address";
                });
            searchWindow.Show(this); searchWindow.BringToFront();
        }), 9, 0);
        bar.Controls.Add(MakeButton("Profiles", () =>
        {
            var manager = new ProfileManagerForm(ProfileCatalog.DefaultRoot);
            manager.Show(this);
        }), 10, 0);
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
        Controls.Add(bar);
        Controls.Add(bottom);
        tabs.SelectedIndexChanged += (_, _) => UpdateNavigation();
        Shown += (_, _) => NewTab(BrowserPolicy.DiagnosticsUrl);
        FormClosing += (_, e) =>
        {
            if (closeComplete) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            var pages = tabs.TabPages.Cast<TabPage>().Concat(retiring).Distinct().ToArray();
            // Register every owner before starting any observer: a completed task
            // can resume synchronously while this close event is still running.
            foreach (var page in pages) retiring.Add(page);
            foreach (var page in pages) BeginRetirement(page, nativeCloseObserved: false);
            if (retiring.Count == 0 && !closeComplete) { closeComplete = true; BeginInvoke((Action)Close); }
        };
        FormClosed += (_, _) =>
        {
            foreach (var page in retiring.ToArray()) page.Dispose();
            retiring.Clear();
            retirementRequests.Clear();
            retiredDuringClose.Clear();
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

    private static Button MakeButton(string text, Action action)
    {
        var button = new Button { Text = text, Dock = DockStyle.Fill, AccessibleName = text };
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

    private void NewTab(string url)
    {
        if (closing) return;
        var page = new TabPage("New tab") { Tag = "Loading…" };
        var browser = new BrowserView(url) { Dock = DockStyle.Fill };
        void Report(string message) => Post(() =>
        {
            if (page.IsDisposed) return;
            page.Tag = message;
            if (tabs.SelectedTab == page) status.Text = message;
        });
        browser.PermissionHandler = new OriginPermissions(Report);
        browser.RequestHandler = new SafeRequests(Report, privacy.Evaluate);
        // The readiness callback is a fallback for native-initiated closes. Explicit
        // tab/window closes start their observer immediately, before this callback.
        var popups = new SafePopups(Report, () => PostLifetime(() => BeginRetirement(page, nativeCloseObserved: true)));
        browser.IsBrowserInitializedChanged += (_, _) => PostLifetime(() =>
        {
            if (retiring.Contains(page)) RequestNativeClose(page, browser);
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
        browser.FrameLoadStart += (_, e) => { if (e.Frame.IsMain) Report("Loading…"); };
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
    }

    // All retirement state is confined to the WinForms thread. A warning never
    // authorizes disposal; the same observer remains alive for a late callback.
    private void BeginRetirement(TabPage page, bool nativeCloseObserved)
    {
        if (page.IsDisposed || retiredDuringClose.Contains(page)) return;
        var browser = page.Controls.OfType<BrowserView>().FirstOrDefault();
        retiring.Add(page);
        if (browser is null || browser.IsDisposed)
        {
            status.Text = "Browser shutdown incomplete; native closure was not confirmed.";
            return;
        }
        if (!retirementRequests.TryGetValue(page, out var request))
            retirementRequests[page] = request = new RetirementRequest();
        if (nativeCloseObserved) request.MarkNativeCloseObserved();
        if (request.TryClosePopups())
        {
            try { ((SafePopups)browser.LifeSpanHandler).ClosePopups(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Popup close request failed: " + ex);
                status.Text = "Browser shutdown incomplete; popup closure was not confirmed.";
            }
        }
        if (request.TryBeginObservation()) _ = ObserveRetirementAsync(page, browser);
        if (!nativeCloseObserved) RequestNativeClose(page, browser);
    }

    private void RequestNativeClose(TabPage page, BrowserView browser)
    {
        if (!retirementRequests.TryGetValue(page, out var request)) return;
        try
        {
            // The CEF callback can complete before its posted WinForms notification.
            if (((SafePopups)browser.LifeSpanHandler).OwnerCloseReady.IsCompletedSuccessfully)
                request.MarkNativeCloseObserved();
            if (!request.TryRequestClose(browser.IsBrowserInitialized)) return;
            browser.GetBrowserHost().CloseBrowser(true);
        }
        catch (Exception ex)
        {
            // The call may have been dispatched before failing. Do not send a
            // duplicate or dispose; keep waiting for native evidence.
            System.Diagnostics.Debug.WriteLine("Native close request failed: " + ex);
            status.Text = "Browser shutdown incomplete; native closure was not confirmed.";
        }
    }

    private async Task ObserveRetirementAsync(TabPage page, BrowserView browser)
    {
        try
        {
            await NativeCloseWaiter.WaitAsync(browser.ReleaseNativeHostAsync(), TimeSpan.FromSeconds(10), () =>
            {
                if (!IsDisposed) status.Text = "Waiting for the browser to finish closing.";
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Browser host release failed: " + ex);
            if (!IsDisposed) status.Text = "Browser shutdown incomplete; native closure was not confirmed.";
            return;
        }
        try
        {
            retiring.Remove(page);
            retirementRequests.Remove(page);
            if (closing)
            {
                // Ignore a queued readiness callback for this already-closed owner
                // while other parents remain alive during form shutdown.
                retiredDuringClose.Add(page);
                if (retiring.Count == 0 && !closeComplete) { closeComplete = true; BeginInvoke((Action)Close); }
                return;
            }
            tabs.TabPages.Remove(page);
            page.Dispose();
            if (tabs.TabCount == 0) NewTab(BrowserPolicy.DiagnosticsUrl);
        }
        catch (Exception ex)
        {
            // The native owner was confirmed closed, but UI cleanup did not finish.
            System.Diagnostics.Debug.WriteLine("Browser retirement finalization failed: " + ex);
            if (!IsDisposed) status.Text = "Browser shutdown incomplete; window cleanup failed.";
        }
    }

    private void CloseTab()
    {
        if (tabs.SelectedTab is not { } page) return;
        BeginRetirement(page, nativeCloseObserved: false);
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
