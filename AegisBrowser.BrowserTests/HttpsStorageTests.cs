using AegisBrowser;
using CefSharp;
using System.Text.Json;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    private readonly record struct StorageObservation(string IdbState, bool IdbA, bool IdbB,
        string CacheState, bool CacheA, bool CacheB, string Access);
    private readonly record struct WorkerObservation(string State, bool Registered, bool Controlled, bool MarkerA, bool MarkerB);

    private static async Task CompileHttpsFixtureScripts(BrowserView browser)
    {
        var pages = new[]
        {
            StorageFixturePage("/storage-clear"), StorageFixturePage("/storage-set-a"),
            StorageFixturePage("/storage-read"), StorageFixturePage("/storage-access-no-gesture"),
            StorageAccessGesturePage(),
            ServiceWorkerPage("/sw/frame-clear"), ServiceWorkerPage("/sw/frame-a"),
            ServiceWorkerPage("/sw/frame-read")
        };
        foreach (var page in pages)
        {
            var start = page.IndexOf("<script>", StringComparison.Ordinal) + "<script>".Length;
            var end = page.IndexOf("</script>", start, StringComparison.Ordinal);
            if (start < "<script>".Length || end < start) throw new InvalidOperationException("Fixture script is missing.");
            var script = page[start..end];
            Check(await Js(browser, "(()=>{new Function(" + JsonSerializer.Serialize(script) + ");return true;})()") == "True",
                "Chromium compiled a generated storage fixture script");
        }
        Check(await Js(browser, "(()=>{new Function(" + JsonSerializer.Serialize(ServiceWorkerScript()) + ");return true;})()") == "True",
            "Chromium compiled the generated service worker script");
        Check(await Js(browser, "(()=>{new Function(" + JsonSerializer.Serialize(SubresourceScript("https://example.invalid/"))
            + ");return true;})()") == "True", "Chromium compiled the generated subresource fixture script");
    }

    private static async Task HttpsAuthentication(BrowserView browser, PrivacyHttpsServer server, string label)
    {
        await LoadReady(browser, server.TopA + "auth-start");
        server.Take("/auth-check");
        await LoadReady(browser, server.TopA + "auth-check");
        var first = server.Take("/auth-check");
        Check(first.Length == 1 && first[0].AuthSession, "HTTPS first-party authentication cookie reaches its server");
        server.Take("/auth-provider"); server.Take("/auth-callback");
        await Js(browser, """
            window.p403authDone=false;
            addEventListener('message',e=>{if(e.origin===location.origin && e.data==='p403-oauth-ok')window.p403authDone=true;});
            'ready'
            """);
        await Cef.UIThreadTaskFactory.StartNew(() =>
        {
            var host = browser.GetBrowserHost();
            var id = host.GetNextDevToolsMessageId();
            if (host.ExecuteDevToolsMethod(id, "Runtime.evaluate",
                new Dictionary<string, object> { ["expression"] = "window.open('" + server.Third + "auth-provider','p403-auth')", ["userGesture"] = true }) != id)
                throw new InvalidOperationException("The test popup command was not submitted with its expected ID.");
        });
        await WaitForAsync(async () => await Js(browser, "window.p403authDone") == "True", "HTTPS OAuth-style popup");
        var provider = server.Take("/auth-provider");
        var callback = server.Take("/auth-callback");
        Check(provider.Length == 1 && callback.Length == 1 && callback[0].AuthSession,
            "HTTPS OAuth-style redirect preserves the Lax session cookie, opener and postMessage");
        await Wait(() => ((SafePopups)browser.LifeSpanHandler).PopupCount == 0, "HTTPS OAuth popup closure");
        Console.WriteLine($"P403_HTTPS_AUTH: scenario={label} provider_requests={provider.Length} callback_requests={callback.Length} callback_session={callback[0].AuthSession}");
    }

    private static async Task HttpsAuthRestart(BrowserView browser, PrivacyHttpsServer server)
    {
        server.Take("/auth-check");
        await LoadReady(browser, server.TopA + "auth-check");
        var before = server.Take("/auth-check");
        Check(before.Length == 1 && before[0].AuthSession, "HTTPS authentication cookie persists across restart");
        await LoadReady(browser, server.TopA + "auth-signout");
        server.Take("/auth-check");
        await LoadReady(browser, server.TopA + "auth-check");
        var after = server.Take("/auth-check");
        Check(after.Length == 1 && !after[0].AuthSession, "HTTPS sign-out expires the synthetic authentication cookie");
    }

    private static string ServiceWorkerScript() => """
        self.addEventListener('install',e=>e.waitUntil(self.skipWaiting()));
        self.addEventListener('activate',e=>e.waitUntil(self.clients.claim()));
        self.addEventListener('fetch',e=>{
          if(new URL(e.request.url).pathname==='/sw/probe')
            e.respondWith(new Response('p403-worker-controlled',{headers:{'Content-Type':'text/plain'}}));
        });
        self.addEventListener('message',e=>e.waitUntil((async()=>{
          try {
            const cache=await caches.open('p403-worker-cache');
            const key=new Request(self.location.origin+'/sw/marker');
            const entry=await cache.match(key); const prior=entry?await entry.text():null;
            if(e.data.marker==='A'||e.data.marker==='B') await cache.put(key,new Response(e.data.marker));
            e.ports[0].postMessage({a:prior==='A',b:prior==='B'});
          } catch(err) { e.ports[0].postMessage({error:err.name}); }
        })()));
        """;

    private static string ServiceWorkerPage(string path)
    {
        var action = path switch
        {
            "/sw/frame-clear" => "clear",
            "/sw/frame-a" => "A",
            "/sw/frame-b" => "B",
            "/sw/frame-read" => "read",
            _ => throw new InvalidOperationException("Unexpected service worker fixture path.")
        };
        return "<!doctype html><script>const action='" + action + "';" + """
            const bounded=p=>Promise.race([p,new Promise((_,reject)=>setTimeout(()=>reject(new Error('timeout')),5000))]);
            (async()=>{
              let state='ok', registered=false, controlled=false, a=false, b=false;
              try {
                if(!('serviceWorker' in navigator)||!('caches' in window)) state='unsupported';
                else {
                  let registration=await bounded(navigator.serviceWorker.getRegistration('/sw/'));
                  if(action==='clear') {
                    if(registration) await bounded(registration.unregister());
                    await bounded(caches.delete('p403-worker-cache'));
                    registration=await bounded(navigator.serviceWorker.getRegistration('/sw/'));
                  } else {
                    if(action==='A'||action==='B') {
                      registration=await bounded(navigator.serviceWorker.register('/sw/worker.js',{scope:'/sw/'}));
                      registration=await bounded(navigator.serviceWorker.ready);
                    }
                    if(registration?.active) {
                      if(!navigator.serviceWorker.controller)
                        await bounded(new Promise(resolve=>navigator.serviceWorker.addEventListener('controllerchange',resolve,{once:true})));
                      controlled=(await (await fetch('/sw/probe',{cache:'no-store'})).text())==='p403-worker-controlled';
                      const channel=new MessageChannel();
                      const reply=bounded(new Promise(resolve=>channel.port1.onmessage=e=>resolve(e.data)));
                      registration.active.postMessage({marker:action},[channel.port2]);
                      const observation=await reply; channel.port1.close();
                      if(observation.error) throw new Error('worker:'+observation.error);
                      a=observation.a===true; b=observation.b===true;
                    }
                  }
                  registered=!!registration?.active;
                }
              } catch(e) { state='error:'+e.name+':'+e.message; }
              parent.postMessage({kind:'p403-worker',state,registered,controlled,a,b},'*');
            })();
            </script>
            """;
    }

    private static async Task<WorkerObservation> HttpsWorkerFrame(BrowserView browser, PrivacyHttpsServer server, string top, string path)
    {
        server.Take(path);
        await LoadReady(browser, top);
        await Js(browser, "window.p403worker='pending';addEventListener('message',e=>{if(e.origin==='"
            + server.Third.TrimEnd('/') + "'&&e.data&&e.data.kind==='p403-worker')window.p403worker=JSON.stringify(e.data);});"
            + "const f=document.createElement('iframe');f.src='" + server.Third + path.TrimStart('/') + "';document.body.appendChild(f);'started'");
        await WaitForAsync(async () => await Js(browser, "window.p403worker") != "pending", "service worker fixture");
        Check(server.Take(path).Length == 1, "service worker iframe reached the HTTPS server");
        using var json = JsonDocument.Parse(await Js(browser, "window.p403worker"));
        var root = json.RootElement;
        return new(root.GetProperty("state").GetString()!, root.GetProperty("registered").GetBoolean(),
            root.GetProperty("controlled").GetBoolean(), root.GetProperty("a").GetBoolean(), root.GetProperty("b").GetBoolean());
    }

    private static async Task HttpsServiceWorker(BrowserView browser, PrivacyHttpsServer server, string label, bool restart)
    {
        if (!restart)
        {
            var clearA = await HttpsWorkerFrame(browser, server, server.TopA, "/sw/frame-clear");
            var clearB = await HttpsWorkerFrame(browser, server, server.TopB, "/sw/frame-clear");
            var setA = await HttpsWorkerFrame(browser, server, server.TopA, "/sw/frame-a");
            var beforeB = await HttpsWorkerFrame(browser, server, server.TopB, "/sw/frame-read");
            var setB = await HttpsWorkerFrame(browser, server, server.TopB, "/sw/frame-b");
            var readA = await HttpsWorkerFrame(browser, server, server.TopA, "/sw/frame-read");
            var readB = await HttpsWorkerFrame(browser, server, server.TopB, "/sw/frame-read");
            var all = new[] { clearA, clearB, setA, beforeB, setB, readA, readB };
            if (all.All(x => x.State == "ok"))
            {
                Check(!clearA.Registered && !clearB.Registered && !beforeB.Registered
                    && setA.Registered && setA.Controlled && setB.Registered && setB.Controlled
                    && readA.Registered && readA.Controlled && readA.MarkerA && !readA.MarkerB
                    && readB.Registered && readB.Controlled && readB.MarkerB && !readB.MarkerA,
                    "service worker registrations and cached markers remain in their HTTPS top-level partitions");
                Console.WriteLine($"P403_SERVICE_WORKER: scenario={label} result=VERIFIED");
            }
            else Console.WriteLine($"P403_SERVICE_WORKER: scenario={label} result=INCONCLUSIVE states={string.Join(',', all.Select(x => x.State).Distinct())}");
        }
        else
        {
            var a = await HttpsWorkerFrame(browser, server, server.TopA, "/sw/frame-read");
            var b = await HttpsWorkerFrame(browser, server, server.TopB, "/sw/frame-read");
            var persisted = a.State == "ok" && b.State == "ok" && a.Registered && b.Registered
                && a.Controlled && b.Controlled && a.MarkerA && !a.MarkerB && b.MarkerB && !b.MarkerA;
            Console.WriteLine($"P403_SERVICE_WORKER: scenario={label} result={(persisted ? "VERIFIED" : "INCONCLUSIVE")} states={a.State},{b.State}");
            await HttpsWorkerFrame(browser, server, server.TopA, "/sw/frame-clear");
            await HttpsWorkerFrame(browser, server, server.TopB, "/sw/frame-clear");
        }
    }

    private static async Task HttpsSubresources(BrowserView browser, PrivacyHttpsServer server, bool expectCookie, string label)
    {
        await LoadReady(browser, server.Third + "seed");
        foreach (var path in new[] { "/third-image", "/third-script", "/third-fetch", "/third-redirect", "/third-final" })
            server.Take(path);
        await LoadReady(browser, server.TopA);
        await Js(browser, SubresourceScript(server.Third));
        await WaitForAsync(async () => await Js(browser,
            "['p403image','p403script','p403fetch','p403redirect'].every(k=>window[k]!=='pending')") == "True",
            "HTTPS subresource requests");
        var results = await Js(browser, "['p403image','p403script','p403fetch','p403redirect'].map(k=>window[k]).join(',')");
        Check(results == "done,done,done,done", "HTTPS image, script, fetch and redirect requests completed");
        foreach (var path in new[] { "/third-image", "/third-script", "/third-fetch", "/third-redirect", "/third-final" })
        {
            var observations = server.Take(path);
            Check(observations.Length == 1 && observations[0].Normal == expectCookie && observations[0].HttpOnly == expectCookie,
                "HTTPS " + path + " reached the server with the expected cookie policy");
            Console.WriteLine($"P403_HTTPS_RESOURCE: scenario={label} path={path} requests={observations.Length} normal={observations[0].Normal} httponly={observations[0].HttpOnly}");
        }
    }

    private static string SubresourceScript(string origin) => """
            window.p403image='pending'; window.p403script='pending'; window.p403fetch='pending'; window.p403redirect='pending';
            const i=document.createElement('img'); i.onload=()=>window.p403image='done'; i.onerror=()=>window.p403image='error';
            """ + "i.src='" + origin + "third-image';document.body.appendChild(i);" + """
            const s=document.createElement('script'); s.onload=()=>window.p403script='done'; s.onerror=()=>window.p403script='error';
            """ + "s.src='" + origin + "third-script';document.body.appendChild(s);"
                + "fetch('" + origin + "third-fetch',{mode:'no-cors',credentials:'include'})" + """
              .then(()=>window.p403fetch='done',()=>window.p403fetch='error');
            """ + "fetch('" + origin + "third-redirect',{mode:'no-cors',credentials:'include'})" + """
              .then(()=>window.p403redirect='done',()=>window.p403redirect='error');
            'started'
            """;

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
                        document.requestStorageAccess().then(()=> 'resolved',e=>'rejected:'+e.name),
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

    private static async Task HttpsStorageAccessNoGesture(BrowserView browser, PrivacyHttpsServer server, string label)
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
        Console.WriteLine($"P403_STORAGE_ACCESS: scenario={label} gesture=false before={before} request={result} after={after}");
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
