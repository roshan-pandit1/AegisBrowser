using System.Net;
using System.Net.Sockets;
using System.Text;
using AegisBrowser;
using AegisBrowser.Core;
using CefSharp;
using CefSharp.Handler;
using CefSharp.WinForms;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Test-only probe. Production continues to use DenyDownloads.
    private static int DownloadCallbackProbe(string profileRoot, string mode)
    {
        if (mode is not ("decline" or "accept" or "cancel" or "interrupt" or "close-tab" or "held-staging" or "held-ancestor-staging" or "barrier-close")) return 2;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var root = Path.GetFullPath(profileRoot);
        if (mode == "held-ancestor-staging")
        {
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
                Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" ||
                Environment.GetEnvironmentVariable("AEGIS_DISPOSABLE_DOWNLOAD_TEST") != "1")
                throw new InvalidOperationException("Holding the full ancestor chain requires an acknowledged disposable GitHub-hosted Windows runner.");
            var runnerTemp = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? throw new IOException("Runner temporary directory is unavailable."));
            var relativeRoot = Path.GetRelativePath(runnerTemp, root);
            if (Path.IsPathRooted(relativeRoot) || relativeRoot == ".." || relativeRoot.StartsWith("..\\", StringComparison.Ordinal))
                throw new IOException("The fixture must be created below runner temp.");
        }
        // This is an owned test staging location, never a user-selected path.
        // It is not the qualified production staging implementation.
        var downloads = Path.Combine(root, "test-owned-staging");
        Directory.CreateDirectory(downloads);
        var target = Path.Combine(downloads, "chosen.txt");
        if (Directory.EnumerateFileSystemEntries(downloads).Any()) throw new IOException("Probe output must start empty.");
        var marker = Path.Combine(downloads, ".aegis-fixture-owner");
        using var heldMarker = mode == "held-ancestor-staging"
            ? new FileStream(marker, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read)
            : null;
        if (heldMarker is null) using (new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
        using var stagingHandle = mode == "held-staging" ? DownloadDirectoryObservation.OpenWithListAccessForTest(downloads) : null;
        using var ancestorHandles = mode == "held-ancestor-staging" ? HeldDirectoryChain.Open(downloads) : null;
        var stagingIdentity = stagingHandle?.Identity;
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
            var bodyLength = mode is "cancel" or "interrupt" or "close-tab" or "barrier-close" ? 1024 * 1024 : payload.Length;
            var headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=fixture.txt\r\nContent-Length: " + bodyLength + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers);
            await stream.WriteAsync(mode is "cancel" or "interrupt" or "close-tab" or "barrier-close" ? new byte[65536] : payload);
            await stream.FlushAsync();
            if (mode == "interrupt") listener.Stop();
            responseServed.TrySetResult(true);
            if (mode is "cancel" or "close-tab" or "barrier-close") await releaseBody.Task;
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
                    if (mode is "accept" or "held-staging" or "held-ancestor-staging")
                    {
                        await handler.Complete.Task.WaitAsync(TimeSpan.FromSeconds(25));
                        Check(File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(payload), "native download wrote exact fixture bytes");
                        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(target)));
                        Check(hash == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)), "native download SHA-256 matches fixture");
                        Console.WriteLine("DOWNLOAD_FIXTURE_SHA256: " + hash);
                        if (stagingHandle is not null)
                            Check(stagingHandle.ReadHeldIdentity() == stagingIdentity, "held staging directory identity remains stable while CEF writes");
                        if (ancestorHandles is not null)
                        {
                            ancestorHandles.Verify();
                            Console.WriteLine("ANCESTOR_CHAIN_OBSERVATION: held_count=" + ancestorHandles.Count + " retained_and_reopened_identities_match=true marker_handle_held=true");
                        }
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
                    else if (mode == "barrier-close")
                    {
                        await handler.Partial.Task.WaitAsync(TimeSpan.FromSeconds(25));
                        var closeBarrier = await Cef.UIThreadTaskFactory.StartNew(handler.RequestBarrierClose);
                        releaseBody.TrySetResult(true);
                        await closeBarrier.WaitAsync(TimeSpan.FromSeconds(25));
                        if (!handler.Cancelled.Task.IsCompletedSuccessfully) throw new IOException("Cancellation was not observed before browser close.");
                        Console.WriteLine("DOWNLOAD_BARRIER_OBSERVATION: cancellation confirmed before native browser close");
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
                    if (mode == "barrier-close" && handler.PendingNative)
                    {
                        // The test has failed, but closing this form would destroy
                        // the browser while CEF still has a tracked download. Keep
                        // it alive until the disposable runner is torn down.
                        result = 1;
                        Console.Error.WriteLine("FAILED: download terminal state unconfirmed; native browser retained");
                    }
                    else
                    {
                        try
                        {
                            await Cef.UIThreadTaskFactory.StartNew(handler.ReleaseRetained);
                            if (!webClosed) { await CloseWeb(web); form.Controls.Remove(web); web.Dispose(); }
                        }
                        catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
                        form.Close();
                    }
                }
            };
            Application.Run(form);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
        finally { if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); } }
        if (result == 0)
        {
            if (!File.Exists(marker)) { Console.Error.WriteLine("FAIL: fixture ownership marker disappeared"); return 1; }
            var files = Directory.EnumerateFileSystemEntries(downloads).Where(path => path != marker).ToArray();
            if (mode is "decline" or "cancel" or "interrupt" or "barrier-close" && files.Length != 0)
            {
                foreach (var file in files) Console.Error.WriteLine("DOWNLOAD_REMAINING_FILE: " + Path.GetFileName(file));
                Console.Error.WriteLine("FAIL: unsuccessful download left output files"); return 1;
            }
            if (mode is "accept" or "held-staging" or "held-ancestor-staging" && (files.Length != 1 || files[0] != target)) { Console.Error.WriteLine("FAIL: unexpected output files"); return 1; }
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
        private DownloadCloseBarrier? closeBarrier;
        private int browserId;
        private int downloadId;
        internal volatile bool PendingNative;
        internal string? Suggested { get; private set; }
        protected override bool CanDownload(IWebBrowser control, IBrowser browser, string url, string method) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1" && uri.Port == port;
        protected override bool OnBeforeDownload(IWebBrowser control, IBrowser browser, DownloadItem item, IBeforeDownloadCallback callback)
        {
            using (callback)
            {
                Suggested = item.SuggestedFileName;
                if (mode == "barrier-close")
                {
                    browserId = browser.Identifier;
                    downloadId = item.Id;
                    closeBarrier = new DownloadCloseBarrier();
                    if (!closeBarrier.TryRegister(browserId, downloadId, CancelRetained)) throw new InvalidOperationException("Could not register fixture download.");
                    PendingNative = true;
                }
                if (mode is "accept" or "cancel" or "interrupt" or "close-tab" or "held-staging" or "held-ancestor-staging" or "barrier-close") callback.Continue(target, false);
            }
            Before.TrySetResult(true);
            return true;
        }
        protected override void OnDownloadUpdated(IWebBrowser control, IBrowser browser, DownloadItem item, IDownloadItemCallback callback)
        {
            if (mode == "close-tab" && item.IsInProgress && item.ReceivedBytes > 0) Partial.TrySetResult(item.FullPath);
            if (mode is "cancel" or "barrier-close" && item.IsInProgress && item.ReceivedBytes > 0 && Interlocked.CompareExchange(ref retainedOnce, 1, 0) == 0)
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
            if (mode == "barrier-close" && (item.IsComplete || item.IsCancelled || item.IsInterrupted))
            {
                PendingNative = false;
                closeBarrier?.ObserveTerminal(browser.Identifier, item.Id);
            }
        }
        internal void CancelRetained()
        {
            using var callback = Interlocked.Exchange(ref retained, null) ?? throw new InvalidOperationException("No active callback.");
            callback.Cancel();
        }
        internal void ReleaseRetained() => Interlocked.Exchange(ref retained, null)?.Dispose();
        internal Task RequestBarrierClose()
        {
            var barrier = closeBarrier ?? throw new InvalidOperationException("Fixture barrier was not initialized.");
            var task = barrier.BeginClose();
            if (barrier.TryRegister(browserId, downloadId + 1, () => { })) throw new InvalidOperationException("Close accepted a new download.");
            return task;
        }
    }

    // Research fixture: retain every ancestor from the volume root to the test
    // stage, and compare path-reopened identities after a native transfer. It
    // does not validate owners, DACLs, existing handles or authorize a download.
    private sealed class HeldDirectoryChain : IDisposable
    {
        private readonly List<(string Path, DownloadDirectoryObservation Handle)> entries = [];
        internal int Count => entries.Count;

        internal static HeldDirectoryChain Open(string leaf)
        {
            var chain = new Stack<string>();
            for (var path = Path.GetFullPath(leaf); path is not null; path = Path.GetDirectoryName(path)) chain.Push(path);
            var result = new HeldDirectoryChain();
            try
            {
                while (chain.TryPop(out var path)) result.entries.Add((path, DownloadDirectoryObservation.OpenWithListAccessForTest(path)));
                result.Verify();
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        internal void Verify()
        {
            foreach (var (path, held) in entries)
            {
                using var reopened = DownloadDirectoryObservation.OpenWithListAccessForTest(path);
                if (held.ReadHeldIdentity() != held.Identity || reopened.Identity != held.Identity)
                    throw new IOException("A held fixture ancestor changed identity.");
            }
        }

        public void Dispose()
        {
            for (var i = entries.Count - 1; i >= 0; i--) entries[i].Handle.Dispose();
            entries.Clear();
        }
    }
}
