using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AegisBrowser.WebView2Feasibility;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AegisBrowser.WebView2TwoControl;

// Test-only observation fixture. Never treat candidate matching as authenticated ownership.
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            Console.Error.WriteLine("STA_REQUIRED");
            Environment.ExitCode = 1;
            return;
        }
        // Scenario parsing remains separate from the shared profile policy.
        if (!ScenarioPlan.TryParseArguments(args, out var scenario, out var profileArgs, out var reason))
        {
            Console.Error.WriteLine(reason);
            Environment.ExitCode = 2;
            return;
        }
        if (!ProfileRootPolicy.TryParse(profileArgs, out var config, out reason))
        {
            Console.Error.WriteLine(reason);
            Environment.ExitCode = 2;
            return;
        }
        if (!ProfileRootPolicy.ValidateExistingRoot(config, out reason))
        {
            Console.Error.WriteLine(reason);
            Environment.ExitCode = 2;
            return;
        }
        var run = Guid.NewGuid();
        string runFolder = Path.Combine(config.ProfileRoot, "two-control-" + run.ToString("N"));
        Directory.CreateDirectory(runFolder);
        var journal = DiagnosticJournal.CreateConsole(run);
        journal.TryRecord(DiagnosticEvent.Startup);
        var requestedMarker = scenario switch
        {
            FixtureScenario.Normal => DiagnosticEvent.RequestedNormal,
            FixtureScenario.OnePending => DiagnosticEvent.RequestedOnePending,
            FixtureScenario.BothPending => DiagnosticEvent.RequestedBothPending,
            _ => throw new InvalidOperationException("Invalid scenario.")
        };
        if (!journal.TryRecord(requestedMarker))
        {
            Environment.ExitCode = 1;
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            Application.Run(new QualificationForm(runFolder, journal, scenario));
            journal.TryRecord(DiagnosticEvent.MessageLoopReturn);
        }
        catch (Exception)
        {
            Environment.ExitCode = 1;
            journal.TryRecord(DiagnosticEvent.ProcessOutcome, failure: DiagnosticFailure.Other, exitCode: 1);
        }
        // This is exit intent. The subsequent bounded flush can change the actual exit.
        journal.TryRecord(DiagnosticEvent.ProcessOutcome, exitCode: Environment.ExitCode == 0 ? 0 : 1);
        // After the loop exits a foreground non-UI worker completes only the
        // bounded diagnostic flush; it never touches controls or WebView2.
        try
        {
            var flush = new Thread(() =>
            {
                try
                {
                    if (!journal.CompleteAsync(Task.Delay(2000)).GetAwaiter().GetResult())
                        Environment.ExitCode = 1;
                }
                catch (Exception) { Environment.ExitCode = 1; }
            }) { IsBackground = false, Name = "Qualification journal flush" };
            flush.Start();
        }
        catch (Exception) { Environment.ExitCode = 1; }
    }
}

internal sealed class QualificationForm : Form
{
    private const string HeaderName = "X-Aegis-Correlation";
    private readonly WebView2[] views = { new(), new() };
    private readonly Label status = new() { Text = "Initializing", Dock = DockStyle.Bottom, AutoSize = true };
    private readonly Label selected = new() { Dock = DockStyle.Top, AutoSize = true };
    private readonly ObservationGate gate = new();
    private readonly string runFolder;
    private readonly DiagnosticJournal journal;
    private readonly FixtureScenario requested;
    private readonly ScenarioPlan plan;
    private readonly string[] tokens = { "", "" };
    private readonly string[] uris = { "", "" };
    private readonly bool[] responseSent = new bool[2];
    private readonly bool[] resourceSeen = new bool[2];
    private bool normalBegun;
    private bool normalBothSubmitted;
    private bool normalComplete;
    private bool closing;
    private bool disposing;
    private bool finalClose;
    private bool closePosted;
    private bool scenarioPosted;
    private int raceMode;
    private int repeatedClose;

