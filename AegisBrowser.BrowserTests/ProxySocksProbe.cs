using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AegisBrowser;
using CefSharp;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Research only: a SOCKS5 fixture that never connects to external destinations.
    private static async Task ObserveSocksThroughProxy(BrowserView browser, IRequestContext context, SocksTrace trace)
    {
        using var proxy = new SocksFixture(trace);
        var configured = await Cef.UIThreadTaskFactory.StartNew(() =>
        {
            if (!context.CanSetPreference("proxy")) return (false, "Proxy preference is read-only.");
            var settings = new Dictionary<string, object> { ["mode"] = "fixed_servers", ["server"] = "socks5://127.0.0.1:" + proxy.Port };
            var ok = context.SetPreference("proxy", settings, out var error); return (ok, error);
        });
        if (!configured.Item1) throw new IOException("CEF rejected the test SOCKS5 preference: " + configured.Item2);
        trace.Add("PROXY_EVENT", "PREFERENCE_APPLIED");

        const string url = "http://aegis-socks.invalid/fixture";
        await LoadReady(browser, url);
        await Wait(() => proxy.FixtureRequests > 0, "CEF request through controlled SOCKS5 fixture");
        Check(proxy.DomainConnects > 0 && await Js(browser, "document.title") == "socks fixture",
            "CEF uses an unauthenticated SOCKS5 tunnel and supplies the controlled destination hostname");

        proxy.Stop();
        const string offlineUrl = "http://aegis-socks.invalid/offline";
        var failed = new TaskCompletionSource<CefErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unexpectedSuccess = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadingFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadingStarted = 0;
        EventHandler<LoadingStateChangedEventArgs> onLoading = (_, e) =>
        {
            trace.Add("NAVIGATION_EVENT", e.IsLoading ? "LOADING" : "NOT_LOADING");
            if (e.IsLoading) Interlocked.Exchange(ref loadingStarted, 1);
            else if (Volatile.Read(ref loadingStarted) != 0) loadingFinished.TrySetResult(true);
        };
        EventHandler<FrameLoadStartEventArgs> onStart = (_, e) =>
        {
            if (e.Frame.IsMain) trace.Add("NAVIGATION_EVENT", e.Url == offlineUrl ? "OFFLINE_COMMITTED" : "OTHER_MAIN_FRAME_START");
        };
        EventHandler<FrameLoadEndEventArgs> onEnd = (_, e) =>
        {
            if (!e.Frame.IsMain) return;
            var matches = e.Url == offlineUrl;
            trace.Add("NAVIGATION_EVENT", matches ? "OFFLINE_LOAD_END" : "OTHER_MAIN_FRAME_END", e.HttpStatusCode.ToString());
            if (matches && e.HttpStatusCode >= 200 && e.HttpStatusCode < 400) unexpectedSuccess.TrySetResult(e.HttpStatusCode);
        };
        EventHandler<LoadErrorEventArgs> onError = (_, e) =>
        {
            if (!e.Frame.IsMain) return;
            var matches = e.FailedUrl == offlineUrl;
            trace.Add("NAVIGATION_EVENT", matches ? "OFFLINE_LOAD_ERROR" : "OTHER_MAIN_FRAME_ERROR", e.ErrorCode.ToString());
            if (matches) failed.TrySetResult(e.ErrorCode);
        };
        browser.LoadingStateChanged += onLoading;
        browser.FrameLoadStart += onStart;
        browser.FrameLoadEnd += onEnd;
        browser.LoadError += onError;
        trace.DetachCallbacks = () =>
        {
            browser.LoadingStateChanged -= onLoading;
            browser.FrameLoadStart -= onStart;
            browser.FrameLoadEnd -= onEnd;
            browser.LoadError -= onError;
        };
        try
        {
            trace.Add("NAVIGATION_EVENT", "OFFLINE_LOAD_REQUESTED");
            browser.Load(offlineUrl);
            var terminal = await Task.WhenAny(failed.Task, unexpectedSuccess.Task, Task.Delay(TimeSpan.FromSeconds(25)));
            if (terminal == unexpectedSuccess.Task)
            {
                trace.Add("NAVIGATION_EVENT", "UNEXPECTED_SUCCESS", (await unexpectedSuccess.Task).ToString());
                throw new InvalidOperationException("The SOCKS-offline navigation unexpectedly succeeded.");
            }
            if (terminal != failed.Task)
            {
                trace.Add("NAVIGATION_EVENT", "TIMEOUT_NO_MATCHING_LOAD_ERROR");
                throw new TimeoutException("No matching SOCKS-offline load error was observed within 25 seconds.");
            }
            var error = await failed.Task;
            trace.Add("NAVIGATION_EVENT", error == CefErrorCode.Aborted ? "NAVIGATION_CANCELLED" : "LOAD_ERROR_OBSERVED", error.ToString());
            Check(error != CefErrorCode.None && error != CefErrorCode.Aborted,
                "CEF reports a non-cancellation load error after the controlled SOCKS5 listener stops");
            await loadingFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!unexpectedSuccess.Task.IsCompleted, "SOCKS-offline navigation did not finish with a successful HTTP response");
            trace.Add("NAVIGATION_EVENT", "LOADING_SETTLED");
        }
        finally
        {
            // Keep observing through browser close, including a late
            // cancellation after a timeout. The owner detaches the callbacks.
            trace.Add("NAVIGATION_EVENT", "OFFLINE_WAIT_ENDED");
        }
    }

    // Test-only, bounded and monotonic. Event data contains no request URLs or credentials.
    private sealed class SocksTrace
    {
        private const int Limit = 128;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly string run = Guid.NewGuid().ToString("N");
        private readonly object gate = new();
        private readonly List<string> events = [];
        private int dropped;
        internal Action? DetachCallbacks { get; set; }

        internal void Detach()
        {
            try { DetachCallbacks?.Invoke(); }
            finally { DetachCallbacks = null; }
        }

        internal void Add(string category, string kind, string status = "none")
        {
            lock (gate)
            {
                if (events.Count >= Limit) { dropped++; return; }
                events.Add($"P404_{category}: run={run} sequence={events.Count + 1} elapsed_ms={elapsed.ElapsedMilliseconds} kind={kind} status={status}");
            }
        }

        internal void Print()
        {
            lock (gate)
            {
                foreach (var item in events) Console.WriteLine(item);
                Console.WriteLine($"P404_TRACE: run={run} recorded={events.Count} dropped={dropped}");
            }
        }
    }

    private sealed class SocksFixture : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentBag<Task> clients = new();
        private readonly Task loop;
        private readonly SocksTrace trace;
        private int domainConnects, fixtureRequests;
        private bool stopped;
        public int Port { get; }
        public int DomainConnects => Volatile.Read(ref domainConnects);
        public int FixtureRequests => Volatile.Read(ref fixtureRequests);

        public SocksFixture(SocksTrace trace)
        {
            this.trace = trace;
            listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            trace.Add("PROXY_EVENT", "LISTENER_READY");
            loop = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    trace.Add("PROXY_EVENT", "CONNECTION_ACCEPTED");
                    clients.Add(Serve(client, stop.Token));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        }

        private async Task Serve(TcpClient client, CancellationToken token)
        {
            try
            {
                using (client)
                {
                    using var stream = client.GetStream();
                    var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, token);
                    if (greeting[0] != 5 || greeting[1] is 0 or > 16) return;
                    var methods = new byte[greeting[1]]; await stream.ReadExactlyAsync(methods, token);
                    if (!methods.Contains((byte)0)) return;
                    await stream.WriteAsync(new byte[] { 5, 0 }, token);
                    var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
                    if (header[0] != 5 || header[1] != 1 || header[2] != 0 || header[3] != 3) return;
                    var length = new byte[1]; await stream.ReadExactlyAsync(length, token);
                    if (length[0] == 0) return;
                    var name = new byte[length[0]]; await stream.ReadExactlyAsync(name, token);
                    var port = new byte[2]; await stream.ReadExactlyAsync(port, token);
                    if (Encoding.ASCII.GetString(name) != "aegis-socks.invalid" || port[0] != 0 || port[1] != 80) return;
                    Interlocked.Increment(ref domainConnects);
                    trace.Add("PROXY_EVENT", "SOCKS_DESTINATION_ACCEPTED");
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, token);
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                    var first = await reader.ReadLineAsync(token);
                    if (first is null || !first.StartsWith("GET /fixture ", StringComparison.Ordinal)) return;
                    for (var i = 0; i < 100; i++)
                    {
                        var line = await reader.ReadLineAsync(token); if (string.IsNullOrEmpty(line)) break;
                        if (line.Length > 8192) return;
                    }
                    Interlocked.Increment(ref fixtureRequests);
                    trace.Add("PROXY_EVENT", "FIXTURE_REQUEST_RECEIVED");
                    const string body = "<!doctype html><title>socks fixture</title>";
                    var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n" + body);
                    await stream.WriteAsync(response, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
        }

        public void Stop()
        {
            if (stopped) return;
            trace.Add("PROXY_EVENT", "LISTENER_STOP_REQUESTED");
            stopped = true; stop.Cancel(); listener.Stop(); loop.GetAwaiter().GetResult();
            Task.WhenAll(clients.ToArray()).GetAwaiter().GetResult();
            trace.Add("PROXY_EVENT", "LISTENER_STOPPED");
        }

        public void Dispose() { Stop(); stop.Dispose(); }
    }
}
