using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AegisBrowser.WebView2Feasibility;

// In-memory sinks and controlled deadlines only; no WebView2 or VM is started.
internal static class DiagnosticJournalTests
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("Diagnostic check " + (count + 1)); count++; }
        static Task Deadline() => Task.Delay(TimeSpan.FromSeconds(5));

        var sink = new RecordingSink();
        var run = Guid.Parse("09bfcc0d-0496-47ea-a658-960d00d6a39a");
        var journal = DiagnosticJournal.CreateForTesting(run, () => sink);
        Check(journal.TryRecord(DiagnosticEvent.Startup));
        Check(journal.TryRecord(DiagnosticEvent.RuntimeSelected, DiagnosticSlot.A, active: 1, runtimeVersion: "158.0.4370.45"));
        Check(journal.TryRecord(DiagnosticEvent.NavigationStart, DiagnosticSlot.B, active: 2, navigationId: 77));
        Check(await journal.CheckpointAsync(DiagnosticEvent.DisposeEnter, DiagnosticSlot.A, Deadline(), active: 0));
        var lines = sink.Lines.ToArray();
        Check(lines.Length == 4);
        Check(lines.Select((line, i) => line.StartsWith("seq=" + (i + 1) + " ", StringComparison.Ordinal)).All(value => value));
        Check(lines.All(line => line.Contains("run=" + run.ToString("N"), StringComparison.Ordinal) &&
            line.Contains("elapsed_ticks=", StringComparison.Ordinal) && line.Contains("thread=", StringComparison.Ordinal)));
        Check(lines[1].Contains("runtime=158.0.4370.45", StringComparison.Ordinal));
        Check(lines[2].Contains("navigation_id=77", StringComparison.Ordinal));
        Check(lines.All(line => !line.Contains("secret", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains("https:", StringComparison.OrdinalIgnoreCase)));
        Check(sink.FlushCount >= 1);
        Check(await journal.CompleteAsync(Deadline()));
        Check(!journal.TryRecord(DiagnosticEvent.CloseRequested));

        // Invalid data is never written and poisons the evidence state, so a
        // later checkpoint cannot silently claim a complete diagnostic stream.
        var invalidSink = new RecordingSink();
        var invalid = DiagnosticJournal.CreateForTesting(Guid.NewGuid(), () => invalidSink);
        Check(!invalid.TryRecord(DiagnosticEvent.RuntimeSelected, DiagnosticSlot.B, runtimeVersion: "https://user:secret@example.invalid/"));
        Check(invalid.IsIncomplete);
        Check(!invalid.TryRecord((DiagnosticEvent)999));
        Check(!invalid.TryRecord(DiagnosticEvent.Startup, (DiagnosticSlot)999));
        Check(!invalid.TryRecord(DiagnosticEvent.Startup, active: -2));
        Check(!invalid.TryRecord(DiagnosticEvent.ProcessOutcome, exitCode: 256));
        Check(!await invalid.CompleteAsync(Deadline()));
        Check(invalidSink.Lines.IsEmpty);

        var concurrentSink = new RecordingSink();
        var concurrent = DiagnosticJournal.CreateForTesting(Guid.NewGuid(), () => concurrentSink);
        var producers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
                if (!concurrent.TryRecord(DiagnosticEvent.NavigationStart, DiagnosticSlot.A, navigationId: (ulong)i)) return false;
            return true;
        })).ToArray();
        Check((await Task.WhenAll(producers)).All(value => value));
        Check(await concurrent.CheckpointAsync(DiagnosticEvent.QuiescenceStart, DiagnosticSlot.None, Deadline()));
        var concurrentLines = concurrentSink.Lines.ToArray();
        Check(concurrentLines.Length == 401);
        Check(concurrentLines.Select((line, i) => line.StartsWith("seq=" + (i + 1) + " ", StringComparison.Ordinal)).All(value => value));
        Check(await concurrent.CompleteAsync(Deadline()));

        var broken = DiagnosticJournal.CreateForTesting(Guid.NewGuid(), () => new ThrowingSink());
        // The producer never performs sink I/O. A writer failure must not become
        // a successful flush acknowledgment even when it races the enqueue.
        _ = broken.TryRecord(DiagnosticEvent.Startup);
        Check(!await broken.CompleteAsync(Deadline()));
        Check(broken.IsIncomplete);

        var stalledSink = new StalledSink();
        var stalled = DiagnosticJournal.CreateForTesting(Guid.NewGuid(), () => stalledSink);
        Check(stalled.TryRecord(DiagnosticEvent.Startup));
        Check(stalledSink.Entered.Wait(TimeSpan.FromSeconds(5)));
        var elapsed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = stalled.CheckpointAsync(DiagnosticEvent.DisposeEnter, DiagnosticSlot.A, elapsed.Task);
        Check(!waiting.IsCompleted);
        elapsed.SetResult(true);
        Check(!await waiting);
        stalledSink.Release.Set();
        // This confirms writer recovery only, not the previously expired checkpoint.
        Check(await stalled.CompleteAsync(Deadline()));
        return count;
    }

    private sealed class RecordingSink : IDiagnosticSink
    {
        internal readonly ConcurrentQueue<string> Lines = new();
        private int flushCount;
        internal int FlushCount => Volatile.Read(ref flushCount);
        public void WriteLine(string line) => Lines.Enqueue(line);
        public void Flush() => Interlocked.Increment(ref flushCount);
        public void Dispose() { }
    }

    private sealed class ThrowingSink : IDiagnosticSink
    {
        public void WriteLine(string line) => throw new InvalidOperationException();
        public void Flush() => throw new InvalidOperationException();
        public void Dispose() { }
    }

    private sealed class StalledSink : IDiagnosticSink
    {
        internal readonly ManualResetEventSlim Entered = new();
        internal readonly ManualResetEventSlim Release = new();
        public void WriteLine(string line) { Entered.Set(); Release.Wait(); }
        public void Flush() { }
        public void Dispose() { }
    }
}
