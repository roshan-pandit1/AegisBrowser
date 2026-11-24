using AegisBrowser.Core;

internal static class DownloadCloseBarrierTests
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); count++; }
        var order = new List<string>();
        var barrier = new DownloadCloseBarrier();
        Check(barrier.TryRegister(10, 1, () => order.Add("cancel one")), "register first download");
        Check(barrier.TryRegister(11, 1, () => order.Add("cancel popup")), "same download id in another browser");
        Check(!barrier.TryRegister(10, 1, () => { }), "reject duplicate identity");
        Check(!barrier.TryRegister(0, 2, () => { }), "reject invalid browser");
        var close = barrier.BeginClose();
        Check(order.SequenceEqual(["cancel one", "cancel popup"]), "request each cancellation once");
        Check(!close.IsCompleted, "cancellation request alone cannot release browser");
        Check(ReferenceEquals(close, barrier.BeginClose()), "repeated close shares completion");
        Check(!barrier.TryRegister(10, 3, () => { }), "reject downloads initiated during close");
        Check(!barrier.ObserveTerminal(99, 1), "reject wrong browser identity");
        Check(barrier.ObserveTerminal(10, 1) && !close.IsCompleted, "wait for every terminal result");
        Check(!barrier.ObserveTerminal(10, 1), "reject duplicate terminal observation");
        Check(barrier.ObserveTerminal(11, 1) && close.IsCompletedSuccessfully, "release only after every terminal observation");

        var reentrant = new DownloadCloseBarrier();
        var secondCancelled = false;
        Check(reentrant.TryRegister(1, 1, () => { reentrant.ObserveTerminal(1, 1); reentrant.ObserveTerminal(1, 2); }), "register reentrant first");
        Check(reentrant.TryRegister(1, 2, () => secondCancelled = true), "register reentrant second");
        Check(reentrant.BeginClose().IsCompletedSuccessfully && !secondCancelled, "do not cancel an already terminal item");

        var failure = new DownloadCloseBarrier();
        var otherRequested = false;
        failure.TryRegister(2, 1, () => { failure.ObserveTerminal(2, 1); throw new IOException("fixture failure"); });
        failure.TryRegister(2, 2, () => otherRequested = true);
        var failedClose = failure.BeginClose();
        Check(failedClose.IsFaulted && otherRequested, "failed cancellation remains failed while other items are requested");
        failure.ObserveTerminal(2, 2);
        Check(failedClose.IsFaulted, "later terminal results do not erase failure");

        var wrongThreadRejected = Task.Run(() =>
        {
            try { _ = barrier.BeginClose(); return false; }
            catch (InvalidOperationException) { return true; }
        }).GetAwaiter().GetResult();
        Check(wrongThreadRejected, "reject calls from another thread");
        Console.WriteLine($"PASS: {count} download close coordination checks");
    }
}
