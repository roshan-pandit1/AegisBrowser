using AegisBrowser;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Fake queue models BeginInvoke delivery and handle generations; no WinForms
    // handle, CEF, network, or synthetic native-closure claim is involved.
    private static int LifetimeDispatcherUnit()
    {
        try
        {
            var posted = new Queue<Action>();
            var failures = 0;
            var dispatcher = new LifetimeDispatcher(a => posted.Enqueue(a), _ => failures++);
            var key = new object();
            var calls = 0;
            Check(dispatcher.Post(key, () => calls++), "notification accepted before handle creation");
            Check(posted.Count == 0 && calls == 0, "missing handle retains notification");
            dispatcher.HandleCreated();
            Check(posted.Count == 1, "handle creation schedules retained notification");
            posted.Dequeue()();
            Check(calls == 1, "normal delivery executes exactly once");

            dispatcher.Post(key, () => calls += 10);
            dispatcher.Post(key, () => calls += 100);
            Check(posted.Count == 1, "duplicate state notifications coalesce");
            posted.Dequeue()();
            Check(calls == 101, "coalesced notification observes latest state");

            dispatcher.Post(key, () => calls++);
            var stale = posted.Dequeue();
            dispatcher.HandleDestroyed();
            Check(calls == 101, "handle destruction does not execute pending work");
            dispatcher.HandleCreated();
            Check(posted.Count == 1, "recreated handle reschedules pending work");
            stale();
            Check(calls == 101, "old handle callback cannot consume pending work");
            posted.Dequeue()();
            Check(calls == 102, "recreated handle delivers notification once");

            dispatcher.HandleDestroyed();
            dispatcher.Post(key, () => calls++);
            dispatcher.HandleCreated();
            posted.Dequeue()();
            Check(calls == 103, "notification while unavailable survives recreation");

            var attempts = 0;
            var retryQueue = new Queue<Action>();
            var retry = new LifetimeDispatcher(a => { if (attempts++ == 0) throw new InvalidOperationException("synthetic post failure"); retryQueue.Enqueue(a); }, _ => failures++);
            retry.HandleCreated();
            retry.Post(key, () => calls++);
            Check(failures == 1 && retryQueue.Count == 0, "failed UI scheduling is reported and retained");
            retry.HandleDestroyed();
            retry.HandleCreated();
            retryQueue.Dequeue()();
            Check(calls == 104, "scheduling failure recovers on handle recreation");

            var second = new object();
            dispatcher.Post(key, () => throw new InvalidOperationException("synthetic delivery failure"));
            dispatcher.Post(second, () => calls++);
            posted.Dequeue()();
            Check(failures == 2 && calls == 105, "failed action is reported and later notification still runs");
            Check(posted.Count == 0, "failed action is not retried as an uncertain operation");

            dispatcher.Post(key, () =>
            {
                calls++;
                dispatcher.Post(second, () => calls++);
                dispatcher.HandleDestroyed();
                dispatcher.HandleCreated();
            });
            posted.Dequeue()();
            Check(calls == 106 && posted.Count == 1, "reentrant recreation preserves remaining work");
            posted.Dequeue()();
            Check(calls == 107, "remaining work runs on replacement handle");

            var wrongThread = Task.Run(() =>
            {
                try { dispatcher.HandleDestroyed(); return false; }
                catch (InvalidOperationException) { return true; }
            }).GetAwaiter().GetResult();
            Check(wrongThread, "handle lifecycle requires the owning thread");
            var accepted = Task.Run(() => dispatcher.Post(key, () => calls++)).GetAwaiter().GetResult();
            Check(accepted && calls == 107, "CEF-thread post does not run UI action inline");
            posted.Dequeue()();
            Check(calls == 108, "cross-thread notification runs on owning thread");

            dispatcher.Post(key, () => calls++);
            var afterStop = posted.Dequeue();
            Check(dispatcher.Stop() == 1, "final disposal reports pending notification");
            afterStop();
            Check(calls == 108 && !dispatcher.Post(key, () => calls++), "final disposal rejects and cancels delivery");
            Check(dispatcher.Stop() == 0, "final disposal is idempotent");
            Console.WriteLine("PASS: 23 lifetime dispatcher checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
