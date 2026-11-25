using AegisBrowser;
using AegisBrowser.Core;
using CefSharp;
using CefSharp.WinForms;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Reports only fixture phase names, event types, status and process-local
    // ordering; visited addresses and page titles are never written to output.
    private static int HistoryNavigationObserve(string root)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Directory.CreateDirectory(root);
        using var lease = new ProfileLease(root);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var server = ServeHistoryFixture(listener, stop.Token);
        var initialized = false;
        var outcome = 0;
        try
        {
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(new CefSettings { RootCachePath = root, CachePath = Path.Combine(root, "Cache"),
                CommandLineArgsDisabled = true, LogFile = Path.Combine(root, "chromium.log"), LogSeverity = LogSeverity.Warning });
            if (!initialized) throw new InvalidOperationException("CEF initialization failed.");
            using var host = new Form { Width = 900, Height = 600 };
            host.Shown += async (_, _) =>
            {
                var phase = "initial";
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var initialDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var initialSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<int>? ended = null;
                TaskCompletionSource? settled = null;
                TaskCompletionSource<CefErrorCode>? failed = null;
                TaskCompletionSource? addressChanged = null;
                void Trace(string name) => Console.WriteLine($"HISTORY_EVENT qpc={Stopwatch.GetTimestamp()} phase={Volatile.Read(ref phase)} {name}");
                var web = new BrowserView("about:blank") { Dock = DockStyle.Fill };
                web.LifeSpanHandler = new SafePopups(_ => { });
                web.RequestHandler = new SafeRequests(_ => { }, navigationObserver: (id, redirect) => Trace("before-browse browser=" + id + " redirect=" + redirect));
                web.IsBrowserInitializedChanged += (_, _) => { if (web.IsBrowserInitialized) ready.TrySetResult(); };
                web.AddressChanged += (_, e) =>
                {
                    var sameDocument = e.Address.Contains("/changed?fixture=1", StringComparison.Ordinal);
                    Trace("address browser=" + e.Browser.Identifier + " same-document=" + sameDocument);
                    if (sameDocument) Volatile.Read(ref addressChanged)?.TrySetResult();
                };
                web.TitleChanged += (_, e) => Trace("title browser=" + e.Browser.Identifier);
                web.LoadingStateChanged += (_, e) =>
                {
                    Trace("loading=" + e.IsLoading);
                    if (!e.IsLoading)
                    {
                        if (Volatile.Read(ref phase) == "initial") initialSettled.TrySetResult();
                        Volatile.Read(ref settled)?.TrySetResult();
                    }
                };
                web.FrameLoadStart += (_, e) => { if (e.Frame.IsMain) Trace("main-start transition=" + e.TransitionType); else Trace("subframe-start"); };
                web.FrameLoadEnd += (_, e) =>
                {
                    if (!e.Frame.IsMain) { Trace("subframe-end status=" + e.HttpStatusCode); return; }
                    Trace("main-end status=" + e.HttpStatusCode + " changed-url=" + e.Url.Contains("/changed?fixture=1", StringComparison.Ordinal));
                    if (Volatile.Read(ref phase) == "initial") initialDone.TrySetResult();
                    Volatile.Read(ref ended)?.TrySetResult(e.HttpStatusCode);
                };
                web.LoadError += (_, e) =>
                {
                    if (!e.Frame.IsMain) return;
                    Trace("main-error code=" + e.ErrorCode);
                    Volatile.Read(ref failed)?.TrySetResult(e.ErrorCode);
                };
                host.Controls.Add(web);
                try
                {
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(25));
                    await initialDone.Task.WaitAsync(TimeSpan.FromSeconds(25));
                    await initialSettled.Task.WaitAsync(TimeSpan.FromSeconds(25));
                    async Task Navigate(string label, Action action, int expected)
                    {
                        phase = label;
                        var current = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        Volatile.Write(ref ended, current);
                        Volatile.Write(ref settled, completed);
                        action();
                        var status = await current.Task.WaitAsync(TimeSpan.FromSeconds(25));
                        if (status != expected) throw new InvalidOperationException("Unexpected fixture status for " + label + ": " + status);
                        await completed.Task.WaitAsync(TimeSpan.FromSeconds(25));
                        Volatile.Write(ref ended, null);
                        Volatile.Write(ref settled, null);
                    }
                    var origin = "http://127.0.0.1:" + port;
                    await Navigate("success", () => web.Load(origin + "/page"), 200);
                    await Navigate("redirect", () => web.Load(origin + "/redirect"), 200);
                    await Navigate("http-error", () => web.Load(origin + "/error"), 404);
                    await Navigate("other", () => web.Load(origin + "/other"), 200);
                    await Navigate("reload", () => web.Reload(), 200);
                    await Navigate("back", () => web.Back(), 404);
                    await Navigate("forward", () => web.Forward(), 200);
                    phase = "same-document";
                    var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Volatile.Write(ref addressChanged, changed);
                    await Navigate("same-document", () => web.Load(origin + "/same"), 200);
                    await changed.Task.WaitAsync(TimeSpan.FromSeconds(25));
                    Volatile.Write(ref addressChanged, null);
                    phase = "connection-reset";
                    var failure = new TaskCompletionSource<CefErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var failureSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Volatile.Write(ref failed, failure);
                    Volatile.Write(ref settled, failureSettled);
                    web.Load(origin + "/reset");
                    var error = await failure.Task.WaitAsync(TimeSpan.FromSeconds(25));
                    if (error == CefErrorCode.Aborted) throw new InvalidOperationException("Connection reset was reported as an aborted navigation.");
                    await failureSettled.Task.WaitAsync(TimeSpan.FromSeconds(25));
                    Volatile.Write(ref failed, null);
                    Volatile.Write(ref settled, null);
                    Console.WriteLine("PASS: controlled history navigation observation");
                }
                catch (Exception ex) { outcome = 1; Console.Error.WriteLine("History observation failed: " + ex.GetType().Name + " phase=" + phase + " javascript-ready=" + web.CanExecuteJavascriptInMainFrame); }
                finally
                {
                    try { await CloseWeb(web); }
                    catch (Exception ex) { outcome = 1; Console.Error.WriteLine("History observation shutdown failed: " + ex.GetType().Name); }
                    host.Close();
                }
            };
            Application.Run(host);
        }
        finally
        {
            if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); }
            stop.Cancel(); listener.Stop();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
        return outcome;
    }

    private static async Task ServeHistoryFixture(TcpListener listener, CancellationToken token, TaskCompletionSource? slowRequest = null,
        TaskCompletionSource? pausedRequest = null, Task? releasePaused = null, TaskCompletionSource? abortRequest = null, Action? requestObserved = null)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                _ = Reply(client, token, slowRequest, pausedRequest, releasePaused, abortRequest, requestObserved);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        static async Task Reply(TcpClient client, CancellationToken token, TaskCompletionSource? slowRequest,
            TaskCompletionSource? pausedRequest, Task? releasePaused, TaskCompletionSource? abortRequest, Action? requestObserved)
        {
            using (client)
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var first = await reader.ReadLineAsync(token) ?? "";
                for (var i = 0; i < 100; i++) if (string.IsNullOrEmpty(await reader.ReadLineAsync(token))) break;
                var parts = first.Split(' ');
                var path = parts.Length > 1 ? parts[1].Split('?')[0] : "";
                requestObserved?.Invoke();
                if (path == "/reset") return;
                if (path == "/slow-abort" && abortRequest is not null)
                {
                    abortRequest.TrySetResult();
                    var untilCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await untilCancelled.Task.WaitAsync(token);
                    return;
                }
                if (path == "/slow-pause" && pausedRequest is not null && releasePaused is not null)
                {
                    pausedRequest.TrySetResult();
                    await releasePaused.WaitAsync(token);
                }
                if (path == "/slow" && slowRequest is not null)
                {
                    slowRequest.TrySetResult();
                    var untilCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await untilCancelled.Task.WaitAsync(token);
                    return;
                }
                var code = path == "/error" ? "404 Not Found" : path == "/redirect" ? "302 Found" : "200 OK";
                var script = path == "/same" ? "<script>history.pushState({}, '', '/changed?fixture=1');</script>" :
                    "<script>window.fixtureReady=true;document.addEventListener('keydown',e=>{if(e.ctrlKey&&e.key.toLowerCase()==='h'){window.historyKeyConsumed=true;e.preventDefault();}});</script>";
                var title = path.StartsWith("/canary-", StringComparison.Ordinal) && path.Length == 40 && path[8..].All(Uri.IsHexDigit)
                    ? "HistoryTitle" + path[8..] : "Fixture title";
                var child = path == "/iframe-parent" ? "<iframe src='/iframe-child'></iframe>" : "";
                var body = Encoding.UTF8.GetBytes("<!doctype html><link rel='icon' href='data:,'><title>" + title + "</title>" + script + child + "<p>Controlled page</p>");
                var headers = $"HTTP/1.1 {code}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n" +
                    (path == "/redirect" ? "Location: /page\r\n" : "") + "\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), token);
                await stream.WriteAsync(body, token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
        }
    }
}
