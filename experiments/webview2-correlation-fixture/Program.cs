using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using AegisBrowser.WebView2Feasibility;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AegisBrowser.WebView2Correlation;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (!ProfileRootPolicy.TryParse(args, out var config, out var reason) ||
            !ProfileRootPolicy.ValidateExistingRoot(config, out reason))
        {
            MessageBox.Show(reason, "Correlation fixture configuration");
            Environment.ExitCode = 2;
            return;
        }

        var directory = Path.Combine(config.ProfileRoot, "correlation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var journal = new Journal(Path.Combine(directory, "correlation.log"));
        journal.Record("PROCESS_START");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            var form = new FixtureForm(directory, journal);
            Application.Run(form);
            journal.Record("MESSAGE_LOOP_RETURN");
        }
        catch (Exception e)
        {
            journal.Record("PROCESS_EXCEPTION", detail: Journal.Category(e));
            Environment.ExitCode = 1;
        }
        journal.Record("PROCESS_EXIT_INTENT", detail: Environment.ExitCode == 0 ? "ZERO" : "NONZERO");
        if (!journal.Complete()) Environment.ExitCode = 1;
    }
}

internal sealed class Journal
{
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly BlockingCollection<string> records = new(256);
    private readonly Thread writer;
    private readonly object gate = new();
    private bool incomplete;
    private int sequence;

    internal Journal(string path)
    {
        writer = new Thread(() => WriteLoop(path)) { IsBackground = true, Name = "Correlation journal" };
        writer.Start();
    }

    internal void Record(string name, ulong? id = null, bool? uri = null, bool? header = null,
        bool? redirect = null, string? detail = null)
    {
        // The caller passes only fixed event names, booleans and fixed categories;
        // never pass a URI, header value, user data, or exception message.
        lock (gate)
        {
            if (incomplete || sequence >= 1024) { incomplete = true; return; }
            string line = string.Create(CultureInfo.InvariantCulture,
                $"{sequence + 1} {watch.ElapsedMilliseconds} {Environment.CurrentManagedThreadId} {name} id={id?.ToString(CultureInfo.InvariantCulture) ?? "-"} uri={uri?.ToString() ?? "-"} header={header?.ToString() ?? "-"} redirect={redirect?.ToString() ?? "-"} detail={detail ?? "-"}");
            if (!records.TryAdd(line)) { incomplete = true; return; }
            sequence++;
        }
    }

    internal static string Category(Exception e) => e switch
    {
        UnauthorizedAccessException => "UNAUTHORIZED_ACCESS",
        SecurityException => "SECURITY",
        IOException => "IO",
        COMException => "COM",
        InvalidOperationException => "INVALID_OPERATION",
        _ => "OTHER"
    };

    private void WriteLoop(string path)
    {
        try
        {
            using var output = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            foreach (var line in records.GetConsumingEnumerable())
            {
                output.WriteLine(line);
                output.Flush();
            }
        }
        catch { lock (gate) incomplete = true; }
    }

    // Called only after the message loop returns, with a bounded wait.
    internal bool Complete()
    {
        lock (gate) records.CompleteAdding();
        bool stopped = writer.Join(TimeSpan.FromSeconds(2));
        lock (gate) return stopped && !incomplete;
    }
}

internal sealed class FixtureForm : Form
{
    private const string HeaderName = "X-Aegis-Correlation";
    private const string Html = "<!doctype html><meta charset=utf-8><title>Fixture</title><p>Correlation fixture</p>";
    private readonly WebView2 view = new() { Dock = DockStyle.Fill };
    private readonly Button dispatch = new() { Text = "Dispatch one fixture navigation", Dock = DockStyle.Top, Enabled = false };
    private readonly Label status = new() { Text = "Initializing", Dock = DockStyle.Bottom, AutoSize = true };
    private readonly HashSet<ulong> active = new();
    private readonly string profile;
    private readonly Journal journal;
    private readonly string token = Guid.NewGuid().ToString("N");
    private string uri = "";
    private bool initializing = true;
    private bool initializationFailed;
    private bool closingRequested;
    private bool dispatched;
    private bool candidateSeen;
    private ulong candidateId;
    private bool candidateComplete;
    private bool unexpectedFrame;
    private bool responseIssued;

    internal FixtureForm(string directory, Journal journal)
    {
        profile = Path.Combine(directory, "profile");
        this.journal = journal;
        uri = "https://aegis-fixture.invalid/navigation/" + token;
        Text = "Navigation correlation fixture";
        Controls.Add(view);
        Controls.Add(dispatch);
        Controls.Add(status);
        dispatch.Click += Dispatch;
        Shown += async (_, _) => await InitializeAsync();
        FormClosing += OnClosing;
        FormClosed += (_, _) => journal.Record("FORM_CLOSED");
    }

