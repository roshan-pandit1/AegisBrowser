namespace P0ManagedHost;

// A pure, test-only consistency check. Its input flags and sequence numbers
// must be supplied by an independently validated producer; this class cannot
// authenticate a browser, frame, process handle, token or Chromium event.
internal static class FrameProcessStability
{
    internal sealed record Document(int BrowserId, string CefFrameId, string DevToolsFrameId,
        string LoaderId, string Url);

    internal sealed record TraceEpoch(Document? Before, Document? After,
        int BrowserPid, int TraceOriginPid, int TracePid, int MatchingRecords,
        bool TraceLoss, bool AgentAttached,
        long PageBeforeOrder, long TraceStartOrder, long TraceEndOrder, long PageAfterOrder);

    internal sealed record ProcessSample(int Pid, long CreationFileTimeUtc,
        int RevalidatedPid, long RevalidatedCreationFileTimeUtc,
        bool RetainedBeforeTracing, bool LiveBeforeToken, bool LiveAfterToken,
        long TokenBeforeOrder, long TokenAfterOrder);

    internal sealed record EventCoverage(bool Complete, bool NavigationOrFrameInvalidated,
        bool AgentDetached, bool BufferOverflow);

    internal sealed record Result(string Status, string Reason,
        string RendererIdentity = "INCONCLUSIVE", string SandboxEnforcement = "INCONCLUSIVE");

    private static Result Reject(string reason) => new("INCONCLUSIVE", reason);
    private static bool Usable(Document? d) => d != null && d.BrowserId > 0 &&
        !string.IsNullOrWhiteSpace(d.CefFrameId) && d.CefFrameId.Length <= 128 &&
        !string.IsNullOrWhiteSpace(d.DevToolsFrameId) && d.DevToolsFrameId.Length <= 128 &&
        !string.IsNullOrWhiteSpace(d.LoaderId) && d.LoaderId.Length <= 128 &&
        !string.IsNullOrWhiteSpace(d.Url) && d.Url.Length <= 2048;

    // Sequence values represent producer-observed ordering, not a claim that
    // Chromium emitted its trace event at a particular Windows clock time.
    internal static Result EvaluateManaged(TraceEpoch? first, TraceEpoch? second, EventCoverage? coverage)
    {
        if (first == null || second == null || coverage == null)
            return Reject("INPUT_MISSING");
        if (!coverage.Complete || coverage.BufferOverflow)
            return Reject("EVENT_COVERAGE_INCOMPLETE");
        if (coverage.NavigationOrFrameInvalidated || coverage.AgentDetached ||
            !first.AgentAttached || !second.AgentAttached)
            return Reject("FRAME_OR_AGENT_INVALIDATED");
        if (!Usable(first.Before) || !Usable(first.After) || !Usable(second.Before) || !Usable(second.After))
            return Reject("DOCUMENT_IDENTITY_INCOMPLETE");
        if (first.Before != first.After || first.Before != second.Before || first.Before != second.After)
            return Reject("DOCUMENT_IDENTITY_CHANGED");
        if (first.TraceLoss || second.TraceLoss || first.MatchingRecords != 1 || second.MatchingRecords != 1)
            return Reject("TRACE_AMBIGUOUS_OR_LOST");
        if (first.BrowserPid <= 0 || first.BrowserPid != second.BrowserPid ||
            first.TraceOriginPid != first.BrowserPid || second.TraceOriginPid != second.BrowserPid)
            return Reject("TRACE_ORIGIN_MISMATCH");
        if (first.TracePid <= 0 || second.TracePid != first.TracePid)
            return Reject("PROCESS_IDENTITY_CHANGED");
        if (!(first.PageBeforeOrder > 0 && first.PageBeforeOrder < first.TraceStartOrder &&
              first.TraceStartOrder < first.TraceEndOrder && first.TraceEndOrder < first.PageAfterOrder &&
              first.PageAfterOrder < second.PageBeforeOrder && second.PageBeforeOrder < second.TraceStartOrder &&
              second.TraceStartOrder < second.TraceEndOrder && second.TraceEndOrder < second.PageAfterOrder))
            return Reject("OBSERVATION_ORDER_INVALID");

        return new Result("CONSISTENT_UNVERIFIED", "TWO_EPOCHS_MATCH");
    }

    // The caller captures finalDocument after the supervisor acknowledges its
    // second retained-handle observation. This still cannot prove that events
    // were not dropped by the underlying browser or IPC infrastructure.
    internal static Result EvaluateFinal(TraceEpoch? first, TraceEpoch? second,
        EventCoverage? coverage, Document? finalDocument)
    {
        var result = EvaluateManaged(first, second, coverage);
        if (result.Status != "CONSISTENT_UNVERIFIED") return result;
        return !Usable(finalDocument) || finalDocument != second!.After
            ? Reject("FINAL_DOCUMENT_CHANGED") : result;
    }

    internal static Result Evaluate(TraceEpoch? first, ProcessSample? process,
        TraceEpoch? second, EventCoverage? coverage)
    {
        var managed = EvaluateManaged(first, second, coverage);
        if (managed.Status != "CONSISTENT_UNVERIFIED") return managed;
        if (process == null) return Reject("INPUT_MISSING");
        if (process.Pid <= 0 || process.CreationFileTimeUtc <= 0 ||
            process.RevalidatedPid != process.Pid ||
            process.RevalidatedCreationFileTimeUtc != process.CreationFileTimeUtc ||
            first!.TracePid != process.Pid || second!.TracePid != process.Pid)
            return Reject("PROCESS_IDENTITY_CHANGED");
        if (!process.RetainedBeforeTracing || !process.LiveBeforeToken || !process.LiveAfterToken)
            return Reject("RETAINED_PROCESS_NOT_LIVE");
        if (!(first.PageAfterOrder < process.TokenBeforeOrder && process.TokenBeforeOrder < second.PageBeforeOrder &&
              second.PageAfterOrder < process.TokenAfterOrder))
            return Reject("OBSERVATION_ORDER_INVALID");

        // Consistency is not independent authentication, proof of continuous
        // association, sandbox lockdown, or native enforcement.
        return managed;
    }
}
