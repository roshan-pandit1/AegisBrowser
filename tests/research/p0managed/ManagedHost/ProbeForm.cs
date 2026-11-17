using CefSharp;
using CefSharp.Handler;
using CefSharp.WinForms;

namespace P0ManagedHost;

internal sealed class ProbeForm : Form
{
    private const string LocalUrl = "data:text/html,%3C!doctype%20html%3E%3Ctitle%3EAegis%20test%3C/title%3E%3Cp%3Econtrolled%20fixture%3C/p%3E";
    private readonly ProbeBrowser browser;
    private readonly ProbeLifetime lifetime;
    private readonly TaskCompletionSource<bool> localLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> httpsLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile Uri? https;
    private bool closeAllowed;

    internal int Result { get; private set; } = 1;

    internal ProbeForm()
    {
        Text = "Aegis sandbox research - disposable fixture";
        Width = 800;
        Height = 600;
        browser = new ProbeBrowser(LocalUrl) { Dock = DockStyle.Fill };
        lifetime = new ProbeLifetime();
        browser.LifeSpanHandler = lifetime;
        browser.FrameLoadEnd += (_, args) =>
        {
            if (!args.Frame.IsMain) return;
            if (args.Url.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase))
                localLoaded.TrySetResult(true);
            var expectedHttps = https;
            if (expectedHttps is not null && args.Url == expectedHttps.AbsoluteUri && args.HttpStatusCode == 200)
                httpsLoaded.TrySetResult(true);
        };
        browser.LoadError += (_, args) =>
        {
            if (!args.Frame.IsMain || args.ErrorCode == CefErrorCode.Aborted) return;
            localLoaded.TrySetException(new InvalidOperationException("LOCAL_OR_HTTPS_LOAD_ERROR_" + args.ErrorCode));
            httpsLoaded.TrySetException(new InvalidOperationException("HTTPS_LOAD_ERROR_" + args.ErrorCode));
        };
        Controls.Add(browser);
        Shown += OnShown;
        FormClosing += (_, args) => { if (!closeAllowed) args.Cancel = true; };
    }

    private async void OnShown(object? sender, EventArgs args)
    {
        Console.WriteLine("MANAGED_EVENT: WINDOW_SHOWN");
        try
        {
            await localLoaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Console.WriteLine("MANAGED_EVENT: LOCAL_PAGE_LOADED");
            var script = await browser.EvaluateScriptAsync("6 * 7").WaitAsync(TimeSpan.FromSeconds(10));
            if (!script.Success || Convert.ToInt32(script.Result) != 42)
                throw new InvalidOperationException("JAVASCRIPT_RESULT_MISMATCH");
            Console.WriteLine("MANAGED_EVENT: JAVASCRIPT_RESULT_VERIFIED");

            https = ManagedEntry.ControlledHttpsUri()
                ?? throw new InvalidOperationException("HTTPS_FIXTURE_UNAVAILABLE");
            browser.Load(https.AbsoluteUri);
            await httpsLoaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var body = await browser.EvaluateScriptAsync("document.body.textContent.trim()")
                .WaitAsync(TimeSpan.FromSeconds(10));
            if (!body.Success || !string.Equals(body.Result as string, "AEGIS_P0_HTTPS_OK", StringComparison.Ordinal))
                throw new InvalidOperationException("HTTPS_FIXTURE_CONTENT_MISMATCH");
            Console.WriteLine("MANAGED_EVENT: HTTPS_PAGE_VERIFIED");
            Result = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("MANAGED_FAIL: FIXTURE_" + ex);
            Result = 1;
        }
        finally
        {
            await CloseBrowserSafely();
        }
    }

    private async Task CloseBrowserSafely()
    {
        try
        {
            if (browser.IsBrowserInitialized)
            {
                // Do not dispose the embedded HWND while Chromium owns it.
                browser.GetBrowserHost().CloseBrowser(false);
                await lifetime.CloseReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
                browser.ReleaseNativeWindow();
                await lifetime.Closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Console.WriteLine("MANAGED_EVENT: BROWSER_BEFORE_CLOSE");
            }
            closeAllowed = true;
            Close();
        }
        catch (Exception ex)
        {
            Result = 1;
            Console.Error.WriteLine("MANAGED_FAIL: CLOSE_" + ex);
            // The supervising disposable test process has a bounded deadline.
            // Never force-destroy an active CEF-owned window or report shutdown.
        }
    }

    private sealed class ProbeBrowser(string address) : ChromiumWebBrowser(address)
    {
        internal void ReleaseNativeWindow() { if (IsHandleCreated) DestroyHandle(); }
    }

    private sealed class ProbeLifetime : LifeSpanHandler
    {
        internal TaskCompletionSource<bool> CloseReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override bool DoClose(IWebBrowser control, IBrowser browser)
        {
            if (browser.IsPopup) return false;
            Cef.UIThreadTaskFactory.StartNew(() => CloseReady.TrySetResult(true));
            return true;
        }

        protected override void OnBeforeClose(IWebBrowser control, IBrowser browser)
        {
            if (!browser.IsPopup) Cef.UIThreadTaskFactory.StartNew(() => Closed.TrySetResult(true));
        }
    }
}
