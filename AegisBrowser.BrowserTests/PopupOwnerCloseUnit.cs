using System.Reflection;
using AegisBrowser;
using CefSharp;

namespace AegisBrowser.BrowserTests;

// DispatchProxy fakes exercise the real SafePopups callback methods without CEF.
public class SyntheticPopupProxy : DispatchProxy
{
    public bool Popup { get; set; }
    public int Id { get; set; }
    public object? Host { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_IsPopup" => Popup,
        "get_Identifier" => Id,
        "get_IsDisposed" => false,
        "GetHost" => Host,
        _ => throw new NotSupportedException("Unexpected synthetic browser call: " + method?.Name)
    };
}

public class SyntheticPopupHostProxy : DispatchProxy
{
    public int Requests { get; private set; }
    public bool Fail { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name == "CloseBrowser")
        {
            Requests++;
            if (Fail) throw new InvalidOperationException("synthetic popup close failure");
            return null;
        }
        throw new NotSupportedException("Unexpected synthetic host call: " + method?.Name);
    }
}

internal static partial class Program
{
    private static int PopupOwnerCloseUnit()
    {
        try
        {
            var scheduled = new Queue<Action>();
            var notifications = 0;
            var starts = 0;
            var handler = new SafePopups(_ => { }, () => notifications++, scheduled.Enqueue, () => starts++);
            var owner = DispatchProxy.Create<IBrowser, SyntheticPopupProxy>();
            ((SyntheticPopupProxy)(object)owner).Popup = false;
            var popup = DispatchProxy.Create<IBrowser, SyntheticPopupProxy>();
            var host = DispatchProxy.Create<IBrowserHost, SyntheticPopupHostProxy>();
            ((SyntheticPopupProxy)(object)popup).Popup = true;
            ((SyntheticPopupProxy)(object)popup).Id = 123;
            ((SyntheticPopupProxy)(object)popup).Host = host;
            var failed = DispatchProxy.Create<IBrowser, SyntheticPopupProxy>();
            var failedHost = DispatchProxy.Create<IBrowserHost, SyntheticPopupHostProxy>();
            ((SyntheticPopupProxy)(object)failed).Popup = true;
            ((SyntheticPopupProxy)(object)failed).Id = 124;
            ((SyntheticPopupProxy)(object)failed).Host = failedHost;
            ((SyntheticPopupHostProxy)(object)failedHost).Fail = true;
            var created = typeof(SafePopups).GetMethod("OnAfterCreated", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var beforeClose = typeof(SafePopups).GetMethod("OnBeforeClose", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var close = typeof(SafePopups).GetMethod("DoClose", BindingFlags.Instance | BindingFlags.NonPublic)!;
            created.Invoke(handler, new object?[] { null, popup });
            created.Invoke(handler, new object?[] { null, failed });
            Check(handler.PopupCount == 2, "created popups are tracked");
            Check((bool)close.Invoke(handler, new object?[] { null, owner })!, "spontaneous owner close retains host");
            Check(handler.OwnerCloseStarted.IsCompletedSuccessfully && starts == 1, "native owner close start is observed separately");
            Check(!handler.OwnerCloseReady.IsCompleted && !handler.OwnerClosed.IsCompleted, "owner readiness waits for tracked popup");
            var beforePopup = typeof(SafePopups).GetMethod("OnBeforePopup", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var popupArgs = new object?[] { null, owner, null, "about:blank", "", default(WindowOpenDisposition), true, null, null, null, false, null };
            Check((bool)beforePopup.Invoke(handler, popupArgs)!, "spontaneous close rejects later popup approval");
            handler.ClosePopups();
            Check(scheduled.Count == 2, "repeated close schedules popup closure only once");
            scheduled.Dequeue()();
            Check(((SyntheticPopupHostProxy)(object)host).Requests == 1, "spontaneous owner close requests popup close");
            Check(((SyntheticPopupHostProxy)(object)failedHost).Requests == 1 && handler.PopupCount == 2, "failed popup request stays tracked and does not prevent other requests");
            var late = DispatchProxy.Create<IBrowser, SyntheticPopupProxy>();
            var lateHost = DispatchProxy.Create<IBrowserHost, SyntheticPopupHostProxy>();
            ((SyntheticPopupProxy)(object)late).Popup = true;
            ((SyntheticPopupProxy)(object)late).Id = 125;
            ((SyntheticPopupProxy)(object)late).Host = lateHost;
            created.Invoke(handler, new object?[] { null, late });
            Check(handler.PopupCount == 3 && ((SyntheticPopupHostProxy)(object)lateHost).Requests == 1, "popup created during shutdown is tracked and asked to close");
            scheduled.Dequeue()();
            Check(!handler.OwnerCloseReady.IsCompleted, "close request is not a popup completion");
            beforeClose.Invoke(handler, new object?[] { null, popup });
            Check(handler.PopupCount == 2 && !handler.OwnerCloseReady.IsCompleted, "failed and late popups remain tracked after other popup closes");
            scheduled.Dequeue()();
            Check(!handler.OwnerCloseReady.IsCompleted && notifications == 0, "failed request never implies popup closure");
            beforeClose.Invoke(handler, new object?[] { null, late });
            scheduled.Dequeue()();
            Check(!handler.OwnerCloseReady.IsCompleted, "late popup close does not hide another tracked popup");
            beforeClose.Invoke(handler, new object?[] { null, failed });
            scheduled.Dequeue()();
            Check(handler.OwnerCloseReady.IsCompletedSuccessfully && notifications == 1, "observed popup completion permits owner readiness notification");
            close.Invoke(handler, new object?[] { null, owner });
            scheduled.Dequeue()();
            Check(starts == 1 && notifications == 1 && ((SyntheticPopupHostProxy)(object)host).Requests == 1, "duplicate native callback does not repeat popup close or readiness notification");
            var closedOnlyQueue = new Queue<Action>();
            var closedOnlyStarts = 0;
            var closedOnly = new SafePopups(_ => { }, null, closedOnlyQueue.Enqueue, () => closedOnlyStarts++);
            beforeClose.Invoke(closedOnly, new object?[] { null, owner });
            Check(closedOnlyStarts == 1 && closedOnly.OwnerCloseStarted.IsCompletedSuccessfully && !closedOnly.OwnerCloseReady.IsCompleted && !closedOnly.OwnerClosed.IsCompleted, "owner close without DoClose does not imply host readiness or immediate completion");
            beforeClose.Invoke(closedOnly, new object?[] { null, owner });
            Check(closedOnlyStarts == 1, "duplicate owner callbacks notify close start only once");
            closedOnlyQueue.Dequeue()();
            Check(closedOnly.OwnerClosed.IsCompletedSuccessfully && !closedOnly.OwnerCloseReady.IsCompleted, "final close without readiness remains unresolved");
            closedOnlyQueue.Dequeue()();
            // Characterize the unresolved approval-before-creation interval.
            // No terminal abort callback is exposed by the installed binding.
            var inFlightQueue = new Queue<Action>();
            var inFlight = new SafePopups(_ => { }, null, inFlightQueue.Enqueue);
            var callbackEvents = new List<PopupLifecycleEvent>();
            inFlight.SetLifecycleObserverForTest(callbackEvents.Add);
            var approvedArgs = new object?[] { null, owner, null, "about:blank", "", default(WindowOpenDisposition), true, null, null, null, false, null };
            Check(!(bool)beforePopup.Invoke(inFlight, approvedArgs)!, "user-gesture popup can be approved before owner close");
            close.Invoke(inFlight, new object?[] { null, owner });
            while (inFlightQueue.Count > 0) inFlightQueue.Dequeue()();
            var readyBeforeCreation = inFlight.OwnerCloseReady.IsCompleted;
            var delayed = DispatchProxy.Create<IBrowser, SyntheticPopupProxy>();
            var delayedHost = DispatchProxy.Create<IBrowserHost, SyntheticPopupHostProxy>();
            ((SyntheticPopupProxy)(object)delayed).Popup = true;
            ((SyntheticPopupProxy)(object)delayed).Id = 126;
            ((SyntheticPopupProxy)(object)delayed).Host = delayedHost;
            created.Invoke(inFlight, new object?[] { null, delayed });
            Check(inFlight.PopupCount == 1 && ((SyntheticPopupHostProxy)(object)delayedHost).Requests == 1, "previously approved popup created after close is tracked and asked to close");
            Check(callbackEvents.IndexOf(PopupLifecycleEvent.PopupApproved) >= 0 && callbackEvents.IndexOf(PopupLifecycleEvent.PopupCreated) > callbackEvents.IndexOf(PopupLifecycleEvent.PopupApproved), "test telemetry records approval before late creation");
            // This is a contract probe, not a safety pass: it does not decide
            // whether native CEF can realize the synthetic ordering.
            Console.WriteLine(readyBeforeCreation
                ? "OWNER_RELEASE_CONTRACT: UNSATISFIED for synthetic approved-before-ready-before-created ordering; native behavior remains unverified."
                : "OWNER_RELEASE_CONTRACT: this synthetic ordering did not release before creation; native behavior remains unverified.");
            var observerFaultQueue = new Queue<Action>();
            var observerFault = new SafePopups(_ => { }, null, observerFaultQueue.Enqueue);
            observerFault.SetLifecycleObserverForTest(_ => throw new InvalidOperationException("synthetic observer failure"));
            var observerArgs = new object?[] { null, owner, null, "about:blank", "", default(WindowOpenDisposition), true, null, null, null, false, null };
            Check(!(bool)beforePopup.Invoke(observerFault, observerArgs)!, "failed telemetry cannot reject an allowed popup");
            Check((bool)close.Invoke(observerFault, new object?[] { null, owner })!, "failed telemetry cannot change owner close handling");
            while (observerFaultQueue.Count > 0) observerFaultQueue.Dequeue()();
            var failedDispatch = new SafePopups(_ => { }, null, _ => throw new InvalidOperationException("synthetic CEF dispatch failure"));
            Check((bool)close.Invoke(failedDispatch, new object?[] { null, owner })! && failedDispatch.OwnerCloseStarted.IsCompletedSuccessfully && !failedDispatch.OwnerCloseReady.IsCompleted, "CEF scheduling failure keeps owner readiness incomplete");
            beforeClose.Invoke(failedDispatch, new object?[] { null, owner });
            Check(!failedDispatch.OwnerClosed.IsCompleted && !failedDispatch.OwnerCloseReady.IsCompleted, "failed final notification dispatch never implies native closure");
            Console.WriteLine("PASS: 25 popup owner close checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
