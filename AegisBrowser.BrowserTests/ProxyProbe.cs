using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AegisBrowser;
using CefSharp;
using CefSharp.WinForms;

namespace AegisBrowser.BrowserTests;

// Research-only fixture: the production application still blocks required proxies.
internal static partial class Program
{
    private static int ProxyProbe(string profileRoot)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var root = Path.GetFullPath(profileRoot); Directory.CreateDirectory(root);
        var initialized = false; var result = 0;
        try
        {
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(new CefSettings
            {
                RootCachePath = root, CachePath = Path.Combine(root, "cache"),
                CommandLineArgsDisabled = true, LogFile = Path.Combine(root, "cef-proxy-probe.log")
            });
            if (!initialized) throw new IOException("CEF initialization failed.");
            using var context = Cef.GetGlobalRequestContext();
            using var origin = new LocalHttpFixture("origin");
            using var proxy = new LocalHttpFixture("proxy");
            using var host = new Form { Width = 800, Height = 600, Text = "Local CEF proxy probe" };
            host.Shown += async (_, _) =>
            {
                BrowserView? browser = null;
                try
                {
                    async Task Configure(bool removeLoopbackBypass)
                    {
                        var configuration = new Dictionary<string, object>
                        {
                            ["mode"] = "fixed_servers", ["server"] = "http://127.0.0.1:" + proxy.Port
                        };
                        // Test only. Chromium documents security consequences of proxying localhost.
                        if (removeLoopbackBypass) configuration["bypass_list"] = "<-loopback>";
                        var outcome = await Cef.UIThreadTaskFactory.StartNew(() =>
                        {
                            if (!context.CanSetPreference("proxy")) return (false, "Proxy preference is read-only.");
                            var success = context.SetPreference("proxy", configuration, out var error);
                            return (success, error);
                        });
                        if (!outcome.Item1) throw new IOException("CEF rejected the test proxy preference: " + outcome.Item2);
                    }

                    await Configure(false);
                    browser = new BrowserView("about:blank") { Dock = DockStyle.Fill };
                    browser.RequestHandler = new SafeRequests(Console.WriteLine);
                    Observe(browser); host.Controls.Add(browser); await Ready(browser);
                    var baseUrl = "http://127.0.0.1:" + origin.Port;
                    await LoadReady(browser, baseUrl + "/default-bypass");
                    await Wait(() => origin.Saw("/default-bypass"), "direct loopback observation");
                    Check(!proxy.Saw("/default-bypass"), "CEF default proxy settings bypass loopback");

                    await Configure(true);
                    await LoadReady(browser, baseUrl + "/proxied");
                    await Wait(() => proxy.Saw("/proxied"), "proxied loopback observation");
                    Check(!origin.Saw("/proxied"), "CEF test-only bypass override routed HTTP to local proxy");

                    proxy.Stop();
                    var offlineUrl = baseUrl + "/offline";
                    var failed = new TaskCompletionSource<CefErrorCode>(TaskCreationOptions.RunContinuationsAsynchronously);
                    EventHandler<LoadErrorEventArgs> onError = (_, e) =>
                    { if (e.Frame.IsMain && e.FailedUrl == offlineUrl) failed.TrySetResult(e.ErrorCode); };
                    browser.LoadError += onError;
                    try
                    {
                        browser.Load(offlineUrl);
                        var error = await failed.Task.WaitAsync(TimeSpan.FromSeconds(25));
                        Check(error != CefErrorCode.None, "offline proxy reports a Chromium load error");
                        await Task.Delay(500);
                        Check(!origin.Saw("/offline"), "offline proxy did not fall back to the local HTTP origin in this fixture");
                    }
                    finally { browser.LoadError -= onError; }
                    Console.WriteLine($"PASS: {checks} CEF proxy probe checks");
                }
                catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
                finally
                {
                    if (browser is not null && browser.IsBrowserInitialized)
                    {
                        try { await CloseWeb(browser); }
                        catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
                    }
                    host.Close();
                }
            };
            Application.Run(host);
        }
        catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
        finally { if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); } }
        if (result == 0) Console.WriteLine("PASS: CEF proxy probe shutdown completed");
        return result;
    }

    private sealed class LocalHttpFixture : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new();
        private readonly ConcurrentQueue<string> requests = new();
        private readonly ConcurrentBag<Task> clients = new();
        private readonly string label;
        private readonly Task loop;
        private bool stopped;
        public int Port { get; }

        public LocalHttpFixture(string label)
        {
            this.label = label;
            listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = Task.Run(AcceptLoop);
        }

        public bool Saw(string path) => requests.Any(line => line.Contains(path, StringComparison.Ordinal));

        private async Task AcceptLoop()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    clients.Add(Serve(client, cancellation.Token));
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (SocketException) when (cancellation.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (cancellation.IsCancellationRequested) { }
        }

        private async Task Serve(TcpClient client, CancellationToken token)
        {
            try
            {
                using (client)
                {
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                    var first = await reader.ReadLineAsync(token);
                    if (first is null || first.Length > 8192) return;
                    for (var i = 0; i < 100; i++)
                    {
                        var line = await reader.ReadLineAsync(token);
                        if (string.IsNullOrEmpty(line)) break;
                        if (line.Length > 8192) throw new IOException("Fixture request headers are too large.");
                    }
                    requests.Enqueue(first); // Only a controlled request line, never headers or credentials.
                    var body = Encoding.ASCII.GetBytes("<!doctype html><title>" + label + "</title><p>local fixture</p>");
                    var headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, token);
                    await stream.WriteAsync(body, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (IOException) { } // The test browser can abandon speculative connections.
            catch (SocketException) { }
            catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        }

        public void Stop()
        {
            if (stopped) return;
            stopped = true; cancellation.Cancel(); listener.Stop();
            loop.GetAwaiter().GetResult();
            Task.WhenAll(clients.ToArray()).GetAwaiter().GetResult();
        }

        public void Dispose() { Stop(); cancellation.Dispose(); }
    }
}
