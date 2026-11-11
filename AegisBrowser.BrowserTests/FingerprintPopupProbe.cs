using AegisBrowser;
using CefSharp;
using CefSharp.WinForms;

namespace AegisBrowser.BrowserTests;

internal static partial class Program
{
    // Test-owned scheme-handler page. Its font URL has its own counter, so
    // a parent-frame request cannot be mistaken for a popup request.
    private static string FingerprintPopupHtml(string mode)
    {
        if (mode is not ("baseline" or "restricted")) throw new ArgumentException("Unknown popup mode.");
        return "<!doctype html><meta charset='utf-8'><title>Controlled privacy popup</title>"
            + "<style>@font-face{font-family:PopupFixtureFont;src:url('/popup-font-" + mode
            + ".woff2') format('woff2')}</style><script>"
            + "(async()=>{const c=document.createElement('canvas');const gl=c.getContext('webgl');"
            + "const available=!!gl;gl?.getExtension('WEBGL_lose_context')?.loseContext();"
            + "const gl2=document.createElement('canvas').getContext('webgl2');const available2=!!gl2;gl2?.getExtension('WEBGL_lose_context')?.loseContext();"
            + "let font='pending';try{await document.fonts.load('20px PopupFixtureFont','test');font='settled';}"
            + "catch(_){font='settled';}"
            + "if(window.opener)window.opener.postMessage({kind:'p4fp03-popup',mode:'" + mode
            + "',gl:available,gl2:available2,font},'https://fp-surface.invalid');})();</script>";
    }

    private static async Task MeasureFingerprintPopup(ChromiumWebBrowser browser, string mode)
    {
        if (mode is not ("baseline" or "restricted")) throw new ArgumentException("Unknown popup mode.");
        var life = browser.LifeSpanHandler as SafePopups ?? throw new InvalidOperationException("Production popup handler is unavailable.");
        Check(life.PopupCount == 0, "native popup fixture begins with no existing popup");
        await Js(browser, """
            window.p4fp03PopupResult=null; window.p4fp03Popup=null;
            addEventListener('message',e=>{
              if(e.origin==='https://fp-surface.invalid' && e.source===window.p4fp03Popup &&
                 e.data && e.data.kind==='p4fp03-popup' && typeof e.data.gl==='boolean' && typeof e.data.gl2==='boolean' &&
                 e.data.font==='settled') window.p4fp03PopupResult=e.data;
            }); 'ready'
            """);
        await Cef.UIThreadTaskFactory.StartNew(() =>
        {
            var host = browser.GetBrowserHost();
            var id = host.GetNextDevToolsMessageId();
            var expression = "window.p4fp03Popup=window.open('https://fp-surface.invalid/popup-" + mode + "','p4fp03-surface')";
            if (host.ExecuteDevToolsMethod(id, "Runtime.evaluate",
                new Dictionary<string, object> { ["expression"] = expression, ["userGesture"] = true }) != id)
                throw new InvalidOperationException("Popup command was not submitted with its expected identifier.");
        });
        await Wait(() => life.PopupCount == 1, mode + " native fingerprint popup created");
        await WaitForAsync(async () => await Js(browser, "window.p4fp03PopupResult !== null") == "True",
            mode + " fingerprint popup attributed response");
        Check(await Js(browser, "window.p4fp03PopupResult.mode") == mode, "popup response belongs to expected fixture mode");
        var gl = await Js(browser, "window.p4fp03PopupResult.gl");
        Console.WriteLine($"P4FP03_POPUP_WEBGL: mode={mode} contextAvailable={gl}");
        if (mode == "restricted") Check(gl == "False", "native popup inherits the WebGL restriction");
        else Check(gl is "True" or "False", "native popup baseline WebGL availability observed");
        var gl2 = await Js(browser, "window.p4fp03PopupResult.gl2");
        Console.WriteLine($"P4FP04_POPUP_WEBGL2: mode={mode} contextAvailable={gl2}");
        if (mode == "restricted") Check(gl2 == "False", "native popup inherits the WebGL2 restriction");
        else Check(gl2 is "True" or "False", "native popup baseline WebGL2 availability observed");
        await Js(browser, "window.p4fp03Popup.close(); 'close requested'");
        await Wait(() => life.PopupCount == 0, mode + " native fingerprint popup closed");
        Check(await Js(browser, "document.querySelector('#fixture')?.textContent") == "Controlled font test",
            "opener remains usable after native fingerprint popup closes");
    }
}
