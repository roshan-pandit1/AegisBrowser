using AegisBrowser;
using AegisBrowser.Core;
using CefSharp;
using CefSharp.WinForms;

namespace AegisBrowser.BrowserTests;

// This fixture uses two disposable, persistent CEF request contexts. The write
// and read phases are separate processes, so the second phase tests restart.
internal static partial class Program
{
    private static int CookieClearProbe(string profileRoot, string phase)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var root = Path.GetFullPath(profileRoot);
        Directory.CreateDirectory(root);
        var initialized = false;
        var result = 1;
        try
        {
            Cef.EnableWaitForBrowsersToClose();
            initialized = Cef.Initialize(new CefSettings
            {
                RootCachePath = root,
                CachePath = Path.Combine(root, "global"),
                CommandLineArgsDisabled = true,
                LogFile = Path.Combine(root, "cef-cookie-clear.log")
            });
            if (!initialized) throw new InvalidOperationException("CEF initialization failed.");
            using var global = Cef.GetGlobalRequestContext();
            if (!global.RegisterSchemeHandlerFactory("https", "browser-tests.invalid", new Fixture()))
                throw new InvalidOperationException("Global cookie fixture registration failed.");

            CefSharp.Handler.RequestContextHandler Handler() => new CefSharp.Handler.RequestContextHandler().OnInitialize(context =>
            {
                if (!context.RegisterSchemeHandlerFactory("https", "browser-tests.invalid", new Fixture()))
                    throw new InvalidOperationException("Cookie fixture registration failed.");
            });
            using var a = new RequestContext(new RequestContextSettings { CachePath = Path.Combine(root, "profile-a") }, Handler());
            using var b = new RequestContext(new RequestContextSettings { CachePath = Path.Combine(root, "profile-b") }, Handler());
            using var host = new Form { Width = 800, Height = 600 };
            host.Shown += async (_, _) =>
            {
                BrowserView? first = null, second = null, current = null;
                try
                {
                    first = new BrowserView(Origin, requestContext: a) { Dock = DockStyle.Left, Width = 350 };
                    second = new BrowserView(Origin, requestContext: b) { Dock = DockStyle.Fill };
                    current = new BrowserView(Origin) { Dock = DockStyle.Bottom, Height = 200 };
                    Observe(first); Observe(second); Observe(current);
                    host.Controls.Add(second); host.Controls.Add(first); host.Controls.Add(current);
                    await Ready(first); await Ready(second); await Ready(current);
                    using var managerA = a.GetCookieManager(null);
                    using var managerB = b.GetCookieManager(null);
                    using var managerCurrent = global.GetCookieManager(null);

                    if (phase == "cookie-clear-write")
                    {
                        Check(await Js(first, "document.cookie") == "", "cookie clear profile A initially empty");
                        Check(await Js(second, "document.cookie") == "", "cookie clear profile B initially empty");
                        Check(await Js(current, "document.cookie") == "", "current-profile cookie store initially empty");
                        await Js(first, "document.cookie='clearprobe=A; Max-Age=86400; Secure; SameSite=Lax; Path=/'; localStorage.setItem('clearprobe','A'); 'ok'");
                        await Js(second, "document.cookie='clearprobe=B; Max-Age=86400; Secure; SameSite=Lax; Path=/'; localStorage.setItem('clearprobe','B'); 'ok'");
                        await Js(current, "document.cookie='clearprobe=G; Max-Age=86400; Secure; SameSite=Lax; Path=/'; localStorage.setItem('clearprobe','G'); 'ok'");
                        Check(await Js(first, "document.cookie") == "clearprobe=A", "cookie clear profile A cookie created");
                        Check(await Js(second, "document.cookie") == "clearprobe=B", "cookie clear profile B cookie created");
                        Check(await Js(current, "document.cookie") == "clearprobe=G", "current-profile cookie created");
                        var httpA = new Cookie { Name = "clearprobe-http", Value = "A", Path = "/", Secure = true, HttpOnly = true, Expires = DateTime.UtcNow.AddDays(1) };
                        var httpB = new Cookie { Name = "clearprobe-http", Value = "B", Path = "/", Secure = true, HttpOnly = true, Expires = DateTime.UtcNow.AddDays(1) };
                        Check(await managerA.SetCookieAsync(Origin, httpA).WaitAsync(TimeSpan.FromSeconds(10)), "profile A HttpOnly cookie created");
                        Check(await managerB.SetCookieAsync(Origin, httpB).WaitAsync(TimeSpan.FromSeconds(10)), "profile B HttpOnly cookie created");
                        Check(await CookieClearHttpOnlyPresent(managerA), "profile A HttpOnly cookie independently observed");
                        Check(await CookieClearHttpOnlyPresent(managerB), "profile B HttpOnly cookie independently observed");

                        // The real confirmation handler must not call its deletion
                        // operation after a cancelled confirmation.
                        var calls = 0;
                        using var dialog = new PrivacyForm(new PrivacySession(), null, clearCookies: () =>
                        {
                            calls++;
                            return ProfileCookieClearing.ClearAsync(a);
                        });
                        Check(await dialog.ClearCookiesConfirmedAsync(false) == PrivacyForm.CookieClearOutcome.Cancelled && calls == 0,
                            "cancelled cookie confirmation does not invoke deletion");
                        Check(await Js(first, "document.cookie") == "clearprobe=A" && await CookieClearHttpOnlyPresent(managerA),
                            "cancelled confirmation leaves profile A cookies intact");
                        using var failedDialog = new PrivacyForm(new PrivacySession(), null,
                            clearCookies: () => Task.FromException(new InvalidOperationException("controlled deletion failure")));
                        Check(await failedDialog.ClearCookiesConfirmedAsync(true) == PrivacyForm.CookieClearOutcome.Failed,
                            "cookie deletion failure is not reported as success");
                        Check(await Js(first, "document.cookie") == "clearprobe=A" && await CookieClearHttpOnlyPresent(managerA),
                            "failed cookie deletion leaves profile A cookies intact");
                        var pendingClear = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        using (var pendingDialog = new PrivacyForm(new PrivacySession(), null, clearCookies: () => pendingClear.Task))
                        {
                            pendingDialog.Show(host);
                            var pendingResult = pendingDialog.ClearCookiesConfirmedAsync(true);
                            Check(pendingDialog.CookieClearInProgress && !pendingResult.IsCompleted,
                                "pending cookie clear stays asynchronous");
                            pendingDialog.Close();
                            Check(!pendingDialog.IsDisposed && pendingDialog.Visible, "privacy window remains open during pending cookie clear");
                            pendingClear.SetResult(true);
                            Check(await pendingResult == PrivacyForm.CookieClearOutcome.Completed, "pending cookie clear completion observed");
                            pendingDialog.Close();
                            Check(pendingDialog.IsDisposed, "privacy window closes after cookie clear completes");
                        }

                        // Exercise the actual confirmation path and production
                        // operation against A, not the global request context.
                        Check(await dialog.ClearCookiesConfirmedAsync(true) == PrivacyForm.CookieClearOutcome.Completed && calls == 1,
                            "confirmed cookie action awaits successful deletion");
                        Check(await Js(first, "document.cookie") == "", "cookie clear removes profile A cookie");
                        Check(await Js(second, "document.cookie") == "clearprobe=B", "cookie clear leaves profile B cookie intact");
                        Check(await Js(current, "document.cookie") == "clearprobe=G", "clearing profile A leaves current-profile cookie intact");
                        Check(!await CookieClearHttpOnlyPresent(managerA), "cookie clear removes profile A HttpOnly cookie");
                        Check(await CookieClearHttpOnlyPresent(managerB), "cookie clear leaves profile B HttpOnly cookie intact");
                        Check(await Js(first, "localStorage.getItem('clearprobe')") == "A", "cookie clear preserves profile A localStorage");
                        Check(await Js(second, "localStorage.getItem('clearprobe')") == "B", "cookie clear preserves profile B localStorage");

                        // The shipped action uses the global request context of the
                        // current profile; verify that exact path independently.
                        using var currentDialog = new PrivacyForm(new PrivacySession(), null);
                        Check(await currentDialog.ClearCookiesConfirmedAsync(true) == PrivacyForm.CookieClearOutcome.Completed,
                            "current-profile action completes on global CEF context");
                        Check(await Js(current, "document.cookie") == "", "current-profile action removes global-context cookie");
                        Check(await Js(second, "document.cookie") == "clearprobe=B" && await CookieClearHttpOnlyPresent(managerB),
                            "current-profile action leaves independent profile B cookies intact");
                        Check(await Js(current, "localStorage.getItem('clearprobe')") == "G", "current-profile action preserves localStorage");

                        using var flushA = new Completion(); using var flushB = new Completion(); using var flushCurrent = new Completion();
                        if (!managerA.FlushStore(flushA) || !managerB.FlushStore(flushB) || !managerCurrent.FlushStore(flushCurrent))
                            throw new InvalidOperationException("Cookie flush refused.");
                        await Task.WhenAll(flushA.Done.Task, flushB.Done.Task, flushCurrent.Done.Task).WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    else
                    {
                        Check(await Js(first, "document.cookie") == "", "cleared profile A cookie remains absent after restart");
                        Check(await Js(second, "document.cookie") == "clearprobe=B", "profile B cookie survives profile A clear and restart");
                        Check(await Js(current, "document.cookie") == "", "cleared current-profile cookie remains absent after restart");
                        Check(!await CookieClearHttpOnlyPresent(managerA), "cleared profile A HttpOnly cookie remains absent after restart");
                        Check(await CookieClearHttpOnlyPresent(managerB), "profile B HttpOnly cookie survives profile A clear and restart");
                        Check(await Js(first, "localStorage.getItem('clearprobe')") == "A", "profile A localStorage survives restart");
                        Check(await Js(second, "localStorage.getItem('clearprobe')") == "B", "profile B localStorage survives restart");
                        Check(await Js(current, "localStorage.getItem('clearprobe')") == "G", "current-profile localStorage survives restart");
                    }

                    await CloseWeb(first); host.Controls.Remove(first); first = null;
                    await CloseWeb(second); host.Controls.Remove(second); second = null;
                    await CloseWeb(current); host.Controls.Remove(current); current = null;
                    Console.WriteLine($"PASS: cookie clear probe completed ({phase})");
                    result = 0;
                }
                catch (Exception ex) { Console.Error.WriteLine(ex); }
                finally
                {
                    foreach (var browser in new[] { first, second, current })
                    {
                        if (browser is { IsDisposed: false, IsBrowserInitialized: true })
                        {
                            try { await CloseWeb(browser); }
                            catch (Exception ex) { Console.Error.WriteLine("Cookie probe browser close failed: " + ex.Message); }
                        }
                    }
                    host.Close();
                }
            };
            Application.Run(host);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); }
        finally
        {
            if (initialized) { Cef.WaitForBrowsersToClose(10000); Cef.Shutdown(); }
        }
        if (result == 0) Console.WriteLine($"PASS: cookie clear probe CEF shutdown completed ({phase})");
        return result;
    }

    private static async Task<bool> CookieClearHttpOnlyPresent(ICookieManager manager)
    {
        var cookies = await manager.VisitUrlCookiesAsync(Origin, includeHttpOnly: true).WaitAsync(TimeSpan.FromSeconds(10));
        if (cookies is null) throw new InvalidOperationException("Cannot inspect test cookies.");
        return cookies.Any(cookie => cookie.Name == "clearprobe-http" && cookie.HttpOnly);
    }
}
