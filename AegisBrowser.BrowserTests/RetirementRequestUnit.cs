using AegisBrowser;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Synchronous and deterministic; creates no CEF browser, profile or window.
    private static int RetirementRequestUnit()
    {
        try
        {
            var initializing = new RetirementRequest();
            Check(initializing.TryBeginObservation(), "first tab close starts one observer");
            Check(!initializing.TryBeginObservation(), "repeated tab and form close reuse observer");
            Check(initializing.TryClosePopups(), "popup closure requested once");
            Check(!initializing.TryClosePopups(), "repeated close does not repeat popup request");
            Check(!initializing.TryRequestClose(false), "native request waits for initialization");
            Check(!initializing.TryRequestClose(false), "repeated uninitialized event stays deferred");
            Check(initializing.TryRequestClose(true), "late initialization issues one native request");
            Check(!initializing.TryRequestClose(true), "queued initialization cannot duplicate request");
            initializing.MarkNativeCloseObserved();
            Check(!initializing.TryBeginObservation(), "native ready after request shares observer");
            Check(!initializing.TryRequestClose(true), "duplicate callback cannot reissue native request");

            var spontaneous = new RetirementRequest();
            spontaneous.MarkNativeCloseObserved();
            Check(spontaneous.TryBeginObservation(), "spontaneous native close starts observer");
            spontaneous.MarkNativeCloseObserved();
            Check(!spontaneous.TryBeginObservation(), "duplicate spontaneous close stays idempotent");
            Check(!spontaneous.TryRequestClose(true), "spontaneous close suppresses queued native request");
            Check(!spontaneous.TryBeginObservation(), "later form close reuses spontaneous observer");
            Check(spontaneous.TryClosePopups(), "spontaneous close can request popup closure");

            var wrongThread = Task.Run(() =>
            {
                try { spontaneous.TryRequestClose(true); return false; }
                catch (InvalidOperationException) { return true; }
            }).GetAwaiter().GetResult();
            Check(wrongThread, "cross-thread retirement request is rejected");
            Console.WriteLine("PASS: 16 retirement request checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
