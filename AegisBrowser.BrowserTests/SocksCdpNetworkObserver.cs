using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CefSharp;
using CefSharp.Callback;
using CefSharp.WinForms;

namespace AegisBrowser.BrowserTests;

// Disposable-test-only observation. This does not open a debugging port or alter
// networking. It deliberately keeps only a tiny allowlisted subset of CDP data.
internal sealed class SocksCdpNetworkObserver : IDevToolsMessageObserver
{
    private const int MaxEventBytes = 65536;
    private const int MaxRecords = 256;
    private const int MaxRequests = 128;
    private readonly object gate = new();
    private readonly Dictionary<string, string> requests = new(StringComparer.Ordinal);
    private readonly List<string> records = new();
    private readonly TaskCompletionSource<bool> enabled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IRegistration? registration;
    private int enableMessageId;
    private int dropped;
    private int disposed;

    internal async Task AttachAsync(ChromiumWebBrowser browser)
    {
        try
        {
            await Cef.UIThreadTaskFactory.StartNew(() =>
            {
                // This wrapper is browser-owned: do not dispose it.
                var host = browser.GetBrowserHost();
                registration = host.AddDevToolsMessageObserver(this)
                    ?? throw new InvalidOperationException("CDP observer registration failed.");
                enableMessageId = host.GetNextDevToolsMessageId();
                if (host.ExecuteDevToolsMethod(enableMessageId, "Network.enable", new Dictionary<string, object>()) != enableMessageId)
                    throw new InvalidOperationException("CDP Network.enable could not be sent.");
            }).WaitAsync(TimeSpan.FromSeconds(5));

            if (!await enabled.Task.WaitAsync(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("CDP Network.enable was rejected or the agent detached.");
        }
        catch
        {
            await DetachAsync();
            throw;
        }
    }

    // Call after the browser-close observation but before Cef.Shutdown; the
    // registration is retained until then so late cancellation is observable.
    internal async Task DetachAsync()
    {
        await Cef.UIThreadTaskFactory.StartNew(() =>
        {
            registration?.Dispose();
            registration = null;
        }).WaitAsync(TimeSpan.FromSeconds(5));
    }

    internal void Print()
    {
        lock (gate)
        {
            Console.WriteLine($"P410_CDP_CLOCK stopwatch_frequency={Stopwatch.Frequency}");
            foreach (var record in records) Console.WriteLine(record);
            Console.WriteLine($"P410_CDP_SUMMARY recorded={records.Count} dropped={dropped} tracked={requests.Count}");
        }
    }

    public bool OnDevToolsMessage(IBrowser browser, Stream message) => false;

    // CefSharp owns the observer callback lifetime. Registration disposal is
    // performed separately by DetachAsync on the CEF UI thread; doing it here
    // could recursively dispose the callback. No browser host is owned here.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        enabled.TrySetResult(false);
        Record("event=observer_disposed");
    }

    public void OnDevToolsMethodResult(IBrowser browser, int messageId, bool success, Stream result)
    {
        if (messageId != enableMessageId || messageId == 0) return;
        Record($"event=Network.enable success={success.ToString().ToLowerInvariant()}");
        enabled.TrySetResult(success);
    }

    public void OnDevToolsAgentAttached(IBrowser browser) => Record("event=agent_attached");

    public void OnDevToolsAgentDetached(IBrowser browser)
    {
        Record("event=agent_detached");
        enabled.TrySetResult(false);
    }

    public void OnDevToolsEvent(IBrowser browser, string method, Stream parameters)
    {
        if (method is not ("Network.requestWillBeSent" or "Network.responseReceived" or
            "Network.loadingFailed" or "Network.loadingFinished" or "Network.requestServedFromCache")) return;

        try
        {
            // CEF owns the stream and its contents expire on callback return.
            // A strict byte limit prevents this diagnostic from buffering headers
            // or event bodies without bound; raw event data is never stored.
            byte[] bytes = new byte[MaxEventBytes + 1];
            int total = 0;
            while (total < bytes.Length)
            {
                int count = parameters.Read(bytes, total, bytes.Length - total);
                if (count == 0) break;
                total += count;
            }
            if (total > MaxEventBytes) { Drop(); return; }
            using var json = JsonDocument.Parse(bytes.AsMemory(0, total));
            var root = json.RootElement;
            string? id = GetString(root, "requestId");
            if (!SafeId(id)) { Drop(); return; }

            string? scenario;
            lock (gate)
            {
                if (method == "Network.requestWillBeSent")
                {
                    scenario = root.TryGetProperty("request", out var request)
                        ? ClassifyUrl(GetString(request, "url")) : null;
                    if (scenario == null) { requests.Remove(id!); return; }
                    if (!requests.ContainsKey(id!) && requests.Count >= MaxRequests) { dropped++; return; }
                    requests[id!] = scenario;
                }
                else if (!requests.TryGetValue(id!, out scenario)) return;
            }

            string timestamp = GetNumber(root, "timestamp");
            string detail = "";
            if (method == "Network.loadingFailed")
                detail = $" error={SafeError(GetString(root, "errorText"))} canceled={GetBoolean(root, "canceled")}";
            else if (method == "Network.responseReceived" && root.TryGetProperty("response", out var response))
                detail = $" status={GetNumber(response, "status")} connection_reused={GetBoolean(response, "connectionReused")}";
            Record($"event={method} scenario={scenario} request_id={id} cdp_monotonic_seconds={timestamp}{detail}");
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or ArgumentException)
        {
            Drop();
        }
    }

    private static string? ClassifyUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp) return null;
        if (uri.Host.Equals("aegis-socks.invalid", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath switch { "/fixture" => "socks_success", "/offline" => "socks_offline", "/hold" => "socks_cancel", _ => null };
        return uri.Host == "127.0.0.1" && uri.AbsolutePath == "/socks-rejected" ? "socks_rejection" : null;
    }

    private static bool SafeId(string? id) => id is { Length: > 0 and <= 80 } &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    private static string SafeError(string? error) => error != null && error.StartsWith("net::ERR_", StringComparison.Ordinal) &&
        error.Length <= 80 && error.Skip(9).All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? error : "other";
    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string GetNumber(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number.ToString("R", CultureInfo.InvariantCulture) : "unavailable";
    private static string GetBoolean(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean().ToString().ToLowerInvariant() : "unavailable";

    private void Record(string content)
    {
        long ticks = Stopwatch.GetTimestamp();
        lock (gate)
        {
            if (records.Count == MaxRecords) { dropped++; return; }
            records.Add($"P410_CDP ticks={ticks} {content}");
        }
    }
    private void Drop() { lock (gate) dropped++; }
}
