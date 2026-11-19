using P0ManagedHost;
using S = P0ManagedHost.FrameProcessStability;

// Synthetic input only: no Chromium, CEF, OS process or sandbox is tested.
int checks = 0;
var doc = new S.Document(7, "cef-frame", "devtools-frame", "loader", "https://127.0.0.1:4443/");
var first = new S.TraceEpoch(doc, doc, 101, 101, 202, 1, false, true, 1, 2, 3, 4);
var second = new S.TraceEpoch(doc, doc, 101, 101, 202, 1, false, true, 6, 7, 8, 9);
var process = new S.ProcessSample(202, 12345678, 202, 12345678, true, true, true, 5, 10);
var coverage = new S.EventCoverage(true, false, false, false);

void Check(bool valid, string name)
{
    if (!valid) throw new InvalidOperationException("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}
void Reject(S.TraceEpoch? a, S.ProcessSample? p, S.TraceEpoch? b, S.EventCoverage? c, string reason)
{
    var result = S.Evaluate(a, p, b, c);
    Check(result.Status == "INCONCLUSIVE" && result.Reason == reason &&
        result.RendererIdentity == "INCONCLUSIVE" && result.SandboxEnforcement == "INCONCLUSIVE", reason);
}

var valid = S.Evaluate(first, process, second, coverage);
Check(valid.Status == "CONSISTENT_UNVERIFIED" && valid.RendererIdentity == "INCONCLUSIVE" &&
      valid.SandboxEnforcement == "INCONCLUSIVE", "matching observations do not prove identity or sandbox");
Check(S.EvaluateFinal(first, second, coverage, doc).Status == "CONSISTENT_UNVERIFIED",
    "unchanged post-revalidation document remains only unverified consistency");
Check(S.EvaluateFinal(first, second, coverage, doc with { LoaderId = "new-loader" }).Reason == "FINAL_DOCUMENT_CHANGED",
    "post-revalidation document replacement is rejected");
Check(S.EvaluateFinal(first, second, coverage with { NavigationOrFrameInvalidated = true }, doc).Reason == "FRAME_OR_AGENT_INVALIDATED",
    "post-revalidation navigation invalidates earlier snapshots");
Reject(null, process, second, coverage, "INPUT_MISSING");
Reject(first, process, second, coverage with { Complete = false }, "EVENT_COVERAGE_INCOMPLETE");
Reject(first, process, second, coverage with { BufferOverflow = true }, "EVENT_COVERAGE_INCOMPLETE");
Reject(first, process, second, coverage with { NavigationOrFrameInvalidated = true }, "FRAME_OR_AGENT_INVALIDATED");
Reject(first, process, second, coverage with { AgentDetached = true }, "FRAME_OR_AGENT_INVALIDATED");
Reject(first, process, second with { AgentAttached = false }, coverage, "FRAME_OR_AGENT_INVALIDATED");
Reject(first with { Before = doc with { CefFrameId = "" } }, process, second, coverage, "DOCUMENT_IDENTITY_INCOMPLETE");
Reject(first, process, second with { After = doc with { LoaderId = "new-loader" } }, coverage, "DOCUMENT_IDENTITY_CHANGED");
Reject(first, process, second with { Before = doc with { DevToolsFrameId = "new-frame" } }, coverage, "DOCUMENT_IDENTITY_CHANGED");
Reject(first, process, second with { After = doc with { CefFrameId = "new-cef-frame" } }, coverage, "DOCUMENT_IDENTITY_CHANGED");
Reject(first, process, second with { After = doc with { BrowserId = 8 } }, coverage, "DOCUMENT_IDENTITY_CHANGED");
Reject(first, process, second with { After = doc with { Url = "https://other.invalid/" } }, coverage, "DOCUMENT_IDENTITY_CHANGED");
Reject(first with { MatchingRecords = 2 }, process, second, coverage, "TRACE_AMBIGUOUS_OR_LOST");
Reject(first, process, second with { MatchingRecords = 0 }, coverage, "TRACE_AMBIGUOUS_OR_LOST");
Reject(first, process, second with { TraceLoss = true }, coverage, "TRACE_AMBIGUOUS_OR_LOST");
Reject(first with { TraceOriginPid = 999 }, process, second, coverage, "TRACE_ORIGIN_MISMATCH");
Reject(first, process, second with { BrowserPid = 999, TraceOriginPid = 999 }, coverage, "TRACE_ORIGIN_MISMATCH");
Reject(first, process, second with { TracePid = 203 }, coverage, "PROCESS_IDENTITY_CHANGED");
Reject(first, process with { Pid = 0 }, second, coverage, "PROCESS_IDENTITY_CHANGED");
Reject(first, process with { CreationFileTimeUtc = 0 }, second, coverage, "PROCESS_IDENTITY_CHANGED");
Reject(first, process with { RevalidatedCreationFileTimeUtc = 12345679 }, second, coverage, "PROCESS_IDENTITY_CHANGED");
Reject(first, process with { RevalidatedPid = 203 }, second, coverage, "PROCESS_IDENTITY_CHANGED");
Reject(first, process with { RetainedBeforeTracing = false }, second, coverage, "RETAINED_PROCESS_NOT_LIVE");
Reject(first, process with { LiveBeforeToken = false }, second, coverage, "RETAINED_PROCESS_NOT_LIVE");
Reject(first, process with { LiveAfterToken = false }, second, coverage, "RETAINED_PROCESS_NOT_LIVE");
Reject(first, process with { TokenBeforeOrder = 3 }, second, coverage, "OBSERVATION_ORDER_INVALID");
Reject(first, process with { TokenAfterOrder = 8 }, second, coverage, "OBSERVATION_ORDER_INVALID");
Reject(first, process, second with { TraceEndOrder = 7 }, coverage, "OBSERVATION_ORDER_INVALID");
Console.WriteLine($"PASS: {checks} synthetic two-epoch checks; renderer identity and sandbox remain INCONCLUSIVE");
