using System.Text.Json;
using AegisBrowser;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Availability only; a missing worker API is not a network isolation proof.
    private static async Task ObserveWebRtcWorkers(BrowserView browser, bool restrict)
    {
        const string script = """
          (() => {
            const result = window.__fp03workers = {dedicated:'pending',shared:'pending',service:'pending'};
            const source = "self.onmessage=()=>postMessage(typeof RTCPeerConnection);self.onconnect=e=>e.ports[0].postMessage(typeof RTCPeerConnection);";
            const url = URL.createObjectURL(new Blob([source],{type:'application/javascript'}));
            try { const w=new Worker(url); w.onmessage=e=>{result.dedicated=e.data;w.terminate();};
                  w.onerror=()=>{result.dedicated='INCONCLUSIVE';w.terminate();};w.postMessage('probe'); }
            catch (_) { result.dedicated='INCONCLUSIVE'; }
            try { if(typeof SharedWorker!=='function') result.shared='API_UNAVAILABLE';
                  else { const w=new SharedWorker(url);w.port.onmessage=e=>{result.shared=e.data;w.port.close();};
                         w.onerror=()=>{result.shared='INCONCLUSIVE';};w.port.start(); } }
            catch (_) { result.shared='INCONCLUSIVE'; }
            (async()=>{try {
              if(!navigator.serviceWorker){result.service='API_UNAVAILABLE';return;}
              const reg=window.__fp03reg=await navigator.serviceWorker.register('/fp03-worker.js');
              const active=reg.active || (await navigator.serviceWorker.ready).active;
              if(!active){result.service='INCONCLUSIVE';await reg.unregister();return;}
              const handler=e=>{if(e.data?.kind==='fp03-service'){result.service=e.data.rtc;navigator.serviceWorker.removeEventListener('message',handler);}};
              navigator.serviceWorker.addEventListener('message',handler);active.postMessage('probe');
              setTimeout(()=>{navigator.serviceWorker.removeEventListener('message',handler);reg.unregister();},7000);
            } catch(_){result.service='INCONCLUSIVE';}})();
            setTimeout(()=>{for(const k of Object.keys(result))if(result[k]==='pending')result[k]='INCONCLUSIVE';URL.revokeObjectURL(url);},8000);
            return 'started';
          })()
          """;
        Check(await Js(browser, script) == "started", "worker capability observation started");
        await WaitForAsync(async () => await Js(browser, "Object.values(window.__fp03workers).every(x=>x!=='pending')") == "True", "worker capability observations");
        using var result = JsonDocument.Parse(await Js(browser, "JSON.stringify(window.__fp03workers)"));
        foreach (var kind in new[] { "dedicated", "shared", "service" })
        {
            var raw = result.RootElement.GetProperty(kind).GetString();
            var state = raw switch { "function" => "API_PRESENT", "undefined" or "API_UNAVAILABLE" => "API_UNAVAILABLE", _ => "INCONCLUSIVE" };
            Console.WriteLine($"P4FP03_WORKER: mode={(restrict ? "restricted" : "baseline")} context={kind} rtc={state} origin=loopback evidence=browser");
        }
        await Js(browser, "window.__fp03cleanup='pending';Promise.resolve(window.__fp03reg?.unregister()).then(()=>window.__fp03cleanup='done',()=>window.__fp03cleanup='failed');'started'");
        await WaitForAsync(async () => await Js(browser, "window.__fp03cleanup !== 'pending'") == "True", "service worker cleanup");
        Check(await Js(browser, "window.__fp03cleanup") == "done", "disposable service worker registration cleanup completed");
    }
}
