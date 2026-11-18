using System.Text.Json;
using CefSharp;
using CefSharp.Callback;

namespace P0ManagedHost;

// Research-only CDP observation. These trace events are not an OS handle and
// cannot establish renderer identity, sandbox policy or enforcement.
internal sealed class RendererTraceProbe : IDevToolsMessageObserver
{
    private const int StartId = 81001, FrameId = 81002, EndId = 81003;
    private readonly TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string?> frameId = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object sync = new();
    private readonly List<(string Frame, int Pid)> frames = new();
    private bool dataLoss;

    private static JsonDocument ParseBounded(Stream input)
    {
        const int maximumBytes = 1024 * 1024;
        using var copy = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = input.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            if (copy.Length + count > maximumBytes) throw new InvalidDataException("TRACE_EVENT_TOO_LARGE");
            copy.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(copy.ToArray());
    }

    internal static async Task ObserveAsync(IBrowser browser)
    {
        var observer = new RendererTraceProbe();
        IDisposable? registration = null;
        bool startSent = false, endSent = false;
        bool lifecycleUnconfirmed = false;
        try
        {
            var registrationTask = Cef.UIThreadTaskFactory.StartNew(
                () => browser.GetHost().AddDevToolsMessageObserver(observer));
            try { registration = await registrationTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                lifecycleUnconfirmed = true;
                // A timeout does not cancel registration. Dispose a late
                // registration on CEF's UI thread, and fail the fixture.
                _ = registrationTask.ContinueWith(t => {
                    if (t.Status == TaskStatus.RanToCompletion && t.Result is not null)
                        _ = Cef.UIThreadTaskFactory.StartNew(t.Result.Dispose);
                }, TaskScheduler.Default);
                throw;
            }
            if (registration is null) throw new InvalidOperationException("OBSERVER_UNAVAILABLE");
            // Submission can complete after our managed timeout; attempting a
            // bounded stop in finally does not assert native cancellation.
            startSent = true;
            var sent = await browser.ExecuteDevToolsMethodAsync(StartId, "Tracing.start", new Dictionary<string, object>
            {
                ["categories"] = "disabled-by-default-devtools.timeline",
                ["transferMode"] = "ReportEvents"
            }).WaitAsync(TimeSpan.FromSeconds(5));
            if (sent != StartId) { startSent = false; throw new InvalidOperationException("TRACE_START_NOT_SUBMITTED"); }
            if (!await observer.started.Task.WaitAsync(TimeSpan.FromSeconds(8)))
            { startSent = false; throw new InvalidOperationException("TRACE_START_REJECTED"); }
            sent = await browser.ExecuteDevToolsMethodAsync(FrameId, "Page.getFrameTree", new Dictionary<string, object>())
                .WaitAsync(TimeSpan.FromSeconds(5));
            if (sent != FrameId) throw new InvalidOperationException("FRAME_QUERY_NOT_SUBMITTED");
            string? mainFrame = await observer.frameId.Task.WaitAsync(TimeSpan.FromSeconds(5));
            sent = await browser.ExecuteDevToolsMethodAsync(EndId, "Tracing.end", new Dictionary<string, object>())
                .WaitAsync(TimeSpan.FromSeconds(5));
            if (sent != EndId) throw new InvalidOperationException("TRACE_END_NOT_SUBMITTED");
            if (!await observer.ended.Task.WaitAsync(TimeSpan.FromSeconds(8)))
                throw new InvalidOperationException("TRACE_END_REJECTED");
            endSent = true;
            await observer.completed.Task.WaitAsync(TimeSpan.FromSeconds(8));
            lock (observer.sync)
            {
                var matches = observer.frames.Where(x => x.Frame == mainFrame && x.Pid > 0).ToArray();
                string status = !observer.dataLoss && mainFrame is not null && matches.Length == 1
                    ? "FRAME_PROCESS_REPORTED_UNCORRELATED" : "INCONCLUSIVE";
                Console.WriteLine("MANAGED_TRACE: " + status + " MATCHES=" + matches.Length +
                    " DATA_LOSS=" + observer.dataLoss +
                    (matches.Length == 1 ? " CANDIDATE_PID=" + matches[0].Pid : "") +
                    " RENDERER_IDENTITY=INCONCLUSIVE");
                // A PID from this trace has not yet been bound to a retained OS
                // process handle or creation time. Never claim verification.
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("MANAGED_TRACE: INCONCLUSIVE " + ex.GetType().Name);
            if (ex is TimeoutException) lifecycleUnconfirmed = true;
        }
        finally
        {
            if (startSent && !endSent)
            {
                try
                {
                    await browser.ExecuteDevToolsMethodAsync(EndId, "Tracing.end", new Dictionary<string, object>())
                        .WaitAsync(TimeSpan.FromSeconds(3));
                    if (!await observer.ended.Task.WaitAsync(TimeSpan.FromSeconds(3))) lifecycleUnconfirmed = true;
                    else await observer.completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (Exception) { lifecycleUnconfirmed = true; }
            }
            if (registration is not null)
            {
                try { await Cef.UIThreadTaskFactory.StartNew(registration.Dispose).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { lifecycleUnconfirmed = true; }
            }
            if (lifecycleUnconfirmed) throw new InvalidOperationException("TRACE_LIFECYCLE_UNCONFIRMED");
        }
    }

    public bool OnDevToolsMessage(IBrowser browser, Stream message) => false;
    public void OnDevToolsMethodResult(IBrowser browser, int messageId, bool success, Stream result)
    {
        try
        {
            if (messageId == StartId) started.TrySetResult(success);
            else if (messageId == EndId) ended.TrySetResult(success);
            else if (messageId == FrameId)
            {
                if (!success) { frameId.TrySetResult(null); return; }
                using var json = ParseBounded(result);
                var id = json.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
                frameId.TrySetResult(id);
            }
        }
        catch (Exception) { frameId.TrySetResult(null); }
    }
    public void OnDevToolsEvent(IBrowser browser, string method, Stream parameters)
    {
        try
        {
            if (method == "Tracing.tracingComplete")
            {
                using var json = ParseBounded(parameters);
                lock (sync) dataLoss |= !json.RootElement.TryGetProperty("dataLossOccurred", out var loss) ||
                    loss.ValueKind != JsonValueKind.False;
                completed.TrySetResult(true);
            }
            else if (method == "Tracing.dataCollected")
            {
                using var json = ParseBounded(parameters);
                foreach (var item in json.RootElement.GetProperty("value").EnumerateArray())
                {
                    if (!item.TryGetProperty("name", out var name) || name.GetString() != "TracingStartedInBrowser" ||
                        !item.TryGetProperty("pid", out var origin) || origin.GetInt32() != Environment.ProcessId ||
                        !item.TryGetProperty("args", out var args) || !args.TryGetProperty("data", out var data) ||
                        !data.TryGetProperty("frames", out var array)) continue;
                    lock (sync) foreach (var frame in array.EnumerateArray())
                    {
                        if (frames.Count >= 32) { dataLoss = true; break; }
                        if (frame.TryGetProperty("frame", out var id) && frame.TryGetProperty("processId", out var pid) &&
                            id.GetString() is string value && pid.TryGetInt32(out int processId)) frames.Add((value, processId));
                    }
                }
            }
        }
        catch (Exception) { lock (sync) dataLoss = true; }
    }
    public void OnDevToolsAgentAttached(IBrowser browser) { }
    public void OnDevToolsAgentDetached(IBrowser browser) { }
    public void Dispose() { }
}
