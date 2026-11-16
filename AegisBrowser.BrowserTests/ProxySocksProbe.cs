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
        using (var closedListenerControl = new TcpClient(AddressFamily.InterNetwork))
        {
            var refused = false;
            try { await closedListenerControl.ConnectAsync(IPAddress.Loopback, proxy.Port).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { refused = true; }
            Check(refused, "stopped SOCKS listener independently refuses a new loopback connection");
            trace.Add("PROXY_EVENT", "STOPPED_LISTENER_CONTROL", "ConnectionRefused");
        }
        const string offlineUrl = "http://aegis-socks.invalid/offline";
        var navigation = new SocksNavigationObserver(offlineUrl);
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
            navigation.ObserveEnd(true, e.Url, e.HttpStatusCode);
        };
        EventHandler<LoadErrorEventArgs> onError = (_, e) =>
        {
            if (!e.Frame.IsMain) return;
            var matches = e.FailedUrl == offlineUrl;
            trace.Add("NAVIGATION_EVENT", matches ? "OFFLINE_LOAD_ERROR" : "OTHER_MAIN_FRAME_ERROR", e.ErrorCode.ToString());
            navigation.ObserveError(true, e.FailedUrl, e.ErrorCode);
        };
        browser.LoadingStateChanged += onLoading;
        browser.FrameLoadStart += onStart;
        browser.FrameLoadEnd += onEnd;
        browser.LoadError += onError;
        trace.DetachCallbacks = () =>
        {
            navigation.MarkBrowserClosed();
            var final = navigation.Snapshot;
            trace.Add("NAVIGATION_EVENT", "OFFLINE_FINAL_OUTCOME", final.Outcome.ToString());
            if (final.ContradictorySuccess) trace.PostCloseViolation = true;
            browser.LoadingStateChanged -= onLoading;
            browser.FrameLoadStart -= onStart;
            browser.FrameLoadEnd -= onEnd;
            browser.LoadError -= onError;
        };
        try
        {
            trace.Add("NAVIGATION_EVENT", "OFFLINE_LOAD_REQUESTED");
            browser.Load(offlineUrl);
            SocksNavigationOutcome outcome;
            try { outcome = await navigation.Completion.WaitAsync(TimeSpan.FromSeconds(25)); }
            catch (TimeoutException)
            {
                navigation.MarkTimedOut();
                trace.Add("NAVIGATION_EVENT", "TIMEOUT_NO_MATCHING_LOAD_ERROR");
                trace.Print("OFFLINE_TIMEOUT");
                throw new TimeoutException("No matching SOCKS-offline load error was observed within 25000 ms.");
            }
            trace.Add("NAVIGATION_EVENT", "OFFLINE_OUTCOME", outcome.ToString());
            Check(outcome == SocksNavigationOutcome.NetworkFailure,
                "CEF reports a non-cancellation load error after the controlled SOCKS5 listener stops");
            await loadingFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!navigation.Snapshot.ContradictorySuccess, "SOCKS-offline navigation did not finish with a successful HTTP response");
            trace.Add("NAVIGATION_EVENT", "LOADING_SETTLED");
        }
        finally
        {
            // Keep observing through browser close, including a late
            // cancellation after a timeout. The owner detaches the callbacks.
            trace.Add("NAVIGATION_EVENT", "OFFLINE_WAIT_ENDED");
        }
    }

    // A protocol rejection is a separate case from a stopped listener. The
    // local origin is independently reachable, so direct fallback is observable.
    private static async Task ObserveSocksRejection(BrowserView browser, IRequestContext context, SocksTrace trace)
    {
        using var origin = new LocalHttpFixture("socks-direct-control");
        using var proxy = new SocksFixture(trace, rejectPort: origin.Port);
        using (var directControl = new TcpClient(AddressFamily.InterNetwork))
        {
            await directControl.ConnectAsync(IPAddress.Loopback, origin.Port).WaitAsync(TimeSpan.FromSeconds(5));
            await directControl.GetStream().WriteAsync("GET /socks-direct-control HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n"u8.ToArray());
            await Wait(() => origin.SawDirect("/socks-direct-control"), "SOCKS direct-origin positive control");
        }
        var configured = await Cef.UIThreadTaskFactory.StartNew(() =>
        {
            var settings = new Dictionary<string, object>
            {
                ["mode"] = "fixed_servers", ["server"] = "socks5://127.0.0.1:" + proxy.Port,
                // Test-only: remove Chromium's implicit loopback bypass so
                // a direct fallback would be visible to this controlled origin.
                ["bypass_list"] = "<-loopback>"
            };
            return context.SetPreference("proxy", settings, out _);
        });
        Check(configured, "controlled SOCKS rejection proxy preference was accepted");
        var url = "http://127.0.0.1:" + origin.Port + "/socks-rejected";
        var failed = new TaskCompletionSource<CefErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var success = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadingStarted = 0;
        EventHandler<LoadErrorEventArgs> onError = (_, e) =>
        {
            if (e.Frame.IsMain && e.FailedUrl == url)
            {
                trace.Add("NAVIGATION_EVENT", "REJECT_LOAD_ERROR", e.ErrorCode.ToString());
                failed.TrySetResult(e.ErrorCode);
            }
        };
        EventHandler<FrameLoadEndEventArgs> onEnd = (_, e) =>
        {
            if (e.Frame.IsMain && e.Url == url && e.HttpStatusCode is >= 200 and < 400)
                success.TrySetResult(e.HttpStatusCode);
        };
        EventHandler<LoadingStateChangedEventArgs> onLoading = (_, e) =>
        {
            if (e.IsLoading) Interlocked.Exchange(ref loadingStarted, 1);
            else if (Volatile.Read(ref loadingStarted) != 0) settled.TrySetResult(true);
        };
        browser.LoadError += onError;
        browser.FrameLoadEnd += onEnd;
        browser.LoadingStateChanged += onLoading;
        try
        {
            trace.Add("NAVIGATION_EVENT", "REJECT_LOAD_REQUESTED");
            browser.Load(url);
            var sentOrSuccess = await Task.WhenAny(proxy.RejectionSent.Task, success.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            if (sentOrSuccess == success.Task)
                throw new InvalidOperationException("The SOCKS rejection navigation unexpectedly succeeded.");
            if (sentOrSuccess != proxy.RejectionSent.Task)
                throw new TimeoutException("The controlled SOCKS server did not send its rejection.");
            Check(proxy.RejectionSent.Task.IsCompletedSuccessfully, "controlled SOCKS5 denial reply was sent");
            var error = await AwaitSocksOutcome(failed.Task, success.Task, TimeSpan.FromSeconds(25), trace, "SOCKS rejection");
            Check(error == CefErrorCode.SocksConnectionFailed,
                "explicit SOCKS rejection produces Chromium's SOCKS connection failure");
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Allow queued loopback accepts to reach the independently owned
            // origin before inspecting its bounded absence evidence.
            await Task.Delay(500);
            Check(!success.Task.IsCompleted && !origin.Saw("/socks-rejected"),
                "explicit SOCKS rejection did not reach the independently reachable origin");
            trace.Add("NAVIGATION_EVENT", "REJECT_TERMINAL", error.ToString());
        }
        finally
        {
            browser.LoadError -= onError;
            browser.FrameLoadEnd -= onEnd;
            browser.LoadingStateChanged -= onLoading;
        }
    }

    private static async Task<CefErrorCode> AwaitSocksOutcome(Task<CefErrorCode> failed, Task<int> unexpectedSuccess,
        TimeSpan timeout, SocksTrace trace, string scenario = "SOCKS-offline")
    {
        var terminal = await Task.WhenAny(failed, unexpectedSuccess, Task.Delay(timeout));
        if (terminal == unexpectedSuccess)
        {
            trace.Add("NAVIGATION_EVENT", "UNEXPECTED_SUCCESS", (await unexpectedSuccess).ToString());
            throw new InvalidOperationException($"The {scenario} navigation unexpectedly succeeded.");
        }
        if (terminal != failed)
        {
            trace.Add("NAVIGATION_EVENT", "TIMEOUT_NO_MATCHING_LOAD_ERROR");
            // Preserve the evidence before browser teardown, which may stall.
            trace.Print("TERMINAL_TIMEOUT");
            throw new TimeoutException($"No matching {scenario} load error was observed within {timeout.TotalMilliseconds:0} ms.");
        }
        return await failed;
    }

    private static async Task VerifySocksOutcomeFailures()
    {
        var synthetic = new SocksTrace("SYNTHETIC");
        var missing = new TaskCompletionSource<CefErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var success = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOut = false;
        try { await AwaitSocksOutcome(missing.Task, success.Task, TimeSpan.FromMilliseconds(20), synthetic); }
        catch (TimeoutException) { timedOut = true; }
        Check(timedOut, "synthetic missing SOCKS callback remains a timeout");

        var rejected = false;
        try { await AwaitSocksOutcome(missing.Task, Task.FromResult(200), TimeSpan.FromSeconds(1), synthetic); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "synthetic successful offline navigation is rejected");

        var overflow = new SocksTrace("SYNTHETIC");
        for (var i = 0; i < 129; i++) overflow.Add("NAVIGATION_EVENT", "SYNTHETIC");
        Check(overflow.Dropped == 1, "synthetic SOCKS trace reports dropped events");
    }

    private static async Task ObserveSocksCancellation(BrowserView browser, IRequestContext context, SocksTrace trace)
    {
        using var proxy = new SocksFixture(trace, holdRequest: true);
        var configured = await Cef.UIThreadTaskFactory.StartNew(() =>
        {
            var settings = new Dictionary<string, object> { ["mode"] = "fixed_servers", ["server"] = "socks5://127.0.0.1:" + proxy.Port };
            return context.SetPreference("proxy", settings, out _);
        });
        Check(configured, "controlled cancellation proxy preference was accepted");
        const string url = "http://aegis-socks.invalid/hold";
        var cancelled = new SocksNavigationObserver(url);
        EventHandler<LoadErrorEventArgs> onError = (_, e) =>
        { cancelled.ObserveError(e.Frame.IsMain, e.FailedUrl, e.ErrorCode); };
        browser.LoadError += onError;
        try
        {
            trace.Add("NAVIGATION_EVENT", "HOLD_LOAD_REQUESTED");
            browser.Load(url);
            await proxy.HeldRequest.Task.WaitAsync(TimeSpan.FromSeconds(15));
            trace.Add("NAVIGATION_EVENT", "STOP_REQUESTED_AFTER_SERVER_OBSERVATION");
            cancelled.NoteCancellationRequested();
            browser.Stop();
            var outcome = await cancelled.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            trace.Add("NAVIGATION_EVENT", "CONTROLLED_CANCELLATION", cancelled.Snapshot.Error.ToString());
            Check(outcome == SocksNavigationOutcome.ExplicitCancellation && cancelled.Snapshot.Error == CefErrorCode.Aborted,
                "a deliberately canceled SOCKS navigation is reported as Aborted");
        }
        finally { browser.LoadError -= onError; }
    }

    // Test-only, bounded and monotonic. Event data contains no request URLs or credentials.
    private sealed class SocksTrace
    {
        private const int Limit = 128;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly string run = Guid.NewGuid().ToString("N");
        private readonly string evidence;
        private readonly object gate = new();
        private readonly List<string> events = [];
        private int dropped;
        internal bool PostCloseViolation { get; set; }
        internal SocksTrace(string evidence = "NATIVE_CEF") => this.evidence = evidence;
        internal int Dropped { get { lock (gate) return dropped; } }
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
                events.Add($"P404_{category}: run={run} evidence={evidence} sequence={events.Count + 1} elapsed_ms={elapsed.ElapsedMilliseconds} kind={kind} status={status}");
            }
        }

        internal void Print(string phase = "FINAL")
        {
            lock (gate)
            {
                foreach (var item in events) Console.WriteLine(item);
                Console.WriteLine($"P404_TRACE: run={run} evidence={evidence} phase={phase} recorded={events.Count} dropped={dropped}");
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
        private readonly bool holdRequest;
        private readonly int? rejectPort;
        internal TaskCompletionSource<bool> HeldRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> RejectionSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int domainConnects, fixtureRequests;
        private bool stopped;
        public int Port { get; }
        public int DomainConnects => Volatile.Read(ref domainConnects);
        public int FixtureRequests => Volatile.Read(ref fixtureRequests);

        public SocksFixture(SocksTrace trace, bool holdRequest = false, int? rejectPort = null)
        {
            this.trace = trace;
            this.holdRequest = holdRequest;
            this.rejectPort = rejectPort;
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
                    if (header[0] != 5 || header[1] != 1 || header[2] != 0) return;
                    string host;
                    if (header[3] == 3)
                    {
                        var length = new byte[1]; await stream.ReadExactlyAsync(length, token);
                        if (length[0] == 0) return;
                        var name = new byte[length[0]]; await stream.ReadExactlyAsync(name, token);
                        host = Encoding.ASCII.GetString(name);
                    }
                    else if (header[3] == 1)
                    {
                        var address = new byte[4]; await stream.ReadExactlyAsync(address, token);
                        host = new IPAddress(address).ToString();
                    }
                    else return;
                    var port = new byte[2]; await stream.ReadExactlyAsync(port, token);
                    var destinationPort = (port[0] << 8) | port[1];
                    if (rejectPort is int expectedPort)
                    {
                        if (host != "127.0.0.1" || destinationPort != expectedPort) return;
                        trace.Add("PROXY_EVENT", "SOCKS_REJECT_DESTINATION_ACCEPTED");
                        // RFC 1928: VER=5, REP=2 (ruleset denial), RSV=0,
                        // IPv4 bound address/port both zero. Close after reply.
                        await stream.WriteAsync(new byte[] { 5, 2, 0, 1, 0, 0, 0, 0, 0, 0 }, token);
                        trace.Add("PROXY_EVENT", "SOCKS_REJECTION_SENT");
                        RejectionSent.TrySetResult(true);
                        return;
                    }
                    if (host != "aegis-socks.invalid" || destinationPort != 80 || header[3] != 3) return;
                    Interlocked.Increment(ref domainConnects);
                    trace.Add("PROXY_EVENT", "SOCKS_DESTINATION_ACCEPTED");
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, token);
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                    var first = await reader.ReadLineAsync(token);
                    var expected = holdRequest ? "GET /hold " : "GET /fixture ";
                    if (first is null || !first.StartsWith(expected, StringComparison.Ordinal)) return;
                    for (var i = 0; i < 100; i++)
                    {
                        var line = await reader.ReadLineAsync(token); if (string.IsNullOrEmpty(line)) break;
                        if (line.Length > 8192) return;
                    }
                    if (holdRequest)
                    {
                        trace.Add("PROXY_EVENT", "HELD_REQUEST_RECEIVED");
                        HeldRequest.TrySetResult(true);
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        return;
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
