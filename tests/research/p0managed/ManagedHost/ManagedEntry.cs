using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CefSharp;
using CefSharp.WinForms;

namespace P0ManagedHost;

// Isolated experiment, invoked synchronously from the browser-process-only
// hostfxr path. This is not an Aegis entry point and is never loaded by the
// existing production application.
public static class ManagedEntry
{
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int Run(nint instance, nint sandboxInfo)
    {
        if (instance == 0 || sandboxInfo == 0)
        {
            Console.Error.WriteLine("MANAGED_FAIL: MISSING_BOOTSTRAP_CONTEXT");
            return 10;
        }
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            Console.Error.WriteLine("MANAGED_FAIL: BROWSER_THREAD_NOT_STA");
            return 11;
        }

        bool initialized = false;
        int result = 1;
        string root = Path.Combine(Path.GetTempPath(), "aegis-p0-managed-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var settings = new CefSettings
            {
                RootCachePath = root,
                CachePath = Path.Combine(root, "cache"),
                BrowserSubprocessPath = string.Empty,
                CommandLineArgsDisabled = true,
                LogSeverity = LogSeverity.Disable
            };
            // The test fork rejects custom CEF arguments. The WinForms settings
            // constructor adds defaults, so remove them for this experiment.
            settings.CefCommandLineArgs.Clear();
            Cef.EnableWaitForBrowsersToClose();
            initialized = InitializePatched(settings, instance, sandboxInfo);
            if (!initialized) throw new InvalidOperationException("CEF_INITIALIZE_FALSE");
            Console.WriteLine("MANAGED_EVENT: CEF_INITIALIZED_SANDBOX_CONTEXT_PRESENT");

            using var form = new ProbeForm();
            Application.Run(form);
            result = form.Result;
            Console.WriteLine("MANAGED_EVENT: WINFORMS_LOOP_RETURNED");
        }
        catch (Exception ex)
        {
            // Disposable synthetic fixture only: retain the nested exception
            // and managed stack so failed native/binding loads are diagnosable.
            Console.Error.WriteLine("MANAGED_FAIL: " + ex);
        }
        finally
        {
            if (initialized)
            {
                try
                {
                    // CefSharp exposes no success result for this bounded wait.
                    // OnBeforeClose is checked separately by ProbeForm; this
                    // call alone is never evidence that subprocesses exited.
                    Cef.WaitForBrowsersToClose(10000);
                    Cef.Shutdown();
                    Console.WriteLine("MANAGED_EVENT: CEF_SHUTDOWN_RETURNED");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("MANAGED_FAIL: SHUTDOWN_" + ex);
                    result = 1;
                }
            }
        }
        return result;
    }

    private static bool InitializePatched(CefSettings settings, nint instance, nint sandboxInfo)
    {
        // The public CefSharp facade does not expose this experimental API.
        // Resolve the exact pinned wrapper and the patched method; do not fall
        // back to Cef.Initialize, which supplies a null sandbox context.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var field = typeof(CefSharp.CefSettingsBase).GetField("settings", flags)
            ?? throw new MissingFieldException("CEF_SETTINGS_WRAPPER_UNAVAILABLE");
        object nativeSettings = field.GetValue(settings)
            ?? throw new InvalidOperationException("CEF_SETTINGS_WRAPPER_NULL");
        var type = Type.GetType("CefSharp.Core.Cef, CefSharp.Core.Runtime", throwOnError: true)!;
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(m => m.Name == "InitializeSandboxed" && m.GetParameters().Length == 5)
            ?? throw new MissingMethodException("PATCHED_INITIALIZE_SANDBOXED_UNAVAILABLE");
        try
        {
            return (bool)(method.Invoke(null, new object?[] { nativeSettings, false, null, instance, sandboxInfo })
                ?? throw new InvalidOperationException("CEF_INITIALIZE_RESULT_NULL"));
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw new InvalidOperationException("PATCHED_INITIALIZE_FAILED", ex.InnerException);
        }
    }

    internal static Uri? ControlledHttpsUri()
    {
        string? value = Environment.GetEnvironmentVariable("AEGIS_P0_HTTPS_URL");
        if (string.IsNullOrEmpty(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !IPAddress.TryParse(uri.Host, out var address) || !IPAddress.IsLoopback(address) ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.IsDefaultPort)
            throw new InvalidOperationException("HTTPS_FIXTURE_MUST_BE_LOOPBACK");
        return uri;
    }
}
