using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using P0ManagedHost;

// Metadata contracts only. These checks do not initialize CEF or verify a
// renderer sandbox and must never be treated as evidence of either.
var entry = typeof(ManagedEntry).GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
    ?? throw new Exception("UNMANAGED_ENTRY_MISSING");
var marker = entry.GetCustomAttribute<UnmanagedCallersOnlyAttribute>()
    ?? throw new Exception("UNMANAGED_ENTRY_ATTRIBUTE_MISSING");
if (entry.ReturnType != typeof(int) || entry.GetParameters().Length != 2 ||
    entry.GetParameters().Any(p => p.ParameterType != typeof(IntPtr)) ||
    marker.CallConvs is null || !marker.CallConvs.Contains(typeof(CallConvCdecl)))
    throw new Exception("UNMANAGED_ENTRY_ABI_MISMATCH");
Console.WriteLine("PASS: unmanaged entry ABI metadata");
unsafe
{
    delegate* unmanaged[Cdecl]<nint, nint, int> call = &ManagedEntry.Run;
    var originalError = Console.Error;
    using var errors = new StringWriter();
    try
    {
        Console.SetError(errors);
        if (call(0, 0) != 10 || call(1, 0) != 10 || call(0, 1) != 10)
            throw new Exception("MISSING_BOOTSTRAP_CONTEXT_NOT_REJECTED");
    }
    finally { Console.SetError(originalError); }
    if (errors.ToString().Split("MANAGED_FAIL: MISSING_BOOTSTRAP_CONTEXT", StringSplitOptions.None).Length != 4)
        throw new Exception("MISSING_BOOTSTRAP_DIAGNOSTIC_NOT_RECORDED");
}
Console.WriteLine("PASS: unmanaged entry rejects absent bootstrap context");
var field = typeof(CefSharp.CefSettingsBase).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)
    ?? throw new Exception("PINNED_SETTINGS_FIELD_MISSING");
if (field.FieldType.FullName != "CefSharp.Core.CefSettingsBase")
    throw new Exception("PINNED_SETTINGS_FIELD_TYPE_MISMATCH");
Console.WriteLine("PASS: pinned settings wrapper metadata");
var fixture = typeof(ManagedEntry).GetMethod("ControlledHttpsUri", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new Exception("HTTPS_VALIDATOR_MISSING");
string? old = Environment.GetEnvironmentVariable("AEGIS_P0_HTTPS_URL");
try
{
    Environment.SetEnvironmentVariable("AEGIS_P0_HTTPS_URL", null);
    if (fixture.Invoke(null, null) is not null) throw new Exception("MISSING_HTTPS_ACCEPTED");
    foreach (var rejected in new[] { "http://127.0.0.1:1234/", "https://example.com:1234/", "https://127.0.0.1/", "https://u:p@127.0.0.1:1234/" })
    {
        Environment.SetEnvironmentVariable("AEGIS_P0_HTTPS_URL", rejected);
        try { fixture.Invoke(null, null); throw new Exception("UNSAFE_HTTPS_FIXTURE_ACCEPTED"); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
    }
    Environment.SetEnvironmentVariable("AEGIS_P0_HTTPS_URL", "https://127.0.0.1:1234/");
    if (fixture.Invoke(null, null) is not Uri uri || uri.Port != 1234) throw new Exception("LOOPBACK_HTTPS_REJECTED");
}
finally { Environment.SetEnvironmentVariable("AEGIS_P0_HTTPS_URL", old); }
Console.WriteLine("PASS: HTTPS fixture destination validation");
// Synthetic event parsing only; no CEF or renderer is launched.
var probeType = typeof(ManagedEntry).Assembly.GetType("P0ManagedHost.RendererTraceProbe")
    ?? throw new Exception("TRACE_PROBE_MISSING");
var eventMethod = probeType.GetMethod("OnDevToolsEvent") ?? throw new Exception("TRACE_EVENT_MISSING");
var framesField = probeType.GetField("frames", BindingFlags.Instance | BindingFlags.NonPublic)
    ?? throw new Exception("TRACE_FRAMES_MISSING");
var lossField = probeType.GetField("dataLoss", BindingFlags.Instance | BindingFlags.NonPublic)
    ?? throw new Exception("TRACE_LOSS_MISSING");
(int count, bool loss) ParseTrace(string payload)
{
    var probe = Activator.CreateInstance(probeType, true) ?? throw new Exception("TRACE_PROBE_CREATE_FAILED");
    using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload));
    eventMethod.Invoke(probe, new object?[] { null, "Tracing.dataCollected", stream });
    return (((System.Collections.ICollection)framesField.GetValue(probe)!).Count, (bool)lossField.GetValue(probe)!);
}
string item = "{\"name\":\"TracingStartedInBrowser\",\"pid\":" + Environment.ProcessId +
    ",\"args\":{\"data\":{\"frames\":[{\"frame\":\"synthetic\",\"processId\":123}]}}}";
