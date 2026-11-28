using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AegisBrowser.WebView2Feasibility;

// Values are an allowlist: never add arbitrary page, path, argument or exception text.
internal enum DiagnosticEvent
{
    Startup, ArgumentsInvalid, ProfileRootValid, ProfileRootInvalid,
    TimeoutValid, TimeoutInvalid, InitializationStart, InitializationComplete,
    InitializationFailed, RuntimeSelected, NavigationRequest, NavigationLeaseAcquired,
    NavigationStart, NavigationCancelled, NavigationComplete, NavigationLeaseReleased,
    NavigationCallFailed, NavigationTimeout, StorageStart, StorageComplete,
    CloseRequested, CloseRepeated, QuiescenceStart, QuiescenceComplete,
    QuiescenceTimeout, DisposeEnter, DisposeReturn, DisposeFailed,
    FinalCloseRequested, FormClosed, MessageLoopReturn, ProcessOutcome,
    ResourceStart, ResourceComplete, NavigationRejected, RacePreconditionMet,
    RacePreconditionMissed, ObservationMatched, ObservationIncomplete,
    DispatchReturned, NavigationSucceeded, NavigationOperationCanceled,
    NavigationOtherFailure, SyntheticResponseServed, ResourceRejected,
    ScenarioOnePending, ScenarioBothPending, ObservedGateCheck, ObservedGateClear,
    ScenarioNormal, DisposeCheckpoint, RequestedNormal, RequestedOnePending,
    RequestedBothPending, ScenarioStartPosted, ScenarioMismatch
}

internal enum DiagnosticSlot { None, A, B }
internal enum DiagnosticFailure { None, InvalidOperation, IOException, UnauthorizedAccess, Com, Other }

internal interface IDiagnosticSink : IDisposable
{
    void WriteLine(string line);
    void Flush();
}

// Producers enqueue only bounded, sanitized records; all file I/O is on a
// background writer. False means incomplete diagnostics, never shutdown success.
internal sealed class DiagnosticJournal
{
    private const int QueueCapacity = 512;
    private const int RecordLimit = 4096;
    private readonly object gate = new();
    private readonly BlockingCollection<Entry> queue = new(QueueCapacity);
    private readonly Guid runId;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly TaskCompletionSource<bool> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long sequence;
    private bool incomplete;
    private bool completed;

    private sealed record Entry(string? Line, TaskCompletionSource<bool>? Ack, bool Stop);

    private DiagnosticJournal(Guid runId, Func<IDiagnosticSink> createSink)
    {
        this.runId = runId;
        var writer = new Thread(() => WriteLoop(createSink)) { IsBackground = true, Name = "Aegis diagnostic writer" };
        writer.Start();
    }

    // Only the background writer touches the console. An operator can redirect
    // stdout to a separately approved evidence destination; no path is created.
    internal static DiagnosticJournal CreateConsole(Guid runId) =>
        new(runId, () => new TextWriterDiagnosticSink(Console.Out));

    internal static DiagnosticJournal CreateForTesting(Guid runId, Func<IDiagnosticSink> createSink) =>
        new(runId, createSink);

    internal bool TryRecord(DiagnosticEvent kind, DiagnosticSlot slot = DiagnosticSlot.None,
        int active = -1, ulong? navigationId = null, string? runtimeVersion = null,
        DiagnosticFailure failure = DiagnosticFailure.None, int? hresult = null, int? exitCode = null) =>
        Enqueue(kind, slot, active, navigationId, runtimeVersion, failure, hresult, exitCode, null);