    private async Task InitializeAsync()
    {
        journal.Record("INITIALIZATION_START");
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            await view.EnsureCoreWebView2Async(environment);
            var browser = view.CoreWebView2;
            string version = environment.BrowserVersionString;
            journal.Record("RUNTIME_SELECTED", detail: Regex.IsMatch(version,
                @"\A[0-9]{1,4}(?:\.[0-9]{1,6}){3}(?: (?:beta|dev|canary))?\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? version : "UNAVAILABLE");
            browser.Settings.IsScriptEnabled = false;
            browser.Settings.AreDefaultContextMenusEnabled = false;
            browser.Settings.AreDevToolsEnabled = false;
            browser.Settings.AreBrowserAcceleratorKeysEnabled = false;
            browser.Settings.AreHostObjectsAllowed = false;
            browser.Settings.IsWebMessageEnabled = false;
            browser.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            browser.DownloadStarting += (_, e) => e.Cancel = true;
            browser.NewWindowRequested += (_, e) => e.Handled = true;
            browser.FrameNavigationStarting += (_, e) =>
            {
                unexpectedFrame = true;
                e.Cancel = true;
                journal.Record("UNEXPECTED_FRAME", e.NavigationId);
            };
            browser.NavigationStarting += OnStart;
            browser.NavigationCompleted += OnComplete;
            browser.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All,
                CoreWebView2WebResourceRequestSourceKinds.All);
            browser.WebResourceRequested += (_, e) => OnResource(environment, e);
            journal.Record("INITIALIZATION_COMPLETE");
            dispatch.Enabled = !closingRequested;
            status.Text = closingRequested ? "Initialization ended; close again." : "Ready; dispatch once.";
        }
        catch (Exception e)
        {
            initializationFailed = true;
            journal.Record("INITIALIZATION_FAILED", detail: Journal.Category(e));
            Environment.ExitCode = 1;
            status.Text = "Initialization failed; see sanitized log.";
        }
        finally { initializing = false; }
    }

    private void Dispatch(object? sender, EventArgs e)
    {
        if (dispatched || closingRequested) return;
        dispatch.Enabled = false;
        try
        {
            var browser = view.CoreWebView2;
            var request = browser.Environment.CreateWebResourceRequest(uri, "GET", null,
                HeaderName + ": " + token + "\r\n");
            journal.Record("DISPATCH_ENTER");
            // From this point, an exception cannot prove that no native work was queued.
            dispatched = true;
            browser.NavigateWithWebResourceRequest(request);
            journal.Record("DISPATCH_RETURN");
        }
        catch (Exception error)
        {
            journal.Record("DISPATCH_EXCEPTION", detail: Journal.Category(error));
            Environment.ExitCode = 1;
            status.Text = "Dispatch failed; terminal state may be unknown.";
        }
    }

    private bool MatchesHeader(CoreWebView2HttpRequestHeaders headers)
    {
        try { return headers.Contains(HeaderName) && headers.GetHeader(HeaderName) == token; }
        catch { return false; }
    }

    private void OnStart(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        bool uriMatch = string.Equals(e.Uri, uri, StringComparison.Ordinal);
        bool headerMatch = MatchesHeader(e.RequestHeaders);
        active.Add(e.NavigationId);
        bool match = dispatched && !candidateSeen && uriMatch && headerMatch && !e.IsRedirected;
        if (match) { candidateSeen = true; candidateId = e.NavigationId; if (closingRequested) e.Cancel = true; }
        else e.Cancel = true;
        journal.Record(match ? (closingRequested ? "CANDIDATE_START_CANCELLED" : "CANDIDATE_START") : "REJECTED_START", e.NavigationId,
            uriMatch, headerMatch, e.IsRedirected);
    }

    private void OnComplete(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        bool found = active.Remove(e.NavigationId);
        bool match = candidateSeen && candidateId == e.NavigationId;
        if (match && found) candidateComplete = true;
        journal.Record("NAVIGATION_COMPLETED", e.NavigationId, found, match,
            detail: e.IsSuccess ? "SUCCESS" : "FAILURE");
        status.Text = "Terminal callback observed; see sanitized log.";
    }

    private void OnResource(CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs e)
    {
        bool uriMatch = string.Equals(e.Request.Uri, uri, StringComparison.Ordinal);
        bool headerMatch = MatchesHeader(e.Request.Headers);
        bool allowed = dispatched && !responseIssued && uriMatch && headerMatch &&
            e.ResourceContext == CoreWebView2WebResourceContext.Document;
        // No default/fall-through branch for observed HTTP(S) requests. A COM
        // exception here still requires external network isolation to be safe.
        try
        {
            e.Response = environment.CreateWebResourceResponse(
                allowed ? new MemoryStream(Encoding.UTF8.GetBytes(Html)) : null,
                allowed ? 200 : 403, allowed ? "OK" : "Blocked",
                allowed ? "Content-Type: text/html; charset=utf-8\r\nContent-Security-Policy: default-src 'none'; frame-ancestors 'none'\r\nCache-Control: no-store\r\n" : "Cache-Control: no-store\r\n");
            if (allowed) responseIssued = true;
            journal.Record(allowed ? "FIXTURE_RESPONSE" : "RESOURCE_BLOCK_RESPONSE", uri: uriMatch, header: headerMatch);
        }
        catch (Exception error)
        {
            journal.Record("RESOURCE_HANDLER_FAILED", detail: Journal.Category(error));
            Environment.ExitCode = 1;
            throw;
        }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        journal.Record("CLOSE_REQUEST");
        closingRequested = true;
        dispatch.Enabled = false;
        if (initializing || initializationFailed || active.Count != 0 || unexpectedFrame || (dispatched && !candidateComplete))
        {
            e.Cancel = true;
            journal.Record("CLOSE_REFUSED_UNRESOLVED");
            status.Text = "Close refused: a terminal state is unconfirmed.";
            return;
        }
        journal.Record("DISPOSE_ENTER");
        view.Dispose(); // WebView2 stays on its owning STA; this may hang.
        journal.Record("DISPOSE_RETURN");
    }
}
