using AegisBrowser;
using CefSharp;
using System.Diagnostics;
using System.Text.Json;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // DevTools input is sent only to the controlled test browser. Unlike a
    // Runtime.evaluate userGesture override, this dispatches mouse input.
    private static async Task ClickStorageControl(BrowserView browser)
    {
        browser.Focus();
        using var client = browser.GetDevToolsClient();
        var move = await client.Input.DispatchMouseEventAsync(CefSharp.DevTools.Input.DispatchMouseEventType.MouseMoved, 80, 60);
        var down = await client.Input.DispatchMouseEventAsync(CefSharp.DevTools.Input.DispatchMouseEventType.MousePressed,
            80, 60, button: CefSharp.DevTools.Input.MouseButton.Left, buttons: 1, clickCount: 1);
        var up = await client.Input.DispatchMouseEventAsync(CefSharp.DevTools.Input.DispatchMouseEventType.MouseReleased,
            80, 60, button: CefSharp.DevTools.Input.MouseButton.Left, buttons: 0, clickCount: 1);
        if (!move.Success || !down.Success || !up.Success) throw new InvalidOperationException("Controlled mouse input was not accepted.");
    }

    // A missing click is a measured capability limitation, not a permission
    // decision. Other errors still fail the probe; no API request is faked.
    private static async Task<bool> ObserveControlledClick(BrowserView browser, string variable, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < limit)
        {
            if (await Js(browser, variable) != "pending") return true;
            await Task.Delay(100);
        }
        return await Js(browser, variable) != "pending";
    }

    // Local CEF-only input qualification; no trust-store or permission changes.
    private static async Task StorageInputPreflight(BrowserView browser)
    {
        await Js(browser, "window.p404never='pending';'ready'");
        Check(!await ObserveControlledClick(browser, "window.p404never", TimeSpan.FromMilliseconds(300)),
            "a missing input event remains unobserved");
        await Js(browser, """
            document.body.innerHTML='<button id="p404button" style="position:fixed;left:20px;top:20px;width:250px;height:100px">Request storage access</button>';
            window.p404input='pending';
            document.getElementById('p404button').addEventListener('click',e=>{
              window.p404input=JSON.stringify({trusted:e.isTrusted,active:navigator.userActivation?.isActive===true});
            });
            'ready'
            """);
        Console.WriteLine("P404_INPUT_TARGET: " + await Js(browser,
            "JSON.stringify({width:innerWidth,height:innerHeight,target:document.elementFromPoint(80,60)?.id||'none'})"));
        await ClickStorageControl(browser);
        await WaitForAsync(async () => await Js(browser, "window.p404input") != "pending", "CEF input preflight");
        var input = await Js(browser, "window.p404input");
        using var json = System.Text.Json.JsonDocument.Parse(input);
        Check(json.RootElement.GetProperty("trusted").GetBoolean()
            && json.RootElement.GetProperty("active").GetBoolean(),
            "controlled CEF mouse input produced a trusted click with transient activation");
        Console.WriteLine("P404_INPUT: trusted=true transient_activation=true scope=local_cef_only");

        var child = StorageInputFramePage();
        await Js(browser, "document.body.innerHTML='';window.p404frame='pending';window.p404frameReady=false;"
            + "const f=document.createElement('iframe');f.id=f.name='aegis-p405-input';f.style='position:fixed;left:20px;top:20px;width:350px;height:120px;border:0';"
            + "addEventListener('message',e=>{if(e.source===f.contentWindow&&e.data){if(e.data.kind==='p404-input-ready')window.p404frameReady=true;"
            + "if(e.data.kind==='p404-input')window.p404frame=JSON.stringify(e.data);}});"
            + "f.srcdoc=" + JsonSerializer.Serialize(child) + ";document.body.appendChild(f);'ready'");
        await WaitForAsync(async () => await Js(browser, "window.p404frameReady") == "True", "CEF iframe input readiness");
        Check(await Js(browser, "document.elementFromPoint(80,60)?.tagName") == "IFRAME", "local iframe is the input target");
        await ClickStorageControl(browser);
        await WaitForAsync(async () => await Js(browser, "window.p404frame") != "pending", "CEF iframe input preflight");
        using var frame = JsonDocument.Parse(await Js(browser, "window.p404frame"));
        Check(frame.RootElement.GetProperty("trusted").GetBoolean() && frame.RootElement.GetProperty("active").GetBoolean(),
            "controlled CEF mouse input activates the visible iframe button");

        // This is still an in-process scheme fixture; it tests cross-origin
        // input routing, not HTTPS sockets or storage permission behavior.
        await LoadReady(browser, StorageTopA);
        await Js(browser, "window.p404frame='pending';window.p404frameReady=false;"
            + "const f=document.createElement('iframe');f.id=f.name='aegis-p405-input';f.style='position:fixed;left:20px;top:20px;width:350px;height:120px;border:0';"
            + "addEventListener('message',e=>{if(e.source===f.contentWindow&&e.origin==='" + StorageThird.TrimEnd('/')
            + "'&&e.data){if(e.data.kind==='p404-input-ready')window.p404frameReady=true;"
            + "if(e.data.kind==='p404-input')window.p404frame=JSON.stringify(e.data);}});"
            + "f.src='" + StorageThird + "p404-input';document.body.appendChild(f);'ready'");
        await WaitForAsync(async () => await Js(browser, "window.p404frameReady") == "True", "cross-origin iframe input readiness");
        Check(await Js(browser, "document.elementFromPoint(80,60)?.tagName") == "IFRAME", "cross-origin iframe is the input target");
        await Js(browser, "document.getElementById('aegis-p405-input').contentWindow.postMessage('p405-script-click','" + StorageThird.TrimEnd('/') + "');'sent'");
        await WaitForAsync(async () => await Js(browser, "window.p404frame") != "pending", "script-generated iframe click");
        using (var scripted = JsonDocument.Parse(await Js(browser, "window.p404frame")))
            Check(!scripted.RootElement.GetProperty("trusted").GetBoolean(), "script-generated iframe click is not trusted input");
        await Js(browser, "window.p404frame='pending';'reset'");
        Check(!await DispatchCrossOriginInput(browser, StorageThird + "wrong", "window.p404frame", "wrong_target"),
            "wrong frame URL is rejected before input");
        Check(await Js(browser, "window.p404frame") == "pending", "wrong frame target did not generate a click");
        await Js(browser, "document.getElementById('aegis-p405-input').style.display='none';'hidden'");
        Check(!await DispatchCrossOriginInput(browser, StorageThird + "p404-input", "window.p404frame", "hidden_target"),
            "hidden iframe is rejected before input");
        await Js(browser, "document.getElementById('aegis-p405-input').style.display='block';'shown'");
        await Js(browser, "document.getElementById('aegis-p405-input').style.left='-1000px';'offscreen'");
        Check(!await DispatchCrossOriginInput(browser, StorageThird + "p404-input", "window.p404frame", "offscreen_target"),
            "offscreen iframe coordinates are rejected before input");
        await Js(browser, "document.getElementById('aegis-p405-input').style.left='20px';'restored'");
        var delivered = await DispatchCrossOriginInput(browser, StorageThird + "p404-input", "window.p404frame", "preflight");
        if (!delivered)
        {
            Console.WriteLine("P404_CROSS_ORIGIN_INPUT: INCONCLUSIVE reason=qualified_input_not_observed");
        }
        else
        {
            using var crossFrame = JsonDocument.Parse(await Js(browser, "window.p404frame"));
            Check(crossFrame.RootElement.GetProperty("trusted").GetBoolean() && crossFrame.RootElement.GetProperty("active").GetBoolean(),
                "controlled CEF mouse input activates the cross-origin iframe button");
            Console.WriteLine("P404_CROSS_ORIGIN_INPUT: VERIFIED trusted=true transient_activation=true");
        }
        await Js(browser, "document.getElementById('aegis-p405-input').remove();'removed'");
        Check(!await DispatchCrossOriginInput(browser, StorageThird + "p404-input", "window.p404frame", "destroyed_target"),
            "destroyed iframe is rejected before input");
    }

    private static string StorageInputFramePage() => """
        <!doctype html><style>body{margin:0}button{width:260px;height:90px}</style><button>Request storage access</button><script>
        document.querySelector('button').addEventListener('click',e=>parent.postMessage({kind:'p404-input',trusted:e.isTrusted,active:navigator.userActivation?.isActive===true},'*'));
        addEventListener('message',e=>{if(e.source===parent&&e.data==='p405-script-click')document.querySelector('button').click();});
        requestAnimationFrame(()=>requestAnimationFrame(()=>parent.postMessage({kind:'p404-input-ready'},'*')));</script>
        """;

    private sealed class StoragePermissionJournal
    {
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly string run = Guid.NewGuid().ToString("N");
        private int storageCallbacks;
        private int storageDenials;
        private int otherCallbacks;
        internal int StorageCallbacks => Volatile.Read(ref storageCallbacks);
        internal int StorageDenials => Volatile.Read(ref storageDenials);
        internal int OtherCallbacks => Volatile.Read(ref otherCallbacks);
        internal void Record(string category, string details) =>
            Console.WriteLine($"P404_{category}: run={run} elapsed_ms={elapsed.ElapsedMilliseconds} {details}");
        internal void Report(string message)
        {
            var storage = message.Contains("StorageAccess", StringComparison.OrdinalIgnoreCase);
            if (storage) Interlocked.Increment(ref storageCallbacks);
            else Interlocked.Increment(ref otherCallbacks);
            var decision = message.StartsWith("Permission blocked:", StringComparison.Ordinal) ? "blocked"
                : message.StartsWith("Permission decision requested", StringComparison.Ordinal) ? "prompt"
                : "other";
            if (storage && decision == "blocked") Interlocked.Increment(ref storageDenials);
            var requesting = message.Contains("localhost", StringComparison.OrdinalIgnoreCase) ? "controlled_third" : "unknown";
            Record("PERMISSION_EVENT", $"type={(storage ? "storage_access" : "other")} decision={decision} requesting={requesting} top=not_exposed_by_report");
        }
        internal void Print() => Console.WriteLine($"P404_PERMISSION_SUMMARY: run={run} storage_callbacks={StorageCallbacks} other_callbacks={Volatile.Read(ref otherCallbacks)}");
    }

    private static async Task InteractWithRequestingSite(BrowserView browser, PrivacyHttpsServer server)
    {
        await LoadReady(browser, server.Third + "seed");
        await Js(browser, """
            document.body.innerHTML='<button id="p404top" style="position:fixed;left:20px;top:20px;width:250px;height:100px">Continue</button>';
            window.p404top='pending';document.getElementById('p404top').addEventListener('click',e=>{
              window.p404top=JSON.stringify({trusted:e.isTrusted,active:navigator.userActivation?.isActive===true});
            });'ready'
            """);
        Check(await Js(browser, "document.elementFromPoint(80,60)?.id") == "p404top", "requesting-site top-level control is the input target");
        await ClickStorageControl(browser);
        await WaitForAsync(async () => await Js(browser, "window.p404top") != "pending", "requesting-site interaction");
        using var input = JsonDocument.Parse(await Js(browser, "window.p404top"));
        Check(input.RootElement.GetProperty("trusted").GetBoolean() && input.RootElement.GetProperty("active").GetBoolean(),
            "controlled top-level requesting-site click has transient activation");
    }

    private static string StorageAccessGesturePage() => """
        <!doctype html><style>body{margin:0}button{width:260px;height:90px}</style>
        <button id="p404-request">Request storage access</button><script>
        let before='unsupported';
        const supported=typeof document.hasStorageAccess==='function' && typeof document.requestStorageAccess==='function';
        (async()=>{
          if(supported){try{before=String(await document.hasStorageAccess());}catch(e){before='error:'+e.name;}}
          await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));
          const target=document.elementFromPoint(60,40)?.id||'none';
          parent.postMessage({kind:'p404-ready',supported,before,target},'*');
        })();
        document.getElementById('p404-request').addEventListener('click',e=>{
          const trusted=e.isTrusted, active=navigator.userActivation?.isActive===true;
          parent.postMessage({kind:'p404-click',trusted,active},'*');
          if(!trusted || !active)return;
          parent.postMessage({kind:'p405-requested',attempted:supported},'*');
          let request;
          try { request=supported ? document.requestStorageAccess() : null; }
          catch(err) { request=Promise.reject(err); }
          (async()=>{
            let result='unsupported', after='unsupported', fetchResult='not-run';
            if(request) result=await Promise.race([
              request.then(()=> 'resolved',err=>'rejected:'+err.name),
              new Promise(resolve=>setTimeout(()=>resolve('timeout'),5000))]);
            if(supported){try{after=await Promise.race([document.hasStorageAccess().then(String),new Promise(resolve=>setTimeout(()=>resolve('timeout'),5000))]);}catch(err){after='error:'+err.name;}}
            try { fetchResult=await Promise.race([fetch('/p404-cookie-check',{credentials:'include',cache:'no-store'}).then(()=> 'completed'),new Promise(resolve=>setTimeout(()=>resolve('timeout'),5000))]); }
            catch(err) { fetchResult='error:'+err.name; }
            parent.postMessage({kind:'p404-result',trusted,active,before,result,after,fetchResult},'*');
          })();
        });
        </script>
        """;

    private static async Task<JsonDocument> OpenStorageAccessFrame(BrowserView browser, PrivacyHttpsServer server, string top, bool sandboxed = false)
    {
        server.Take("/p404-access");
        await LoadReady(browser, top);
        await Js(browser, "window.p404ready='pending';window.p404click='pending';window.p404result='pending';window.p405requested='pending';"
            + "const f=document.createElement('iframe');f.id=f.name='aegis-p405-input';f.style='position:fixed;left:20px;top:20px;width:350px;height:120px;border:0;z-index:1000';"
            + "addEventListener('message',e=>{if(e.source===f.contentWindow&&e.origin==='" + server.Third.TrimEnd('/')
            + "'&&e.data){if(e.data.kind==='p404-ready')window.p404ready=JSON.stringify(e.data);"
            + "if(e.data.kind==='p404-click')window.p404click=JSON.stringify(e.data);"
            + "if(e.data.kind==='p405-requested')window.p405requested=JSON.stringify(e.data);"
            + "if(e.data.kind==='p404-result')window.p404result=JSON.stringify(e.data);}});"
            + (sandboxed ? "f.setAttribute('sandbox','allow-scripts allow-same-origin');" : "")
            + "f.src='" + server.Third + "p404-access';document.body.appendChild(f);'started'");
        await WaitForAsync(async () => await Js(browser, "window.p404ready") != "pending", "Storage Access iframe readiness");
        Check(server.Take("/p404-access").Length == 1, "Storage Access iframe reached the controlled HTTPS server");
        return JsonDocument.Parse(await Js(browser, "window.p404ready"));
    }

    private static async Task EmbeddedAuthentication(BrowserView browser, PrivacyHttpsServer server, StoragePermissionJournal journal, bool? expectCookie, string label, bool seed = true)
    {
        if (seed) await LoadReady(browser, server.Third + "seed");
        server.Take("/p404-embedded-auth");
        await LoadReady(browser, server.TopA);
        await Js(browser, "window.p404auth='pending';const f=document.createElement('iframe');"
            + "addEventListener('message',e=>{if(e.source===f.contentWindow&&e.origin==='" + server.Third.TrimEnd('/')
            + "'&&e.data&&e.data.kind==='p404-embedded-auth')window.p404auth=String(e.data.signedIn);});"
            + "f.src='" + server.Third + "p404-embedded-auth';document.body.appendChild(f);'started'");
        await WaitForAsync(async () => await Js(browser, "window.p404auth") != "pending", "embedded authentication fixture");
        var observations = server.Take("/p404-embedded-auth");
        var signedIn = await Js(browser, "window.p404auth");
        Check(observations.Length == 1, "embedded synthetic session reached the HTTPS server");
        Check(signedIn == (observations[0].Normal && observations[0].HttpOnly ? "true" : "false"),
            "embedded synthetic session reflects the server-observed cookies");
        if (expectCookie.HasValue)
            Check(observations[0].Normal == expectCookie.Value && observations[0].HttpOnly == expectCookie.Value,
                "embedded synthetic session follows the selected cookie policy");
        journal.Record("AUTH_EVENT", $"scenario={label} request_received=true ordinary_cookie={observations[0].Normal} httponly_cookie={observations[0].HttpOnly} synthetic_session={signedIn}");
    }

    private static async Task StorageAccessGesture(BrowserView browser, PrivacyHttpsServer server, StoragePermissionJournal permissions, string label, bool sandboxed = false)
    {
        await InteractWithRequestingSite(browser, server);
        server.Take("/p404-cookie-check");
        var beforeCallbacks = permissions.StorageCallbacks;
        var beforeDenials = permissions.StorageDenials;
        var beforeOtherCallbacks = permissions.OtherCallbacks;
        using var ready = await OpenStorageAccessFrame(browser, server, server.TopA, sandboxed);
        var readyRoot = ready.RootElement;
        var supported = readyRoot.GetProperty("supported").GetBoolean();
        var before = readyRoot.GetProperty("before").GetString();
        Check(readyRoot.GetProperty("target").GetString() == "p404-request", "iframe button is ready at the controlled input point");
        Check(await Js(browser, "document.elementFromPoint(80,60)?.tagName") == "IFRAME", "visible storage-access iframe is the input target");
        if (!await DispatchCrossOriginInput(browser, server.Third + "p404-access", "window.p404click", label))
        {
            permissions.Record("STORAGE_ACCESS_EVENT", $"scenario={label} result=INCONCLUSIVE reason=qualified_input_not_observed");
            return;
        }
        using var click = JsonDocument.Parse(await Js(browser, "window.p404click"));
        permissions.Record("INPUT_EVENT", $"scenario={label} trusted={click.RootElement.GetProperty("trusted").GetBoolean()} active={click.RootElement.GetProperty("active").GetBoolean()}");
        await WaitForAsync(async () => await Js(browser, "window.p405requested") != "pending", "Storage Access request dispatch");
        using var requested = JsonDocument.Parse(await Js(browser, "window.p405requested"));
        Console.WriteLine($"P405_STORAGE_ACCESS_REQUESTED: scenario={label} attempted={requested.RootElement.GetProperty("attempted").GetBoolean()}");
        await WaitForAsync(async () => await Js(browser, "window.p404result") != "pending", "Storage Access user-input result");
        using var data = JsonDocument.Parse(await Js(browser, "window.p404result"));
        var root = data.RootElement;
        var trusted = root.GetProperty("trusted").GetBoolean();
        var active = root.GetProperty("active").GetBoolean();
        Check(trusted && active, "Storage Access request followed a trusted controlled click with transient activation");
        var result = root.GetProperty("result").GetString();
        var after = root.GetProperty("after").GetString();
        Console.WriteLine($"P405_STORAGE_ACCESS_RESULT: scenario={label} api={result} before={before} after={after}");
        if (supported && sandboxed)
            Check(result is not null && result.StartsWith("rejected:", StringComparison.Ordinal),
                "sandboxed iframe without the storage-access token rejects the request");
        var fetch = root.GetProperty("fetchResult").GetString();
        var requests = server.Take("/p404-cookie-check");
        Check(fetch == "completed" && requests.Length == 1, "post-request cookie observation reached the HTTPS server");
        var incomplete = !supported || result is "timeout" or "unsupported" || after is "timeout" or "unsupported";
        var classification = incomplete ? "result=INCONCLUSIVE reason=api_unavailable_or_timeout " : "";
        permissions.Record("STORAGE_ACCESS_EVENT", $"scenario={label} {classification}supported={supported} trusted={trusted} active={active} before={before} api={result} after={after} storage_named_callbacks={permissions.StorageCallbacks - beforeCallbacks} other_callbacks={permissions.OtherCallbacks - beforeOtherCallbacks}");
        permissions.Record("COOKIE_OBSERVATION", $"scenario={label} request_received=true ordinary_cookie={requests[0].Normal} httponly_cookie={requests[0].HttpOnly}");
        if (label == "enabled" && !incomplete && result == "rejected:NotAllowedError")
        {
            var callbackWait = Stopwatch.StartNew();
            while (permissions.StorageDenials == beforeDenials && callbackWait.Elapsed < TimeSpan.FromSeconds(2))
                await Task.Delay(50);
        }
        var deniedByHandler = permissions.StorageDenials > beforeDenials;
        if (label == "enabled" && !incomplete)
        {
            if (deniedByHandler)
            {
                Check(before == "false" && result == "rejected:NotAllowedError" && after == "false",
                    "production Storage Access denial agrees with the API result");
                Check(!requests[0].Normal && !requests[0].HttpOnly,
                    "denied Storage Access leaves synthetic third-party cookies suppressed");
                Console.WriteLine("P405_PERMISSION_DECISION: scenario=enabled result=DENIED_BY_HANDLER user_decision=not_observed");
            }
            else Console.WriteLine("P405_PERMISSION_DECISION: scenario=enabled result=INCONCLUSIVE reason=no_named_handler_denial");
        }
        bool? expectAfter = label == "enabled" && deniedByHandler ? false
            : label == "baseline" && before == "true" && result == "resolved" ? true : null;
        if (!sandboxed) await EmbeddedAuthentication(browser, server, permissions, expectAfter, label + "_after_access", seed: false);
        using var siteA = await OpenStorageAccessFrame(browser, server, server.TopA);
        using var siteB = await OpenStorageAccessFrame(browser, server, server.TopB);
        permissions.Record("STORAGE_ACCESS_PERSISTENCE", $"scenario={label} site_a={siteA.RootElement.GetProperty("before").GetString()} site_b={siteB.RootElement.GetProperty("before").GetString()}");
    }

    private static async Task StorageAccessAfterRestart(BrowserView browser, PrivacyHttpsServer server, StoragePermissionJournal permissions)
    {
        using var a = await OpenStorageAccessFrame(browser, server, server.TopA);
        using var b = await OpenStorageAccessFrame(browser, server, server.TopB);
        permissions.Record("STORAGE_ACCESS_PERSISTENCE", $"scenario=restart_enabled site_a={a.RootElement.GetProperty("before").GetString()} site_b={b.RootElement.GetProperty("before").GetString()}");
    }
}
