using AegisBrowser.Core;
using CefSharp;
using CefSharp.WinForms;

namespace AegisBrowser;

// One attachment per native tab; removed before its control is disposed.
internal sealed class BrowserDiagnosticAttachment : IDisposable
{
    private readonly ChromiumWebBrowser browser;
    private readonly NavigationDiagnostics diagnostics;
    private bool disposed;
    private readonly object gate = new();
    private void Observe(Action<NavigationDiagnostics> observation)
    {
        lock (gate) { if (!disposed) diagnostics.Observe(observation); }
    }
    internal BrowserDiagnosticAttachment(ChromiumWebBrowser browser, NavigationDiagnostics diagnostics)
    {
        this.browser = browser; this.diagnostics = diagnostics;
        browser.FrameLoadEnd += Loaded; browser.LoadError += Failed; browser.ConsoleMessage += ConsoleError;
    }
    private void Loaded(object? sender, FrameLoadEndEventArgs e)
    {
        Observe(d => { if (e.Frame.IsMain) d.Record(DiagnosticKind.MainDocumentLoaded, e.Url, e.Browser.Identifier, status: e.HttpStatusCode); });
    }
    private void Failed(object? sender, LoadErrorEventArgs e)
    {
        Observe(d => d.Record(DiagnosticKind.LoadError, e.FailedUrl, e.Browser.Identifier, error: (int)e.ErrorCode));
    }
    private void ConsoleError(object? sender, ConsoleMessageEventArgs e)
    {
        Observe(d =>
        {
            if (e.Level == LogSeverity.Error || e.Level == LogSeverity.Fatal)
                d.Record(DiagnosticKind.ConsoleError, e.Source, e.Browser.Identifier);
        });
        // Arbitrary JavaScript error messages can contain tokens and page contents: never retain them.
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return; disposed = true;
            browser.FrameLoadEnd -= Loaded; browser.LoadError -= Failed; browser.ConsoleMessage -= ConsoleError;
        }
    }
}
