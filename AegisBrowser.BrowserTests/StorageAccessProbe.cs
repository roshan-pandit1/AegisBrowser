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

    // Local CEF-only input qualification; no trust-store or permission changes.
    private static async Task StorageInputPreflight(BrowserView browser)
    {
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

        const string child = """
            <!doctype html><style>body{margin:0}button{width:260px;height:90px}</style><button>Request storage access</button><script>
            document.querySelector('button').addEventListener('click',e=>parent.postMessage({kind:'p404-input',trusted:e.isTrusted,active:navigator.userActivation?.isActive===true},'*'));
            parent.postMessage({kind:'p404-input-ready'},'*');</script>
            """;
        await Js(browser, "document.body.innerHTML='';window.p404frame='pending';window.p404frameReady=false;"
            + "const f=document.createElement('iframe');f.style='position:fixed;left:20px;top:20px;width:350px;height:120px;border:0';"
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
    }

    private sealed class StoragePermissionJournal
    {
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly string run = Guid.NewGuid().ToString("N");
        private int storageCallbacks;
        private int otherCallbacks;
        internal int StorageCallbacks => Volatile.Read(ref storageCallbacks);
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
          parent.postMessage({kind:'p404-ready',supported,before},'*');
        })();
        document.getElementById('p404-request').addEventListener('click',e=>{
          const trusted=e.isTrusted, active=navigator.userActivation?.isActive===true;
          let request;
          try { request=supported ? document.requestStorageAccess() : null; }
          catch(err) { request=Promise.reject(err); }
          (async()=>{
            let result='unsupported', after='unsupported', fetchResult='not-run';
            if(request) result=await Promise.race([
              request.then(()=> 'resolved',err=>'rejected:'+err.name),
              new Promise(resolve=>setTimeout(()=>resolve('timeout'),5000))]);
            if(supported){try{after=String(await document.hasStorageAccess());}catch(err){after='error:'+err.name;}}
            try { await fetch('/p404-cookie-check',{credentials:'include',cache:'no-store'}); fetchResult='completed'; }
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
        await Js(browser, "window.p404ready='pending';window.p404result='pending';"
            + "const f=document.createElement('iframe');f.style='position:fixed;left:20px;top:20px;width:350px;height:120px;border:0;z-index:1000';"
            + "addEventListener('message',e=>{if(e.source===f.contentWindow&&e.origin==='" + server.Third.TrimEnd('/')
            + "'&&e.data){if(e.data.kind==='p404-ready')window.p404ready=JSON.stringify(e.data);"
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
        using var ready = await OpenStorageAccessFrame(browser, server, server.TopA, sandboxed);
        var readyRoot = ready.RootElement;
        var supported = readyRoot.GetProperty("supported").GetBoolean();
        var before = readyRoot.GetProperty("before").GetString();
        Check(await Js(browser, "document.elementFromPoint(80,60)?.tagName") == "IFRAME", "visible storage-access iframe is the input target");
        await ClickStorageControl(browser);
        await WaitForAsync(async () => await Js(browser, "window.p404result") != "pending", "Storage Access user-input result");
        using var data = JsonDocument.Parse(await Js(browser, "window.p404result"));
        var root = data.RootElement;
        var trusted = root.GetProperty("trusted").GetBoolean();
        var active = root.GetProperty("active").GetBoolean();
        Check(trusted && active, "Storage Access request followed a trusted controlled click with transient activation");
        var result = root.GetProperty("result").GetString();
        var after = root.GetProperty("after").GetString();
        if (supported && sandboxed)
            Check(result is not null && result.StartsWith("rejected:", StringComparison.Ordinal),
                "sandboxed iframe without the storage-access token rejects the request");
        var fetch = root.GetProperty("fetchResult").GetString();
        var requests = server.Take("/p404-cookie-check");
        Check(fetch == "completed" && requests.Length == 1, "post-request cookie observation reached the HTTPS server");
        permissions.Record("STORAGE_ACCESS_EVENT", $"scenario={label} supported={supported} trusted={trusted} active={active} before={before} api={result} after={after} permission_callbacks={permissions.StorageCallbacks - beforeCallbacks}");
        permissions.Record("COOKIE_OBSERVATION", $"scenario={label} request_received=true ordinary_cookie={requests[0].Normal} httponly_cookie={requests[0].HttpOnly}");
        if (!sandboxed) await EmbeddedAuthentication(browser, server, permissions, null, label + "_after_access", seed: false);
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