    internal QualificationForm(string runFolder, DiagnosticJournal journal, FixtureScenario scenario)
    {
        this.runFolder = runFolder;
        this.journal = journal;
        requested = scenario;
        plan = new ScenarioPlan(scenario);
        Text = "Two-control lifecycle observation: " + scenario;
        selected.Text = "Selected scenario: " + scenario;
        Width = 1000;
        Height = 700;
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        for (int i = 0; i < 2; i++) { views[i].Dock = DockStyle.Fill; grid.Controls.Add(views[i], i, 0); }
        Controls.Add(grid); Controls.Add(selected); Controls.Add(status);
        Shown += async (_, _) => await InitializeAsync();
        FormClosing += OnClosing;
        FormClosed += (_, _) => journal.TryRecord(DiagnosticEvent.FormClosed);
    }

    private static DiagnosticSlot Slot(int index) => index == 0 ? DiagnosticSlot.A : DiagnosticSlot.B;

    private async Task InitializeAsync()
    {
        for (int i = 0; i < 2; i++)
        {
            int slot = i;
            if (closing)
            {
                gate.DispatchUncertain(slot);
                status.Text = "Close requested during initialization; close is blocked.";
                return;
            }
            journal.TryRecord(DiagnosticEvent.InitializationStart, Slot(slot), active: gate.PendingForSlot(slot));
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(runFolder, slot == 0 ? "a" : "b"));
                if (closing)
                {
                    gate.CompleteInitialization(slot, false);
                    status.Text = "Close requested during initialization; close is blocked.";
                    return;
                }
                await views[slot].EnsureCoreWebView2Async(environment);
                if (closing)
                {
                    gate.CompleteInitialization(slot, false);
                    status.Text = "Close requested during initialization; close is blocked.";
                    return;
                }
                var browser = views[slot].CoreWebView2;
                browser.Settings.IsScriptEnabled = false;
                browser.Settings.AreDefaultContextMenusEnabled = false;
                browser.Settings.AreDevToolsEnabled = false;
                browser.Settings.AreBrowserAcceleratorKeysEnabled = false;
                browser.Settings.AreHostObjectsAllowed = false;
                browser.Settings.IsWebMessageEnabled = false;
                browser.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                browser.DownloadStarting += (_, e) => e.Cancel = true;
                browser.NewWindowRequested += (_, e) => e.Handled = true;
                browser.FrameNavigationStarting += (_, e) => { e.Cancel = true; gate.DispatchUncertain(slot); journal.TryRecord(DiagnosticEvent.NavigationRejected, Slot(slot), active: gate.PendingForSlot(slot), navigationId: e.NavigationId); };
                browser.NavigationStarting += (_, e) => OnStart(slot, e);
                browser.NavigationCompleted += (_, e) => OnComplete(slot, e);
                browser.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
                browser.WebResourceRequested += (_, e) => OnResource(slot, environment, e);
                // Only a version accepted by the journal allowlist will be emitted.
                if (!journal.TryRecord(DiagnosticEvent.RuntimeSelected, Slot(slot), runtimeVersion: environment.BrowserVersionString))
                    gate.DispatchUncertain(slot);
                gate.CompleteInitialization(slot, true);
                journal.TryRecord(DiagnosticEvent.InitializationComplete, Slot(slot), active: gate.PendingForSlot(slot));
            }
            catch (Exception)
            {
                gate.CompleteInitialization(slot, false);
                Environment.ExitCode = 1;
                journal.TryRecord(DiagnosticEvent.InitializationFailed, Slot(slot), failure: DiagnosticFailure.Other);
                status.Text = "Initialization incomplete; close is blocked.";
                return;
            }
        }
        if (closing || gate.IsAmbiguous) return;
        status.Text = "Both initialized; scheduling normal navigation.";
        try { BeginInvoke(new Action(BeginNormal)); }
        catch (Exception)
        {
            gate.DispatchUncertain(0);
            Environment.ExitCode = 1;
            journal.TryRecord(DiagnosticEvent.ObservationIncomplete);
        }
    }

    private bool BeginDispatch(int slot)
    {
        if (!gate.TryBeginDispatch(slot)) return false;
        tokens[slot] = Guid.NewGuid().ToString("N");
        uris[slot] = "https://aegis-fixture.invalid/navigation/" + tokens[slot];
        responseSent[slot] = false; resourceSeen[slot] = false;
        try
        {
            var browser = views[slot].CoreWebView2;
            var request = browser.Environment.CreateWebResourceRequest(uris[slot], "GET", null, HeaderName + ": " + tokens[slot] + "\r\n");
            journal.TryRecord(DiagnosticEvent.NavigationRequest, Slot(slot), active: gate.PendingForSlot(slot));
            browser.NavigateWithWebResourceRequest(request);
            journal.TryRecord(DiagnosticEvent.DispatchReturned, Slot(slot), active: gate.PendingForSlot(slot));
            return true;
        }
        catch (Exception)
        {
            // Even synchronous failure does not establish no native work was queued.
            gate.DispatchUncertain(slot);
            journal.TryRecord(DiagnosticEvent.NavigationCallFailed, Slot(slot), active: gate.PendingForSlot(slot), failure: DiagnosticFailure.Other);
            Environment.ExitCode = 1;
            return false;
        }
    }

    private void BeginNormal()
    {
        if (closing || normalBegun) return;
        normalBegun = true;
        if (!BeginDispatch(0) || !BeginDispatch(1))
        {
            status.Text = "Dispatch incomplete; close is blocked.";
            return;
        }
        normalBothSubmitted = true;
        MaybeAdvance();
    }

    // Always called by a posted UI action after both normal observations.
    private void ExecuteScenario(FixtureScenario actual)
    {
        if (closing || !plan.TryBegin(actual, normalComplete && !gate.IsAmbiguous && gate.PendingOperations == 0))
        {
            gate.DispatchUncertain(0);
            Environment.ExitCode = 1;
            journal.TryRecord(DiagnosticEvent.ScenarioMismatch);
            status.Text = "Scenario was rejected; close is blocked.";
            return;
        }
        int mask = ScenarioPlan.DispatchMask(actual);
        raceMode = mask == 0 ? 0 : mask == 1 ? 1 : 2;
        var marker = actual switch
        {
            FixtureScenario.Normal => DiagnosticEvent.ScenarioNormal,
            FixtureScenario.OnePending => DiagnosticEvent.ScenarioOnePending,
            FixtureScenario.BothPending => DiagnosticEvent.ScenarioBothPending,
            _ => throw new InvalidOperationException("Invalid scenario.")
        };
        if (!journal.TryRecord(marker))
        {
            gate.DispatchUncertain(0);
            Environment.ExitCode = 1;
            return;
        }
        if ((mask & 1) != 0 && !BeginDispatch(0) || (mask & 2) != 0 && !BeginDispatch(1))
        {
            gate.DispatchUncertain(0);
            status.Text = "Dispatch incomplete; close is blocked.";
            return;
        }
        // Same STA stack, before intentionally returning to the message pump.
        Close();
        if (mask != 0 && !IsDisposed) Close(); // Repeated close in pending scenarios.
    }

    private bool HeaderMatches(int slot, CoreWebView2HttpRequestHeaders headers)
    {
        try { return headers.Contains(HeaderName) && headers.GetHeader(HeaderName) == tokens[slot]; }
        catch { return false; }
    }

    private void OnStart(int slot, CoreWebView2NavigationStartingEventArgs e)
    {
        var observed = gate.ObserveStart(slot, e.NavigationId,
            string.Equals(e.Uri, uris[slot], StringComparison.Ordinal), HeaderMatches(slot, e.RequestHeaders), e.IsRedirected);
        if (observed.Cancel) e.Cancel = true;
        journal.TryRecord(observed.Candidate ? DiagnosticEvent.NavigationStart : DiagnosticEvent.NavigationRejected,
            Slot(slot), active: gate.PendingForSlot(slot), navigationId: e.NavigationId);
        if (observed.Cancel) journal.TryRecord(DiagnosticEvent.NavigationCancelled, Slot(slot), active: gate.PendingForSlot(slot), navigationId: e.NavigationId);
    }

    private void OnComplete(int slot, CoreWebView2NavigationCompletedEventArgs e)
    {
        bool cancelled = !e.IsSuccess && e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled;
        var observed = gate.ObserveCompletion(slot, e.NavigationId, e.IsSuccess, cancelled);
        journal.TryRecord(e.IsSuccess ? DiagnosticEvent.NavigationSucceeded :
            cancelled ? DiagnosticEvent.NavigationOperationCanceled : DiagnosticEvent.NavigationOtherFailure,
            Slot(slot), active: gate.PendingForSlot(slot), navigationId: e.NavigationId);
        journal.TryRecord(observed.Matched && observed.Expected ? DiagnosticEvent.NavigationComplete : DiagnosticEvent.NavigationRejected,
            Slot(slot), active: gate.PendingForSlot(slot), navigationId: e.NavigationId);
        MaybeAdvance();
    }

    private void OnResource(int slot, CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs e)
    {
        gate.BeginResource(slot);
        resourceSeen[slot] = true;
        journal.TryRecord(DiagnosticEvent.ResourceStart, Slot(slot), active: gate.PendingForSlot(slot));
        try
        {
            bool allowed = !responseSent[slot] && e.ResourceContext == CoreWebView2WebResourceContext.Document &&
                string.Equals(e.Request.Method, "GET", StringComparison.Ordinal) &&
                string.Equals(e.Request.Uri, uris[slot], StringComparison.Ordinal) && HeaderMatches(slot, e.Request.Headers);
            // No app-owned response stream is handed to WebView2. Even so,
            // native request processing and unseen queued callbacks are unknown.
            e.Response = environment.CreateWebResourceResponse(null,
                allowed ? 200 : 403, allowed ? "OK" : "Blocked",
                allowed ? "Content-Type: text/html; charset=utf-8\r\nContent-Length: 0\r\nContent-Security-Policy: default-src 'none'\r\nCache-Control: no-store\r\n" : "Cache-Control: no-store\r\n");
            if (allowed)
            {
                responseSent[slot] = true;
                journal.TryRecord(DiagnosticEvent.SyntheticResponseServed, Slot(slot), active: gate.PendingForSlot(slot));
            }
            else
            {
                gate.DispatchUncertain(slot);
                journal.TryRecord(DiagnosticEvent.ResourceRejected, Slot(slot), active: gate.PendingForSlot(slot));
            }
        }
        catch (Exception)
        {
            gate.DispatchUncertain(slot);
            Environment.ExitCode = 1;
            journal.TryRecord(DiagnosticEvent.NavigationCallFailed, Slot(slot), failure: DiagnosticFailure.Other);
            throw; // VM network isolation is mandatory if interception fails.
        }
        finally
        {
            gate.EndResource(slot);
            // Body-complete is observed; this is not proof that native work is quiescent.
            journal.TryRecord(DiagnosticEvent.ResourceComplete, Slot(slot), active: gate.PendingForSlot(slot));
            MaybeAdvance();
        }
    }

    private void MaybeAdvance()
    {
        if (!closing && normalBothSubmitted && !normalComplete && gate.PendingOperations == 0 &&
            responseSent[0] && responseSent[1] && !gate.IsAmbiguous)
        {
            normalComplete = true;
            journal.TryRecord(DiagnosticEvent.ObservationMatched, active: 0);
            status.Text = "Normal observations completed; scheduling selected scenario.";
            if (scenarioPosted) { gate.DispatchUncertain(0); Environment.ExitCode = 1; return; }
            scenarioPosted = true;
            journal.TryRecord(DiagnosticEvent.ScenarioStartPosted);
            try { BeginInvoke(new Action(() => ExecuteScenario(requested))); }
            catch (Exception)
            {
                gate.DispatchUncertain(0);
                Environment.ExitCode = 1;
                journal.TryRecord(DiagnosticEvent.ScenarioMismatch);
            }
        }
        if (closing && gate.CanAttemptObservedDisposal && !closePosted)
        {
            closePosted = true;
            try { BeginInvoke(new Action(Close)); }
            catch (Exception) { gate.DispatchUncertain(0); Environment.ExitCode = 1; }
        }
    }

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (finalClose) return;
        e.Cancel = true;
        // Manual close before the requested scenario is not a substitute for it.
        if (!plan.IsExecuted)
        {
            closing = true;
            gate.RequestClose();
            gate.DispatchUncertain(0);
            Environment.ExitCode = 1;
            journal.TryRecord(DiagnosticEvent.ScenarioMismatch, active: gate.PendingOperations);
            status.Text = "Close requested before selected scenario; close is blocked.";
            return;
        }
        if (!closing)
        {
            if (raceMode != 0)
            {
                bool precondition = !gate.IsAmbiguous && gate.IsDispatchPending(0) && !gate.HasCandidate(0) &&
                    (raceMode == 2 ? (gate.IsDispatchPending(1) && !gate.HasCandidate(1))
                                   : !gate.IsDispatchPending(1));
                journal.TryRecord(precondition ? DiagnosticEvent.RacePreconditionMet : DiagnosticEvent.RacePreconditionMissed,
                    active: gate.PendingOperations);
            }
            closing = true;
            gate.RequestClose();
            journal.TryRecord(DiagnosticEvent.CloseRequested, active: gate.PendingOperations);
        }
        else
        {
            repeatedClose++;
            journal.TryRecord(DiagnosticEvent.CloseRepeated, active: gate.PendingOperations);
        }
        if ((normalBegun && !normalComplete) || !gate.CanAttemptObservedDisposal || disposing)
        {
            journal.TryRecord(DiagnosticEvent.ObservationIncomplete, active: gate.PendingOperations);
            status.Text = "Observed work unresolved or ambiguous; close refused.";
            return;
        }
        disposing = true;
        journal.TryRecord(DiagnosticEvent.ObservedGateCheck, active: gate.PendingOperations);
        for (int slot = 0; slot < 2; slot++)
        {
            // Awaiting checkpoint allows UI events; recheck observed state immediately after.
            bool logged = await journal.CheckpointAsync(DiagnosticEvent.DisposeCheckpoint, Slot(slot), Task.Delay(2000), active: gate.PendingForSlot(slot));
            if (!logged || !gate.CanAttemptObservedDisposal)
            {
                journal.TryRecord(DiagnosticEvent.ObservationIncomplete, Slot(slot), active: gate.PendingForSlot(slot));
                status.Text = "Diagnostic or observed state incomplete; disposal stopped.";
                Environment.ExitCode = 1;
                return;
            }
            if (!journal.TryRecord(DiagnosticEvent.DisposeEnter, Slot(slot), active: gate.PendingForSlot(slot)))
            {
                status.Text = "Diagnostic incomplete; disposal stopped.";
                Environment.ExitCode = 1;
                return;
            }
            try { views[slot].Dispose(); }
            catch (Exception)
            {
                journal.TryRecord(DiagnosticEvent.DisposeFailed, Slot(slot), failure: DiagnosticFailure.Other);
                status.Text = "Disposal failed; closing remains blocked.";
                Environment.ExitCode = 1;
                return;
            }
            journal.TryRecord(DiagnosticEvent.DisposeReturn, Slot(slot), active: gate.PendingForSlot(slot));
        }
        // This refers only to the observed gate; queued native callbacks are unknown.
        journal.TryRecord(DiagnosticEvent.ObservedGateClear, active: gate.PendingOperations);
        finalClose = true;
        BeginInvoke(new Action(Close));
    }
}