    // Await this BEFORE entering each synchronous dispose. On false, the caller
    // must report that the marker is not confirmed; it cannot claim the log was
    // flushed or infer a disposal outcome. Deadline expiry does not stop work.
    internal async Task<bool> CheckpointAsync(DiagnosticEvent kind, DiagnosticSlot slot, Task deadline,
        int active = -1, ulong? navigationId = null)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Enqueue(kind, slot, active, navigationId, null, DiagnosticFailure.None, null, null, ack)) return false;
        var winner = await Task.WhenAny(ack.Task, deadline).ConfigureAwait(false);
        return ReferenceEquals(winner, ack.Task) && ack.Task.IsCompletedSuccessfully && ack.Task.Result && !IsIncomplete;
    }

    // The caller awaits this without blocking the UI thread. A false result may
    // leave the background writer running; it must never be called proof of flush.
    internal async Task<bool> CompleteAsync(Task deadline)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        lock (gate)
        {
            if (!completed)
            {
                completed = true;
                if (!queue.TryAdd(new Entry(null, null, true))) incomplete = true;
                queue.CompleteAdding();
            }
        }
        var winner = await Task.WhenAny(finished.Task, deadline).ConfigureAwait(false);
        return ReferenceEquals(winner, finished.Task) && finished.Task.IsCompletedSuccessfully && finished.Task.Result && !IsIncomplete;
    }

    internal bool IsIncomplete { get { lock (gate) return incomplete; } }

    private bool Enqueue(DiagnosticEvent kind, DiagnosticSlot slot, int active, ulong? navigationId,
        string? runtimeVersion, DiagnosticFailure failure, int? hresult, int? exitCode, TaskCompletionSource<bool>? ack)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(slot) || !Enum.IsDefined(failure) ||
            active < -1 || active > 10000 || (exitCode.HasValue && (exitCode < 0 || exitCode > 255)) ||
            !IsSafeVersion(runtimeVersion))
        {
            lock (gate) incomplete = true;
            return false;
        }
        lock (gate)
        {
            if (completed || incomplete || sequence >= RecordLimit)
            {
                incomplete = true;
                return false;
            }
            var elapsed = Stopwatch.GetTimestamp() - started;
            var next = sequence + 1;
            string line = $"seq={next.ToString(CultureInfo.InvariantCulture)} elapsed_ticks={elapsed.ToString(CultureInfo.InvariantCulture)} " +
                $"frequency={Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture)} thread={Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)} " +
                $"run={runId:N} event={kind} slot={slot} active={active.ToString(CultureInfo.InvariantCulture)}";
            if (navigationId.HasValue) line += " navigation_id=" + navigationId.Value.ToString(CultureInfo.InvariantCulture);
            if (runtimeVersion is not null) line += " runtime=" + runtimeVersion;
            if (failure != DiagnosticFailure.None) line += " failure=" + failure;
            if (hresult.HasValue) line += " hresult=0x" + unchecked((uint)hresult.Value).ToString("X8", CultureInfo.InvariantCulture);
            if (exitCode.HasValue) line += " exit_code=" + exitCode.Value.ToString(CultureInfo.InvariantCulture);
            if (!queue.TryAdd(new Entry(line, ack, false)))
            {
                incomplete = true;
                return false;
            }
            sequence = next;
            return true;
        }
    }

    private static bool IsSafeVersion(string? value) => value is null ||
        (value.Length <= 64 && Regex.IsMatch(value, @"\A[0-9]{1,4}(?:\.[0-9]{1,6}){3}(?: (?:beta|dev|canary))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    private void WriteLoop(Func<IDiagnosticSink> createSink)
    {
        Entry? current = null;
        try
        {
            using var sink = createSink();
            foreach (var item in queue.GetConsumingEnumerable())
            {
                current = item;
                if (item.Stop) { sink.Flush(); break; }
                sink.WriteLine(item.Line!);
                if (item.Ack is not null) { sink.Flush(); item.Ack.TrySetResult(true); }
                current = null;
            }
            finished.TrySetResult(true);
        }
        catch
        {
            lock (gate) incomplete = true;
            current?.Ack?.TrySetResult(false);
            finished.TrySetResult(false);
        }
        finally
        {
            while (queue.TryTake(out var entry)) entry.Ack?.TrySetResult(false);
        }
    }

    private sealed class TextWriterDiagnosticSink : IDiagnosticSink
    {
        private readonly TextWriter writer;
        internal TextWriterDiagnosticSink(TextWriter writer) => this.writer = writer;
        public void WriteLine(string line) => writer.WriteLine(line);
        public void Flush() => writer.Flush();
        public void Dispose() { }
    }
}
