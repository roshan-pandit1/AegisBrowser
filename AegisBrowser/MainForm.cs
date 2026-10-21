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
    private readonly PrivacySession privacy = new();
    private PrivacyForm? privacyWindow;
    private ChromiumWebBrowser? Current => tabs.SelectedTab?.Controls.OfType<ChromiumWebBrowser>().FirstOrDefault();

    public MainForm(string profile)
    {
        Text = $"Aegis Browser — {profile}";
        Width = 1250;
        Height = 850;
        MinimumSize = new Size(750, 450);
        var bar = new TableLayoutPanel { Dock = DockStyle.Top, Height = 40, ColumnCount = 9 };
        for (var i = 0; i < 9; i++)
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
                privacyWindow = new PrivacyForm(privacy, Current?.Address);
            privacyWindow.Show(this); privacyWindow.BringToFront();
        }), 8, 0);
        address.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            var url = BrowserPolicy.NormalizeAddress(address.Text);
            if (url is null) status.Text = "Enter an HTTP(S) address without credentials, or aegis:diagnostics.";
            else Current?.Load(url);
        };
        var bottom = new StatusStrip();
        bottom.Items.Add(new ToolStripStatusLabel($"Profile: {profile} | Permissions: deny all |"));
        bottom.Items.Add(status);
        Controls.Add(tabs);
        Controls.Add(bar);
        Controls.Add(bottom);
        tabs.SelectedIndexChanged += (_, _) => UpdateNavigation();
        Shown += (_, _) => NewTab(BrowserPolicy.DiagnosticsUrl);
        FormClosing += (_, _) => closing = true;
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

    private void NewTab(string url)
    {
        if (closing) return;
        var page = new TabPage("New tab");
        var browser = new ChromiumWebBrowser(url) { Dock = DockStyle.Fill };
        void Report(string message) => Post(() => { if (tabs.SelectedTab == page) status.Text = message; });
        void Open(string target) => Post(() => { if (!page.IsDisposed) NewTab(target); });
        browser.PermissionHandler = new DenyPermissions(Report);
        browser.RequestHandler = new SafeRequests(Open, Report, privacy.Evaluate);
        browser.LifeSpanHandler = new SafePopups(Open, Report);
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
        browser.LoadError += (_, e) =>
        {
            if (e.Frame.IsMain && e.ErrorCode != CefErrorCode.Aborted) Report($"Page failed to load: {e.ErrorCode}");
        };
        page.Controls.Add(browser);
        tabs.TabPages.Add(page);
        tabs.SelectedTab = page;
    }

    private void UpdateNavigation()
    {
        var browser = Current;
        address.Text = browser?.Address ?? "";
        back.Enabled = browser?.CanGoBack == true;
        forward.Enabled = browser?.CanGoForward == true;
        reload.Text = browser?.IsLoading == true ? "Stop" : "Reload";
    }

    private void CloseTab()
    {
        if (tabs.SelectedTab is not { } page) return;
        tabs.TabPages.Remove(page);
        page.Dispose();
        if (tabs.TabCount == 0) NewTab(BrowserPolicy.DiagnosticsUrl);
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