if (ParseTrace("{\"value\":[" + item + "]}") != (1, false)) throw new Exception("TRACE_FRAME_NOT_PARSED");
if (ParseTrace("{\"value\":[" + item + "," + item + "]}").count != 2) throw new Exception("TRACE_DUPLICATE_LOST");
string other = item.Replace("\"pid\":" + Environment.ProcessId, "\"pid\":0", StringComparison.Ordinal);
if (ParseTrace("{\"value\":[" + other + "]}").count != 0) throw new Exception("TRACE_WRONG_BROWSER_ACCEPTED");
if (!ParseTrace("{").loss) throw new Exception("TRACE_MALFORMED_ACCEPTED");
if (!ParseTrace(new string(' ', 1024 * 1024 + 1)).loss) throw new Exception("TRACE_OVERSIZED_ACCEPTED");
Console.WriteLine("PASS: 5 synthetic trace parsing checks; no renderer role or sandbox accepted");
var senderType = typeof(ManagedEntry).Assembly.GetType("P0ManagedHost.TraceCorrelationClient")
    ?? throw new Exception("TRACE_SENDER_MISSING");
var encode = senderType.GetMethod("Encode", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new Exception("TRACE_ENCODER_MISSING");
string validFrame = new string('A', 32);
string encoded = System.Text.Encoding.ASCII.GetString((byte[])encode.Invoke(null, new object[] { 42, validFrame })!);
if (encoded != "P0TRACE1 42 " + validFrame + "\n") throw new Exception("TRACE_ENCODER_WRONG_FRAME");
bool Reject(int pid, string frame)
{
    try { encode.Invoke(null, new object[] { pid, frame }); return false; }
    catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { return true; }
}
if (!Reject(0, validFrame) || !Reject(-1, validFrame) || !Reject(1, new string('A', 31)) ||
    !Reject(1, new string('a', 32)) || !Reject(1, new string('A', 31) + "\n"))
    throw new Exception("TRACE_ENCODER_INVALID_INPUT_ACCEPTED");
var encodeRevalidation = senderType.GetMethod("EncodeRevalidation", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new Exception("TRACE_REVALIDATION_ENCODER_MISSING");
string encodedAgain = System.Text.Encoding.ASCII.GetString((byte[])encodeRevalidation.Invoke(null, new object[] { 42, validFrame })!);
if (encodedAgain != "P0TRACE2 42 " + validFrame + "\n") throw new Exception("TRACE_REVALIDATION_ENCODER_WRONG_FRAME");
Console.WriteLine("PASS: 7 synthetic trace message validation checks");
var encodeFinal = senderType.GetMethod("EncodeFinalization", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new Exception("TRACE_FINALIZATION_ENCODER_MISSING");
var finalBytes = (byte[])encodeFinal.Invoke(null, null)!;
if (System.Text.Encoding.ASCII.GetString(finalBytes) != "P0TRACE3 STABLE\n") throw new Exception("TRACE_FINALIZATION_INVALID");
finalBytes[0] = 0;
if (System.Text.Encoding.ASCII.GetString((byte[])encodeFinal.Invoke(null, null)!) != "P0TRACE3 STABLE\n")
    throw new Exception("TRACE_FINALIZATION_MUTABLE");
Console.WriteLine("PASS: synthetic finalization message is exact and independently copied");
// The timeline contains only bounded test metadata, not URLs or security verdicts.
var timelineType = typeof(ManagedEntry).Assembly.GetType("P0ManagedHost.ProbeTimeline") ?? throw new Exception("TIMELINE_MISSING");
var stageType = timelineType.GetNestedType("Stage", BindingFlags.NonPublic) ?? throw new Exception("TIMELINE_STAGE_MISSING");
var timelineRecord = timelineType.GetMethod("Record", BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new Exception("TIMELINE_RECORD_MISSING");
var timeline = Activator.CreateInstance(timelineType, true) ?? throw new Exception("TIMELINE_CREATE_FAILED");
var oldOutput = Console.Out;
using (var captured = new StringWriter())
{
    try
    {
        Console.SetOut(captured);
        var stage = Enum.Parse(stageType, "FrameLoadStart");
        for (int i = 0; i < 70; i++) timelineRecord.Invoke(timeline, new object?[] { stage, 7, i == 0 ? "bad\nframe" : "A1-B2" });
    }
    finally { Console.SetOut(oldOutput); }
    var lines = captured.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    if (lines.Length != 64 || !lines[0].Contains("frame=UNAVAILABLE", StringComparison.Ordinal) ||
        !lines[1].Contains("frame=A1-B2", StringComparison.Ordinal) ||
        lines.Any(line => !line.StartsWith("MANAGED_TIMELINE: ", StringComparison.Ordinal)))
        throw new Exception("TIMELINE_BOUNDS_OR_SANITIZATION_FAILED");
}
Console.WriteLine("PASS: synthetic timeline bounds and frame sanitization");
// These are synthetic event deliveries, not native CEF lifecycle evidence.
var recordingField = probeType.GetField("recordingEvents", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new Exception("EVENT_RECORDING_FIELD_MISSING");
var invalidField = probeType.GetField("navigationInvalidated", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new Exception("EVENT_INVALIDATION_FIELD_MISSING");
var eventsLostField = probeType.GetField("eventsLost", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new Exception("EVENT_LOSS_FIELD_MISSING");
var detachedField = probeType.GetField("detached", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new Exception("AGENT_DETACH_FIELD_MISSING");
object FreshProbe()
{
    var p = Activator.CreateInstance(probeType, true) ?? throw new Exception("TRACE_PROBE_CREATE_FAILED");
    recordingField.SetValue(p, true);
    return p;
}
void Deliver(object p, string method, string payload)
{
    using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload));
    eventMethod.Invoke(p, new object?[] { null, method, stream });
}
foreach (string method in new[] { "Page.frameNavigated", "Page.frameDetached", "Page.frameStartedNavigating", "Page.navigatedWithinDocument", "Inspector.targetCrashed" })
{
    var p = FreshProbe(); Deliver(p, method, "{}");
    if (!(bool)invalidField.GetValue(p)!) throw new Exception("LIFECYCLE_EVENT_NOT_INVALIDATED_" + method);
}
var browserCommit = "{\"value\":[{\"name\":\"FrameCommittedInBrowser\",\"pid\":" + Environment.ProcessId + "}]}";
var commitProbe = FreshProbe(); Deliver(commitProbe, "Tracing.dataCollected", browserCommit);
if (!(bool)invalidField.GetValue(commitProbe)!) throw new Exception("BROWSER_PRECOMMIT_NOT_INVALIDATED");
var wrongOrigin = FreshProbe();
Deliver(wrongOrigin, "Tracing.dataCollected", browserCommit.Replace("\"pid\":" + Environment.ProcessId, "\"pid\":0", StringComparison.Ordinal));
if ((bool)invalidField.GetValue(wrongOrigin)!) throw new Exception("WRONG_ORIGIN_COMMIT_ACCEPTED");
var overflow = FreshProbe();
for (int i = 0; i < 129; i++) Deliver(overflow, "Page.frameNavigated", "{}");
if (!(bool)eventsLostField.GetValue(overflow)!) throw new Exception("EVENT_OVERFLOW_NOT_INVALIDATED");
var disconnected = FreshProbe();
(probeType.GetMethod("OnDevToolsAgentDetached") ?? throw new Exception("AGENT_DETACH_MISSING")).Invoke(disconnected, new object?[] { null });
if (!(bool)detachedField.GetValue(disconnected)!) throw new Exception("AGENT_DETACH_NOT_INVALIDATED");
Console.WriteLine("PASS: 9 synthetic frame, precommit, origin, overflow and detach checks");
Console.WriteLine("PASS: 4 synthetic managed contracts; CEF and sandbox NOT_TESTED");
