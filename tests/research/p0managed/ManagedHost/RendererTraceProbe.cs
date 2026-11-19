using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CefSharp;
using CefSharp.Callback;

namespace P0ManagedHost;

// Browser-side research diagnostics only. Matching trace epochs are not proof
// of renderer identity, complete lockdown, or native access enforcement.
internal sealed class RendererTraceProbe : IDevToolsMessageObserver
{
    private sealed record Reply(bool Success, string? Frame = null, string? Loader = null, string? Url = null);
    private readonly object sync = new();
    private readonly Dictionary<int, TaskCompletionSource<Reply>> pending = new();
    private readonly Dictionary<int, string> pendingMethods = new();
    // These names are also used by explicitly synthetic parser tests.
    private readonly List<(string Frame, int Pid)> frames = new();
    private readonly List<(string Stage, long Timestamp)> timeline = new();
    private bool dataLoss, recordingEvents, navigationInvalidated, detached, eventsLost;
    private int lifecycleEvents, nextId = 81000;
    private long ordering;
    private TaskCompletionSource<bool>? traceComplete;

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

    private long NextOrder() => Interlocked.Increment(ref ordering);

    private void Mark(string stage)
    {
        lock (sync)
        {
            if (timeline.Count >= 128) { eventsLost = true; return; }
            timeline.Add((stage, Stopwatch.GetTimestamp()));
        }
    }

    private async Task<Reply> SendAsync(IBrowser browser, string method, Dictionary<string, object>? parameters = null)
    {
        int id = Interlocked.Increment(ref nextId);
        var reply = new TaskCompletionSource<Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync) { pending.Add(id, reply); pendingMethods.Add(id, method); }
        try
        {
            int sent = await browser.ExecuteDevToolsMethodAsync(id, method, parameters ?? new()).WaitAsync(TimeSpan.FromSeconds(5));
            if (sent != id) throw new InvalidOperationException("DEVTOOLS_COMMAND_NOT_SUBMITTED");
            return await reply.Task.WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally { lock (sync) { pending.Remove(id); pendingMethods.Remove(id); } }
    }

