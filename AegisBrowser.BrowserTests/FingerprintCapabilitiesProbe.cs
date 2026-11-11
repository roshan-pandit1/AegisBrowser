using System.Text.Json;
using CefSharp.WinForms;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Report only bounded capability outcomes. Do not collect graphics pixels,
    // audio samples, adapter attributes, font names or fingerprint hashes.
    private const string CapabilityFunction = """
        async function measureCapabilities() {
          const result = {};
          try {
            const canvas = typeof document !== 'undefined'
              ? document.createElement('canvas')
              : (typeof OffscreenCanvas === 'function' ? new OffscreenCanvas(1,1) : null);
            if (!canvas) result.canvas = 'UNSUPPORTED';
            else {
              canvas.width = canvas.height = 1;
              const ctx = canvas.getContext('2d');
              if (!ctx) result.canvas = 'INCONCLUSIVE';
              else { ctx.fillRect(0,0,1,1); ctx.getImageData(0,0,1,1); result.canvas = 'FEATURE_FUNCTIONAL'; }
            }
          } catch (_) { result.canvas = 'REQUEST_DENIED'; }
          if (typeof OfflineAudioContext !== 'function') result.offlineAudio = 'API_UNAVAILABLE';
          else {
            try {
              const offline = new OfflineAudioContext(1, 8, 44100);
              result.offlineAudio = await Promise.race([
                offline.startRendering().then(() => 'FEATURE_FUNCTIONAL'),
                new Promise(resolve => setTimeout(() => resolve('INCONCLUSIVE'), 3000))
              ]);
            } catch (_) { result.offlineAudio = 'REQUEST_DENIED'; }
          }
          result.audio = typeof AudioContext === 'function' ? 'API_PRESENT' : 'API_UNAVAILABLE';
          result.localFonts = typeof queryLocalFonts === 'function' ? 'API_PRESENT' : 'API_UNAVAILABLE';
          if (!navigator.gpu) result.webgpu = 'API_UNAVAILABLE';
          else {
            try {
              const adapter = await Promise.race([
                navigator.gpu.requestAdapter().then(x => x ? 'FEATURE_FUNCTIONAL' : 'INCONCLUSIVE'),
                new Promise(resolve => setTimeout(() => resolve('INCONCLUSIVE'), 3000))
              ]);
              result.webgpu = adapter;
            } catch (_) { result.webgpu = 'REQUEST_DENIED'; }
          }
          return result;
        }
        """;

    private static string CapabilityFrameHtml =>
        "<!doctype html><meta charset='utf-8'><script>" + CapabilityFunction
        + "measureCapabilities().then(result=>parent.postMessage({kind:'aegis-fp-capabilities',result},'*'));"
        + "</script><p>Controlled frame</p>";

    private static async Task MeasureFingerprintContexts(ChromiumWebBrowser browser, string mode)
    {
        // Match both the iframe WindowProxy and its origin; a role label alone
        // must not be treated as proof of frame attribution.
        var setup = """
            window.fpCapabilities = { main:'pending', same:'pending', cross:'pending', worker:'pending' };
            const same = document.createElement('iframe'), cross = document.createElement('iframe');
            addEventListener('message', e => {
              if (!e.data || e.data.kind !== 'aegis-fp-capabilities') return;
              if (e.source === same.contentWindow && e.origin === 'https://fp-surface.invalid')
                window.fpCapabilities.same = e.data.result;
              if (e.source === cross.contentWindow && e.origin === 'https://fp-other.invalid')
                window.fpCapabilities.cross = e.data.result;
            });
            same.src = 'https://fp-surface.invalid/capability-frame';
            cross.src = 'https://fp-other.invalid/capability-frame';
            document.body.append(same, cross);
            """;
        var workerSetup = """
            try {
              const workerSource = CAPABILITY_SOURCE + ';measureCapabilities().then(r=>postMessage(r)).catch(()=>postMessage(null));';
              const url = URL.createObjectURL(new Blob([workerSource], {type:'text/javascript'}));
              const worker = new Worker(url);
              worker.onmessage = e => { window.fpCapabilities.worker = e.data || 'INCONCLUSIVE'; worker.terminate(); URL.revokeObjectURL(url); };
              worker.onerror = () => { window.fpCapabilities.worker = 'INCONCLUSIVE'; worker.terminate(); URL.revokeObjectURL(url); };
              setTimeout(() => { if (window.fpCapabilities.worker === 'pending') { window.fpCapabilities.worker = 'INCONCLUSIVE'; worker.terminate(); URL.revokeObjectURL(url); } }, 8000);
            } catch (_) { window.fpCapabilities.worker = 'INCONCLUSIVE'; }
            """.Replace("CAPABILITY_SOURCE", JsonSerializer.Serialize(CapabilityFunction));
        await Js(browser, setup + CapabilityFunction
            + "measureCapabilities().then(r=>window.fpCapabilities.main=r).catch(()=>window.fpCapabilities.main='INCONCLUSIVE');"
            + workerSetup + "'started'");
        await WaitForAsync(async () => await Js(browser,
            "Object.values(window.fpCapabilities).every(x=>x!=='pending')") == "True",
            mode + " controlled fingerprint context results");
        using var results = JsonDocument.Parse(await Js(browser, "JSON.stringify(window.fpCapabilities)"));
        foreach (var context in new[] { "main", "same", "cross", "worker" })
        {
            var value = results.RootElement.GetProperty(context);
            if (value.ValueKind == JsonValueKind.String)
            {
                Check(value.GetString() == "INCONCLUSIVE", "unsupported capability context is explicit");
                Console.WriteLine($"P4FP02_CAPABILITIES: mode={mode} context={context} result=INCONCLUSIVE");
                continue;
            }
            Check(value.ValueKind == JsonValueKind.Object, "controlled context returned capability outcomes");
            foreach (var surface in new[] { "canvas", "audio", "offlineAudio", "webgpu", "localFonts" })
            {
                var state = value.GetProperty(surface).GetString();
                Check(state is "API_PRESENT" or "API_UNAVAILABLE" or "REQUEST_DENIED"
                    or "FEATURE_FUNCTIONAL" or "UNSUPPORTED" or "INCONCLUSIVE",
                    "capability result uses a bounded state");
                Console.WriteLine($"P4FP02_CAPABILITY: mode={mode} context={context} surface={surface} state={state}");
            }
        }
    }
}
