using AegisBrowser;
using System.Text.Json;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    private readonly record struct StorageObservation(string IdbState, bool IdbA, bool IdbB,
        string CacheState, bool CacheA, bool CacheB, string Access);

    private static string StorageFixturePage(string path)
    {
        if (path == "/storage-access-no-gesture")
            return """
                <!doctype html><script>
                (async()=>{
                  const active=navigator.userActivation?.isActive===true;
                  let before='unsupported', result='unsupported', after='unsupported';
                  if(typeof document.hasStorageAccess==='function') {
                    try { before=String(await document.hasStorageAccess()); } catch(e) { before='error:'+e.name; }
                  }
                  if(typeof document.requestStorageAccess==='function') {
                    try {
                      result=await Promise.race([
                        document.requestStorageAccess().then(()=> 'granted',e=>'rejected:'+e.name),
                        new Promise(resolve=>setTimeout(()=>resolve('timeout'),5000))]);
                    } catch(e) { result='error:'+e.name; }
                  }
                  if(typeof document.hasStorageAccess==='function') {
                    try { after=String(await document.hasStorageAccess()); } catch(e) { after='error:'+e.name; }
                  }
                  parent.postMessage({kind:'p403-access',active,before,result,after},'*');
                })();
                </script>
                """;
        var action = path switch
        {
            "/storage-clear" => "clear",
            "/storage-set-a" => "A",
            "/storage-set-b" => "B",
            "/storage-read" => "read",
            _ => throw new InvalidOperationException("Unexpected storage fixture path.")
        };
        return "<!doctype html><script>const action='" + action + "';" + """
            function request(r) { return new Promise((resolve,reject) => {
              r.onsuccess=()=>resolve(r.result); r.onerror=()=>reject(r.error);
            }); }
            async function indexed() {
              if (!('indexedDB' in window)) return {state:'unsupported',a:false,b:false};
              try {
                const open=indexedDB.open('p403-test-db',1);
                open.onupgradeneeded=()=>open.result.createObjectStore('values');
                const db=await request(open);
                const value=await request(db.transaction('values','readonly').objectStore('values').get('marker'));
                if (action!=='read') {
                  const tx=db.transaction('values','readwrite');
                  if (action==='clear') tx.objectStore('values').clear();
                  else tx.objectStore('values').put(action,'marker');
                  await new Promise((resolve,reject)=>{tx.oncomplete=resolve;tx.onerror=()=>reject(tx.error);tx.onabort=()=>reject(tx.error);});
                }
                db.close(); return {state:'ok',a:value==='A',b:value==='B'};
              } catch (e) { return {state:'error:'+e.name,a:false,b:false}; }
            }
            async function cacheStorage() {
              if (!('caches' in window)) return {state:'unsupported',a:false,b:false};
              try {
                const cache=await caches.open('p403-test-cache');
                const key=new Request(location.origin+'/p403-cache-marker');
                const found=await cache.match(key); const value=found?await found.text():null;
                if (action==='clear') await caches.delete('p403-test-cache');
                else if (action!=='read') await cache.put(key,new Response(action));
                return {state:'ok',a:value==='A',b:value==='B'};
              } catch (e) { return {state:'error:'+e.name,a:false,b:false}; }
            }
            async function accessStatus() {
              if (typeof document.hasStorageAccess!=='function') return 'unsupported';
              try { return (await document.hasStorageAccess())?'available:true':'available:false'; }
              catch (e) { return 'error:'+e.name; }
            }
            (async()=>{
              const i=await indexed(), c=await cacheStorage(), access=await accessStatus();
              parent.postMessage({kind:'p403-storage',idbState:i.state,idbA:i.a,idbB:i.b,
                cacheState:c.state,cacheA:c.a,cacheB:c.b,access},'*');
            })();
            </script>
            """;
    }

    private static async Task HttpsStorageAccessNoGesture(BrowserView browser, PrivacyHttpsServer server)
    {
        const string path = "/storage-access-no-gesture";
        server.Take(path);
        await LoadReady(browser, server.TopA);
        await Js(browser, "window.p403access='pending';addEventListener('message',e=>{if(e.origin==='"
            + server.Third.TrimEnd('/') + "'&&e.data&&e.data.kind==='p403-access')window.p403access=JSON.stringify(e.data);});"
            + "const f=document.createElement('iframe');f.src='" + server.Third + path.TrimStart('/') + "';document.body.appendChild(f);'started'");
        await WaitForAsync(async () => await Js(browser, "window.p403access") != "pending", "Storage Access API no-gesture result");
        Check(server.Take(path).Length == 1, "Storage Access API fixture reached the HTTPS server");
        using var data = JsonDocument.Parse(await Js(browser, "window.p403access"));
        var root = data.RootElement;
        var active = root.GetProperty("active").GetBoolean();
        Check(!active, "Storage Access API no-gesture case has no transient user activation");
        var before = root.GetProperty("before").GetString();
        var result = root.GetProperty("result").GetString();
        var after = root.GetProperty("after").GetString();
        Console.WriteLine($"P403_STORAGE_ACCESS: gesture=false before={before} request={result} after={after}");
    }

    private static async Task<StorageObservation> HttpsStorageFrame(BrowserView browser, PrivacyHttpsServer server, string top, string path)
    {
        server.Take(path);
        await LoadReady(browser, top);
        await Js(browser, "window.p403storage='pending';addEventListener('message',e=>{if(e.origin==='"
            + server.Third.TrimEnd('/') + "'&&e.data&&e.data.kind==='p403-storage')window.p403storage=JSON.stringify(e.data);});"
            + "const f=document.createElement('iframe');f.src='" + server.Third + path.TrimStart('/') + "';document.body.appendChild(f);'started'");
        await WaitForAsync(async () => await Js(browser, "window.p403storage") != "pending", "HTTPS storage API iframe");
        var data = await Js(browser, "window.p403storage");
        Check(server.Take(path).Length == 1, "storage API iframe reached the HTTPS server exactly once");
        using var json = JsonDocument.Parse(data);
        var root = json.RootElement;
        return new(root.GetProperty("idbState").GetString()!, root.GetProperty("idbA").GetBoolean(),
            root.GetProperty("idbB").GetBoolean(), root.GetProperty("cacheState").GetString()!,
            root.GetProperty("cacheA").GetBoolean(), root.GetProperty("cacheB").GetBoolean(),
            root.GetProperty("access").GetString()!);
    }

    private static async Task HttpsStorage(BrowserView browser, PrivacyHttpsServer server, string label, bool restart)
    {
        if (!restart)
        {
            await HttpsStorageFrame(browser, server, server.TopA, "/storage-clear");
            await HttpsStorageFrame(browser, server, server.TopB, "/storage-clear");
            var emptyA = await HttpsStorageFrame(browser, server, server.TopA, "/storage-read");
            var emptyB = await HttpsStorageFrame(browser, server, server.TopB, "/storage-read");
            var writeA = await HttpsStorageFrame(browser, server, server.TopA, "/storage-set-a");
            var firstA = await HttpsStorageFrame(browser, server, server.TopA, "/storage-read");
            var beforeB = await HttpsStorageFrame(browser, server, server.TopB, "/storage-read");
            var writeB = await HttpsStorageFrame(browser, server, server.TopB, "/storage-set-b");
            var finalB = await HttpsStorageFrame(browser, server, server.TopB, "/storage-read");
            var finalA = await HttpsStorageFrame(browser, server, server.TopA, "/storage-read");
            var all = new[] { emptyA, emptyB, writeA, firstA, beforeB, writeB, finalB, finalA };
            var idb = all.All(x => x.IdbState == "ok");
            var cache = all.All(x => x.CacheState == "ok");
            if (idb)
                Check(!emptyA.IdbA && !emptyA.IdbB && !emptyB.IdbA && !emptyB.IdbB
                    && firstA.IdbA && !firstA.IdbB && !beforeB.IdbA && !beforeB.IdbB
                    && finalA.IdbA && !finalA.IdbB && finalB.IdbB && !finalB.IdbA,
                    "IndexedDB test data remains in the corresponding HTTPS top-level partition");
            if (cache)
                Check(!emptyA.CacheA && !emptyA.CacheB && !emptyB.CacheA && !emptyB.CacheB
                    && firstA.CacheA && !firstA.CacheB && !beforeB.CacheA && !beforeB.CacheB
                    && finalA.CacheA && !finalA.CacheB && finalB.CacheB && !finalB.CacheA,
                    "CacheStorage test data remains in the corresponding HTTPS top-level partition");
            Console.WriteLine($"P403_STORAGE: scenario={label} indexeddb={(idb ? "VERIFIED" : "INCONCLUSIVE")} cache={(cache ? "VERIFIED" : "INCONCLUSIVE")} access={finalA.Access}");
        }
        else
        {
            var a = await HttpsStorageFrame(browser, server, server.TopA, "/storage-read");
            var b = await HttpsStorageFrame(browser, server, server.TopB, "/storage-read");
            var idb = a.IdbState == "ok" && b.IdbState == "ok" && a.IdbA && !a.IdbB && b.IdbB && !b.IdbA;
            var cache = a.CacheState == "ok" && b.CacheState == "ok" && a.CacheA && !a.CacheB && b.CacheB && !b.CacheA;
            Console.WriteLine($"P403_STORAGE: scenario={label} indexeddb={(idb ? "VERIFIED" : "INCONCLUSIVE")} cache={(cache ? "VERIFIED" : "INCONCLUSIVE")} access={a.Access}");
        }
    }

    private static async Task<HttpsCookieObservation> HttpsFrame(BrowserView browser, PrivacyHttpsServer server, string top, string path)
    {
        server.Take(path);
        await LoadReady(browser, top);
        await Js(browser, "window.p403frame=false;addEventListener('message',e=>{if(e.origin==='"
            + server.Third.TrimEnd('/') + "'&&e.data==='p403:done')window.p403frame=true;});"
            + "const f=document.createElement('iframe');f.src='" + server.Third + path.TrimStart('/') + "';document.body.appendChild(f);'started'");
        await WaitForAsync(async () => await Js(browser, "window.p403frame") == "True", "HTTPS storage iframe");
        var observations = server.Take(path);
        Check(observations.Length == 1, "controlled HTTPS iframe reached its server exactly once");
        return observations[0];
    }

    private static async Task HttpsChips(BrowserView browser, PrivacyHttpsServer server, string label, bool restart)
    {
        if (!restart)
        {
            await HttpsFrame(browser, server, server.TopA, "/chips-clear");
            await HttpsFrame(browser, server, server.TopB, "/chips-clear");
            var emptyA = await HttpsFrame(browser, server, server.TopA, "/chips-read");
            var emptyB = await HttpsFrame(browser, server, server.TopB, "/chips-read");
            Check(!emptyA.ChipA && !emptyA.ChipB && !emptyB.ChipA && !emptyB.ChipB,
                "partitioned cookie fixture starts with empty test partitions");
            await HttpsFrame(browser, server, server.TopA, "/chips-a");
            var afterA = await HttpsFrame(browser, server, server.TopA, "/chips-read");
            var beforeB = await HttpsFrame(browser, server, server.TopB, "/chips-read");
            if (!afterA.ChipA)
            {
                Console.WriteLine($"P403_CHIPS: scenario={label} result=INCONCLUSIVE reason=partitioned_cookie_not_observed");
                return;
            }
            Check(!beforeB.ChipA && !beforeB.ChipB, "Site B did not receive Site A's partitioned cookie");
            await HttpsFrame(browser, server, server.TopB, "/chips-b");
        }
        var siteB = await HttpsFrame(browser, server, server.TopB, "/chips-read");
        var siteA = await HttpsFrame(browser, server, server.TopA, "/chips-read");
        if (!siteA.ChipA || !siteB.ChipB)
        {
            Console.WriteLine($"P403_CHIPS: scenario={label} result=INCONCLUSIVE reason=expected_partition_cookie_not_observed");
            return;
        }
        Check(!siteA.ChipB && !siteB.ChipA, "partitioned cookies remain separate across the two HTTPS top-level sites");
        Console.WriteLine($"P403_CHIPS: scenario={label} result=VERIFIED site_a_own={siteA.ChipA} site_a_other={siteA.ChipB} site_b_own={siteB.ChipB} site_b_other={siteB.ChipA}");
    }
}