    private static async Task<(int BrowserId, string FrameId)?> CefMainFrameAsync(IBrowser browser)
    {
        return await Cef.UIThreadTaskFactory.StartNew(() => {
            if (browser.IsDisposed || !browser.HasDocument) return ((int, string)?)null;
            using var frame = browser.MainFrame;
            if (frame == null || !frame.IsValid || !frame.IsMain || string.IsNullOrWhiteSpace(frame.Identifier))
                return ((int, string)?)null;
            return (browser.Identifier, frame.Identifier);
        }).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task<FrameProcessStability.Document?> DocumentAsync(IBrowser browser)
    {
        var before = await CefMainFrameAsync(browser);
        var page = await SendAsync(browser, "Page.getFrameTree");
        var after = await CefMainFrameAsync(browser);
        Uri? expected = ManagedEntry.ControlledHttpsUri();
        if (before == null || after == null || before != after || !page.Success || expected == null ||
            !string.Equals(page.Url, expected.AbsoluteUri, StringComparison.Ordinal)) return null;
        return new FrameProcessStability.Document(before.Value.BrowserId, before.Value.FrameId,
            page.Frame ?? "", page.Loader ?? "", page.Url ?? "");
    }

    private async Task<FrameProcessStability.TraceEpoch> EpochAsync(IBrowser browser)
    {
        var before = await DocumentAsync(browser);
        Mark("PAGE_BEFORE");
        long beforeOrder = NextOrder();
        var complete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync) { frames.Clear(); traceComplete = complete; }
        long startOrder = NextOrder();
        Mark("TRACE_START");
        bool startOutstanding = true;
        try
        {
            var start = await SendAsync(browser, "Tracing.start", new()
            {
                ["categories"] = "disabled-by-default-devtools.timeline", ["transferMode"] = "ReportEvents"
            });
            if (!start.Success) { startOutstanding = false; throw new InvalidOperationException("TRACE_START_REJECTED"); }
            var end = await SendAsync(browser, "Tracing.end");
            if (!end.Success) throw new InvalidOperationException("TRACE_END_REJECTED");
            await complete.Task.WaitAsync(TimeSpan.FromSeconds(8));
            startOutstanding = false;
        }
        finally
        {
            // Tracing.start/end has no managed cancellation. Never continue
            // the fixture as if tracing shutdown were confirmed on timeout.
            if (startOutstanding)
            {
                try
                {
                    var stop = await SendAsync(browser, "Tracing.end");
                    if (stop.Success) await complete.Task.WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (Exception) { /* The fixture fails below. */ }
                throw new InvalidOperationException("TRACE_LIFECYCLE_UNCONFIRMED");
            }
        }
        long endOrder = NextOrder();
        Mark("TRACE_END");
        (string Frame, int Pid)[] matched;
        bool loss;
        lock (sync)
        {
            matched = frames.Where(x => before != null && x.Frame == before.DevToolsFrameId && x.Pid > 0).ToArray();
            loss = dataLoss;
        }
        var after = await DocumentAsync(browser);
        Mark("PAGE_AFTER");
        long afterOrder = NextOrder();
        return new FrameProcessStability.TraceEpoch(before, after, Environment.ProcessId, Environment.ProcessId,
            matched.Length == 1 ? matched[0].Pid : 0, matched.Length, loss, !detached,
            beforeOrder, startOrder, endOrder, afterOrder);
    }

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static async Task ObserveAsync(IBrowser browser)
    {
        var observer = new RendererTraceProbe();
        IDisposable? registration = null;
        TraceCorrelationClient? correlation = null;
        bool lifecycleUnconfirmed = false;
        bool emitted = false;
        FrameProcessStability.Document? evidenceDocument = null;
        int? evidencePid = null;
        FrameProcessStability.Result finalResult = new("INCONCLUSIVE", "OBSERVATION_NOT_COMPLETED");
        void Emit(FrameProcessStability.Result result)
        {
            if (emitted) return;
            emitted = true;
            int eventCount;
            lock (observer.sync) eventCount = observer.lifecycleEvents;
            Console.WriteLine("MANAGED_STABILITY_JSON: " + JsonSerializer.Serialize(new {
                status = result.Status, reason = result.Reason,
                browser_id = evidenceDocument?.BrowserId, pid = evidencePid,
                cef_frame_sha256 = evidenceDocument == null ? null : Fingerprint(evidenceDocument.CefFrameId),
                frame_sha256 = evidenceDocument == null ? null : Fingerprint(evidenceDocument.DevToolsFrameId),
                loader_sha256 = evidenceDocument == null ? null : Fingerprint(evidenceDocument.LoaderId),
                event_count = eventCount, renderer_identity = "INCONCLUSIVE", sandbox_enforcement = "INCONCLUSIVE"
            }));
        }
        try
        {
            var registrationTask = Cef.UIThreadTaskFactory.StartNew(() => browser.GetHost().AddDevToolsMessageObserver(observer));
            try { registration = await registrationTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                lifecycleUnconfirmed = true;
                _ = registrationTask.ContinueWith(t => {
                    if (t.Status == TaskStatus.RanToCompletion && t.Result is not null)
                        _ = Cef.UIThreadTaskFactory.StartNew(t.Result.Dispose);
                }, TaskScheduler.Default);
                throw;
            }
            if (registration is null) throw new InvalidOperationException("OBSERVER_UNAVAILABLE");
            var enable = await observer.SendAsync(browser, "Page.enable");
            if (!enable.Success) throw new InvalidOperationException("PAGE_DOMAIN_UNAVAILABLE");
            lock (observer.sync) observer.recordingEvents = true;
            correlation = await TraceCorrelationClient.ConnectAsync();
            observer.Mark("HANDLE_BARRIER");
            var first = await observer.EpochAsync(browser);
            if (first.Before == null || first.MatchingRecords != 1 || first.TraceLoss || first.TracePid <= 0)
                throw new InvalidOperationException("FIRST_TRACE_INCONCLUSIVE");
            evidenceDocument = first.Before;
            evidencePid = first.TracePid;
            // The supervisor records the retained handle and token before it
            // acknowledges this first browser-authenticated report.
            observer.Mark("TOKEN_REQUEST");
            await correlation.SendAsync(first.TracePid, first.Before.DevToolsFrameId);
            observer.Mark("TOKEN_RECEIPT");
            var second = await observer.EpochAsync(browser);
            FrameProcessStability.EventCoverage coverage;
            lock (observer.sync)
                coverage = new(!observer.eventsLost, observer.navigationInvalidated, observer.detached, observer.eventsLost);
            var result = FrameProcessStability.EvaluateManaged(first, second, coverage);
            if (result.Status == "CONSISTENT_UNVERIFIED")
            {
                observer.Mark("REVALIDATION_REQUEST");
                await correlation.SendRevalidationAsync(second.TracePid, second.Before!.DevToolsFrameId);
                observer.Mark("REVALIDATION_RECEIPT");
                // The ACK denotes receipt, not verified identity. Sample the
                // active document again after the second supervisor token read.
                var finalDocument = await observer.DocumentAsync(browser);
                observer.Mark("FINAL_DOCUMENT");
                lock (observer.sync)
                {
                    coverage = new(!observer.eventsLost, observer.navigationInvalidated, observer.detached, observer.eventsLost);
                    result = FrameProcessStability.EvaluateFinal(first, second, coverage, finalDocument);
                }
                if (result.Status == "CONSISTENT_UNVERIFIED")
                {
                    observer.Mark("FINALIZATION_REQUEST");
                    await correlation.SendFinalizationAsync();
                    observer.Mark("FINALIZATION_RECEIPT");
                    // Late events cannot promote earlier observations. A
                    // receipt still does not establish renderer identity.
                    lock (observer.sync)
                    {
                        coverage = new(!observer.eventsLost, observer.navigationInvalidated, observer.detached, observer.eventsLost);
                        result = FrameProcessStability.EvaluateFinal(first, second, coverage, finalDocument);
                    }
                }
            }
            Console.WriteLine("MANAGED_STABILITY: " + result.Status + " REASON=" + result.Reason +
                " BROWSER=" + first.Before.BrowserId + " PID=" + first.TracePid +
                " CEF_FRAME_SHA256=" + Fingerprint(first.Before.CefFrameId) +
                " FRAME_SHA256=" + Fingerprint(first.Before.DevToolsFrameId) +
                " LOADER_SHA256=" + Fingerprint(first.Before.LoaderId) +
                " EVENTS=" + observer.lifecycleEvents + " RENDERER_IDENTITY=INCONCLUSIVE");
            finalResult = result;
        }
        catch (Exception ex)
        {
            Console.WriteLine("MANAGED_STABILITY: INCONCLUSIVE " + ex.GetType().Name + " " + ex.Message);
            finalResult = new FrameProcessStability.Result("INCONCLUSIVE", "EXCEPTION_" + ex.GetType().Name);
            if (ex is TimeoutException || ex.Message == "TRACE_LIFECYCLE_UNCONFIRMED") lifecycleUnconfirmed = true;
        }
        finally
        {
            (string Stage, long Timestamp)[] stamps;
            lock (observer.sync) stamps = observer.timeline.ToArray();
            Console.WriteLine("MANAGED_STABILITY_TIMELINE: " + JsonSerializer.Serialize(new {
                clock = "Stopwatch", frequency = Stopwatch.Frequency,
                entries = stamps.Select(s => new { stage = s.Stage, qpc = s.Timestamp })
            }));
            correlation?.Dispose();
            if (registration is not null)
            {
                try { await Cef.UIThreadTaskFactory.StartNew(registration.Dispose).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { lifecycleUnconfirmed = true; }
            }
            if (lifecycleUnconfirmed)
            {
                Emit(new FrameProcessStability.Result("INCONCLUSIVE", "TRACE_LIFECYCLE_UNCONFIRMED"));
                throw new InvalidOperationException("TRACE_LIFECYCLE_UNCONFIRMED");
            }
            Emit(finalResult);
        }
    }

    public bool OnDevToolsMessage(IBrowser browser, Stream message) => false;
    public void OnDevToolsMethodResult(IBrowser browser, int messageId, bool success, Stream result)
    {
        TaskCompletionSource<Reply>? target;
        string? method;
        lock (sync) { pending.TryGetValue(messageId, out target); pendingMethods.TryGetValue(messageId, out method); }
        if (target == null) return;
        try
        {
            if (!success) { target.TrySetResult(new Reply(false)); return; }
            if (method != "Page.getFrameTree") { target.TrySetResult(new Reply(true)); return; }
            using var json = ParseBounded(result);
            if (json.RootElement.TryGetProperty("frameTree", out var tree))
            {
                var frame = tree.GetProperty("frame");
                target.TrySetResult(new Reply(true, frame.GetProperty("id").GetString(),
                    frame.GetProperty("loaderId").GetString(), frame.GetProperty("url").GetString()));
            }
            else target.TrySetResult(new Reply(true));
        }
        catch (Exception) { target.TrySetResult(new Reply(false)); lock (sync) eventsLost = true; }
    }

    public void OnDevToolsEvent(IBrowser browser, string method, Stream parameters)
    {
        try
        {
            if (method == "Tracing.tracingComplete")
            {
                using var json = ParseBounded(parameters);
                lock (sync)
                {
                    dataLoss |= !json.RootElement.TryGetProperty("dataLossOccurred", out var loss) ||
                        loss.ValueKind != JsonValueKind.False;
                    traceComplete?.TrySetResult(true);
                }
            }
            else if (method == "Tracing.dataCollected")
            {
                using var json = ParseBounded(parameters);
                foreach (var item in json.RootElement.GetProperty("value").EnumerateArray())
                {
                    if (!item.TryGetProperty("name", out var name) ||
                        !item.TryGetProperty("pid", out var origin) || origin.GetInt32() != Environment.ProcessId)
                        continue;
                    string? eventName = name.GetString();
                    if (eventName is "FrameCommittedInBrowser" or "FrameDeletedInBrowser")
                    {
                        // Chromium emits FrameCommittedInBrowser at ready-to-
                        // commit, not after commit. Treat it as invalidation.
                        lock (sync) if (recordingEvents)
                        {
                            if (++lifecycleEvents > 128) eventsLost = true;
                            navigationInvalidated = true;
                        }
                        Mark("BROWSER_FRAME_INVALIDATION");
                        continue;
                    }
                    if (eventName != "TracingStartedInBrowser" ||
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
            else if (method is "Page.frameNavigated" or "Page.frameDetached" or "Page.frameStartedNavigating" or
                "Page.frameStartedLoading" or "Page.navigatedWithinDocument" or "Page.frameRequestedNavigation" or
                "Page.frameScheduledNavigation" or "Inspector.targetCrashed" or "Inspector.detached" or "Target.targetCrashed")
            {
                // Conservatively invalidate on any frame navigation, including
                // subframes; never infer a stable main frame after an event.
                lock (sync) if (recordingEvents)
                {
                    if (++lifecycleEvents > 128) eventsLost = true;
                    navigationInvalidated = true;
                }
                Mark("PAGE_LIFECYCLE_INVALIDATION");
            }
        }
        catch (Exception) { lock (sync) { dataLoss = true; eventsLost = true; } }
    }
    public void OnDevToolsAgentAttached(IBrowser browser) { }
    public void OnDevToolsAgentDetached(IBrowser browser) { lock (sync) detached = true; Mark("AGENT_DETACHED"); }
    public void Dispose() { }
}
