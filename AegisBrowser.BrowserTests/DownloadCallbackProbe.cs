using System.Net;
using System.Net.Sockets;
using System.Text;
using AegisBrowser;
using CefSharp;
using CefSharp.Handler;
using CefSharp.WinForms;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Test-only probe. Production continues to use DenyDownloads.
    private static int DownloadCallbackProbe(string profileRoot, string mode)
    {
        if (mode is not ("decline" or "accept" or "cancel" or "interrupt" or "close-tab")) return 2;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var root = Path.GetFullPath(profileRoot);
        var downloads = Path.Combine(root, "approved-output");
        Directory.CreateDirectory(downloads);
        var target = Path.Combine(downloads, "chosen.txt");
        if (Directory.EnumerateFileSystemEntries(downloads).Any()) throw new IOException("Probe output must start empty.");
        var payload = Encoding.UTF8.GetBytes("Aegis owned download callback fixture\n");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var responseServed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBody = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            while (await reader.ReadLineAsync() is { Length: > 0 }) { }
            var bodyLength = mode is "cancel" or "interrupt" or "close-tab" ? 1024 * 1024 : payload.Length;
            var headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=fixture.txt\r\nContent-Length: " + bodyLength + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers);
            await stream.WriteAsync(mode is "cancel" or "interrupt" or "close-tab" ? new byte[65536] : payload);
            await stream.FlushAsync();
            if (mode == "interrupt") listener.Stop();
            responseServed.TrySetResult(true);
            if (mode is "cancel" or "close-tab") await releaseBody.Task;
            if (mode == "close-tab") await stream.WriteAsync(new byte[1024 * 1024 - 65536]);
        });
        var handler = new CallbackProbeHandler(mode, target, port);
        var initialized = false;
        var result = 1;
        try
        {
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(new CefSettings
            {
                RootCachePath = root, CachePath = Path.Combine(root, "cache"),
                CommandLineArgsDisabled = true, LogFile = Path.Combine(root, "cef-download-probe.log")
            });
            if (!initialized) throw new IOException("CEF initialization failed.");
            using var form = new Form { Width = 500, Height = 300, Text = "Download callback fixture" };
            form.Shown += async (_, _) =>
            {
                var web = new BrowserView("about:blank") { Dock = DockStyle.Fill, LifeSpanHandler = new SafePopups(_ => { }), DownloadHandler = handler };
                form.Controls.Add(web);
                var webClosed = false;
                try
                {
                    await Ready(web);
                    web.Load($"http://127.0.0.1:{port}/attachment");
                    await responseServed.Task.WaitAsync(TimeSpan.FromSeconds(25));
                    await handler.Before.Task.WaitAsync(TimeSpan.FromSeconds(25));
                    Check(handler.Suggested == "fixture.txt", "Content-Disposition suggested filename reaches native callback");
                    if (mode == "accept")
                    {
                        await handler.Complete.Task.WaitAsync(TimeSpan.FromSeconds(25));
                        Check(File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(payload), "native download wrote exact fixture bytes");
                        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(target)));
                        Check(hash == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)), "native download SHA-256 matches fixture");
                        Console.WriteLine("DOWNLOAD_FIXTURE_SHA256: " + hash);
                    }
                    else if (mode == "cancel")
                    {
                        var partial = await handler.Partial.Task.WaitAsync(TimeSpan.FromSeconds(25));
                        Console.WriteLine("DOWNLOAD_PARTIAL_OBSERVATION: " + Path.GetFileName(partial));
                        foreach (var entry in Directory.EnumerateFileSystemEntries(downloads))
                            Console.WriteLine("DOWNLOAD_DIRECTORY_OBSERVATION: " + Path.GetFileName(entry));
                        await Cef.UIThreadTaskFactory.StartNew(handler.CancelRetained);
                        releaseBody.TrySetResult(true);
                        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(25));
                    }
                    else if (mode == "interrupt")
                    {
                        await handler.Interrupted.Task.WaitAsync(TimeSpan.FromSeconds(25));
                    }
                    else if (mode == "close-tab")
                    {
                        await handler.Partial.Task.WaitAsync(TimeSpan.FromSeconds(25));
                        await CloseWeb(web);
                        webClosed = true;
                        form.Controls.Remove(web);
                        web.Dispose();
                        releaseBody.TrySetResult(true);
                        await server.WaitAsync(TimeSpan.FromSeconds(25));
                        Console.WriteLine("DOWNLOAD_FIXTURE_BODY_WRITTEN: true");
                        var terminal = await Task.WhenAny(handler.Complete.Task, handler.Cancelled.Task, handler.Interrupted.Task).WaitAsync(TimeSpan.FromSeconds(25));
                        Console.WriteLine("DOWNLOAD_TAB_CLOSE_RESULT: " + (terminal == handler.Complete.Task ? "COMPLETED" : terminal == handler.Cancelled.Task ? "CANCELLED" : "INTERRUPTED"));
                    }
                    result = 0;
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                finally
                {
                    releaseBody.TrySetResult(true);
                    try
                    {
                        await Cef.UIThreadTaskFactory.StartNew(handler.ReleaseRetained);
                        if (!webClosed) { await CloseWeb(web); form.Controls.Remove(web); web.Dispose(); }
                    }
                    catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
                    form.Close();
                }
            };
            Application.Run(form);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
        finally { if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); } }
        if (result == 0)
        {
            var files = Directory.EnumerateFileSystemEntries(downloads).ToArray();
            if (mode is "decline" or "cancel" or "interrupt" && files.Length != 0)
            {
                foreach (var file in files) Console.Error.WriteLine("DOWNLOAD_REMAINING_FILE: " + Path.GetFileName(file));
                Console.Error.WriteLine("FAIL: unsuccessful download left output files"); return 1;
            }
            if (mode == "accept" && (files.Length != 1 || files[0] != target)) { Console.Error.WriteLine("FAIL: unexpected output files"); return 1; }
            Console.WriteLine($"PASS: native download callback probe ({mode}), CEF shutdown and output listing");
        }
        return result;
    }

    private sealed class CallbackProbeHandler(string mode, string target, int port) : DownloadHandler
    {
        internal TaskCompletionSource<bool> Before { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Interrupted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string> Partial { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IDownloadItemCallback? retained;
        private int retainedOnce;
        internal string? Suggested { get; private set; }
        protected override bool CanDownload(IWebBrowser control, IBrowser browser, string url, string method) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1" && uri.Port == port;
        protected override bool OnBeforeDownload(IWebBrowser control, IBrowser browser, DownloadItem item, IBeforeDownloadCallback callback)
        {
            using (callback)
            {
                Suggested = item.SuggestedFileName;
                if (mode is "accept" or "cancel" or "interrupt" or "close-tab") callback.Continue(target, false);
            }
            Before.TrySetResult(true);
            return true;
        }
        protected override void OnDownloadUpdated(IWebBrowser control, IBrowser browser, DownloadItem item, IDownloadItemCallback callback)
        {
            if (mode == "close-tab" && item.IsInProgress && item.ReceivedBytes > 0) Partial.TrySetResult(item.FullPath);
            if (mode == "cancel" && item.IsInProgress && item.ReceivedBytes > 0 && Interlocked.CompareExchange(ref retainedOnce, 1, 0) == 0)
            {
                retained = callback;
                Partial.TrySetResult(item.FullPath);
                return;
            }
            using (callback)
            {
                if (item.IsComplete) Complete.TrySetResult(true);
                else if (item.IsCancelled) Cancelled.TrySetResult(true);
                else if (item.IsInterrupted) { Interrupted.TrySetResult(true); Complete.TrySetException(new IOException("Native download did not complete.")); }
            }
        }
        internal void CancelRetained()
        {
            using var callback = Interlocked.Exchange(ref retained, null) ?? throw new InvalidOperationException("No active callback.");
            callback.Cancel();
        }
        internal void ReleaseRetained() => Interlocked.Exchange(ref retained, null)?.Dispose();
    }
}
