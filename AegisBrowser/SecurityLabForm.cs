using AegisBrowser.SecurityLab;

namespace AegisBrowser;

internal sealed class SecurityLabForm(Action<string> openPage, Func<CancellationToken, Task<SecurityDecisionServer>>? startServer = null) : Form
{
    private readonly Label status = new() { Text = "Not Run", AutoSize = true, AccessibleName = "Test status" };
    private readonly Label decision = new() { Text = "Decision: —", AutoSize = true };
    private readonly Label requestId = new() { Text = "Request ID: —", AutoSize = true };
    private readonly Button run = new() { Text = "Run Test", AutoSize = true };
    private readonly Button reset = new() { Text = "Reset Session", AutoSize = true };
    private readonly Button open = new() { Text = "Open Test Page", AutoSize = true };
    private readonly Button export = new() { Text = "Export JSON Report", AutoSize = true, Enabled = false };
    private readonly CancellationTokenSource stop = new();
    private Task<SecurityDecisionServer>? server;
    private Task active = Task.CompletedTask;
    private bool busy, closing, cleaned;
    private Task? cleanup;
    internal SecurityTestReport? Report { get; private set; }
    internal string TestStatus => status.Text;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Text = "Security Lab — Local simulation"; AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 330); MinimumSize = new Size(500, 300);
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(16) };
        layout.Controls.Add(new Label { Text = "Simulation only — Scenario: Security Decisions | Mock response: Allow", AutoSize = true });
        layout.Controls.Add(new Label { Text = "Run Test checks a fresh isolated session and revokes it at completion.\nOpen Test Page uses a separate temporary browser session.", AutoSize = true });
        layout.Controls.Add(status); layout.Controls.Add(decision); layout.Controls.Add(requestId);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        buttons.Controls.AddRange([run, reset, open, export]); layout.Controls.Add(buttons); Controls.Add(layout);
        run.Click += async (_, _) => await RunTestAsync();
        reset.Click += (_, _) => ResetSession();
        open.Click += async (_, _) => await OpenTestPageAsync();
        export.Click += async (_, _) => {
            if (Report is null) return;
            using var dialog = new SaveFileDialog { Filter = "JSON report|*.json", FileName = "aegis-mock-allow.json" };
            if (dialog.ShowDialog(this) == DialogResult.OK && !await SaveReportAsync(dialog.FileName) && !closing)
                MessageBox.Show(this, "Could not save report.", "Security Lab", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
    }
    internal async Task<bool> SaveReportAsync(string path)
    {
        if (Report is not { } report) return false;
        try { await File.WriteAllTextAsync(path, report.ToJson()); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    private Task<SecurityDecisionServer> Server() => server ??= startServer is null ? SecurityDecisionServer.StartAsync(cancellationToken: stop.Token) : startServer(stop.Token);
    internal Task RunTestAsync() => busy || closing ? Task.CompletedTask : active = ExecuteAsync(true);
    internal Task OpenTestPageAsync() => busy || closing ? Task.CompletedTask : active = ExecuteAsync(false);
    private async Task ExecuteAsync(bool test)
    {
        busy = true; run.Enabled = reset.Enabled = open.Enabled = export.Enabled = false;
        if (test) { Report = null; status.Text = "Running"; decision.Text = "Decision: —"; requestId.Text = "Request ID: —"; }
        try
        {
            var instance = await Server();
            if (test)
            {
                var result = await new SecurityTestRunner().RunAsync(instance, stop.Token);
                if (!closing) {
                    Report = result;
                    status.Text = Report.Passed ? "Passed" : "Failed";
                    decision.Text = "Decision: " + (Report.Decision ?? "unavailable");
                    requestId.Text = "Request ID: " + (Report.RequestId ?? "unavailable");
                }
            }
            else if (!closing) openPage(instance.PageUrl.AbsoluteUri);
        }
        catch {
            // A failed startup is retryable; never reuse a cached faulted task.
            if (server is { IsFaulted: true } or { IsCanceled: true }) server = null;
            if (!closing) {
                var now = DateTimeOffset.UtcNow;
                Report = new("Security Decisions", "AegisMock", null, null, now, now, [new("Server available", false)], false, "ServerUnavailable");
                status.Text = "Failed";
                decision.Text = "Decision: unavailable"; requestId.Text = "Request ID: unavailable";
            }
        }
        finally { busy = false; if (!closing) { run.Enabled = reset.Enabled = open.Enabled = true; export.Enabled = Report is not null; } }
    }
    internal void ResetSession()
    {
        if (busy || closing) return;
        if (server?.IsCompletedSuccessfully == true) server.Result.ResetAllSessions();
        Report = null; status.Text = "Not Run"; decision.Text = "Decision: —"; requestId.Text = "Request ID: —"; export.Enabled = false;
    }
    internal Task ShutdownAsync() => cleanup ??= CleanupAsync();
    private async Task CleanupAsync()
    {
        closing = true; stop.Cancel();
        try { await active; if (server is not null) await (await server).DisposeAsync(); }
        catch { /* Teardown never produces a successful report. */ }
        cleaned = true;
    }
    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (!cleaned) {
            e.Cancel = true;
            if (closing) return;
            await ShutdownAsync(); Close(); return;
        }
        base.OnFormClosing(e);
    }
    protected override void Dispose(bool disposing) { if (disposing) stop.Dispose(); base.Dispose(disposing); }
}
