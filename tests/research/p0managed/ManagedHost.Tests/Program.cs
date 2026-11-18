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
Console.WriteLine("PASS: 4 synthetic managed contracts; CEF and sandbox NOT_TESTED");
