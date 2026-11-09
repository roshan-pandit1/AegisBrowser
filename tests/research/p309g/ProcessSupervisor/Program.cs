using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Runtime.InteropServices;
using P309g;

internal static class Program
{
    private const string RuntimeVersion = "154.0.34+g14c5a08+chromium-154.0.8037.98";
    private static async Task<int> Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--fixture-helper") return await Helper(args[1], args.Skip(2).ToArray());
        if (args.Length == 1 && args[0] == "--self-test") return await SelfTest.Run();
        if (args.Length == 1 && args[0] == "--fixture-wire-selftest") return await SelfTest.WireFixture();
        if (args.Length != 5 || args[0] != "supervise" || !int.TryParse(args[4], out int timeout))
        {
            Console.Error.WriteLine("Usage: ProcessSupervisor supervise <absolute-bootstrapc.exe> <external-manifest-path> <manifest-sha256> <timeout-ms>");
            return 2;
        }
        try
        {
            var bootstrap = Path.GetFullPath(args[1]);
            if (!Path.IsPathFullyQualified(args[1]) || !OperatingSystem.IsWindows() ||
                !string.Equals(Path.GetFileName(bootstrap), "bootstrapc.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("BOOTSTRAP_PATH_INVALID");
            var dir = Path.GetDirectoryName(bootstrap)!;
            if (new DriveInfo(Path.GetPathRoot(dir)!).DriveType == DriveType.Network) throw new InvalidDataException("NETWORK_PATH_REJECTED");
            // The independently approved manifest hash is the trust root;
            // a manifest copied from the same untrusted bundle is not one.
            using var bundle = BundlePins.Open(dir, args[2], args[3]);
            if (!string.Equals(FileVersionInfo.GetVersionInfo(Path.Combine(dir, "libcef.dll")).FileVersion,
                RuntimeVersion, StringComparison.Ordinal)) throw new InvalidDataException("CEF_VERSION_MISMATCH");
            var result = await ProcessRunner.RunAsync(bootstrap, bundle.BootstrapHash, new[] { "--module=p309f_client" }, dir, timeout, true,
                (id, _) => Console.WriteLine(JsonSerializer.Serialize(new { kind = "PROCESS_IDENTITY", identity = id })));
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "PROCESS_RESULT", result, DependencyCoverage = "PINNED_BUNDLE_FILES_NOT_SYSTEM_DEPENDENCIES", OverallQualification = "INCONCLUSIVE" }));
            return result.ProcessExit == "NORMAL" && result.OutputComplete && !result.Stdout.Truncated && !result.Stderr.Truncated ? 0 : 1;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or FormatException or JsonException)
        {
            Console.Error.WriteLine("SUPERVISOR_PRECHECK_FAILED: " + ex.Message);
            return 2;
        }
    }

    // Helpers are synthetic short-lived child processes; they never start CEF.
    private static async Task<int> Helper(string mode, string[] args)
    {
        switch (mode)
        {
            case "zero": Console.Write("zero"); return 0;
            case "nonzero": Console.Error.Write("nonzero"); return 7;
            case "delay": await Task.Delay(250); Console.Write("late"); return 0;
            case "timeout": await Task.Delay(2000); return 0;
            case "burst":
                var a = Console.Out.WriteAsync(new string('o', 200000));
                var b = Console.Error.WriteAsync(new string('e', 200000));
                await Task.WhenAll(a, b); return 0;
            case "echo": Console.Write(args.Length == 1 ? args[0] : "wrong"); return 0;
            case "bad-utf8": await Console.OpenStandardOutput().WriteAsync(new byte[] { 0xff, 0xfe }); return 0;
            case "wire":
                // Explicitly synthetic: lets the offline classifier consume
                // the real supervisor wire format without launching CEF.
                await Task.Delay(200);
                foreach (string line in new[] {
                    "EVENT: CONTEXT_INITIALIZED", "EVENT: CEF_INITIALIZE_SUCCEEDED",
                    "SANDBOX_CONFIGURATION: REQUESTED; ENFORCEMENT: INCONCLUSIVE",
                    "EVENT: BROWSER_CREATED", "EVENT: MAIN_FRAME_LOAD_STARTED", "EVENT: MAIN_FRAME_LOAD_END",
                    "EVENT: CLOSE_REQUESTED", "EVENT: BEFORE_CLOSE",
                    "EVENT: CEF_SHUTDOWN_RETURNED; SUBPROCESS_CLEANUP: INCONCLUSIVE", "OWNER_RELEASE_CONTRACT: UNSATISFIED" })
                    Console.WriteLine(line);
                return 0;
            case "spawn":
                var psi = SelfTest.HelperStartInfo("delay");
                using (var child = Process.Start(psi) ?? throw new InvalidOperationException("child start"))
                    Console.Write("child-started");
                return 0;
            default: return 9;
        }
    }
}

internal static class SelfTest
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);
    private static int checks;
    internal static ProcessStartInfo HelperStartInfo(string mode, params string[] extra)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("missing process path");
        var p = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            p.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        p.ArgumentList.Add("--fixture-helper"); p.ArgumentList.Add(mode);
        foreach (var arg in extra) p.ArgumentList.Add(arg);
        return p;
    }
    internal static async Task<int> WireFixture()
    {
        var helper = HelperStartInfo("wire");
        string hash = Hash(helper.FileName);
        var result = await ProcessRunner.RunAsync(helper.FileName, hash, helper.ArgumentList.ToArray(), AppContext.BaseDirectory,
            5000, true, (id, _) => Console.WriteLine(JsonSerializer.Serialize(new { kind = "PROCESS_IDENTITY", identity = id })));
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "PROCESS_RESULT", result,
            DependencyCoverage = "SYNTHETIC_FIXTURE_ONLY", OverallQualification = "INCONCLUSIVE" }));
        return result.ProcessExit == "NORMAL" && result.OutputComplete ? 0 : 1;
    }
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException("FAIL: " + name);
        checks++; Console.WriteLine("PASS: " + name);
    }
    private static async Task<ProcessRunner.Result> Run(string mode, int timeout = 5000, bool kill = true, string? hashOverride = null, params string[] extra)
    {
        var psi = HelperStartInfo(mode, extra);
        string hash = hashOverride ?? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(psi.FileName)));
        return await ProcessRunner.RunAsync(psi.FileName, hash, psi.ArgumentList.ToArray(), AppContext.BaseDirectory, timeout, kill);
    }
    private static bool Reject(Action action)
    {
        try { action(); return false; }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException) { return true; }
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static async Task CheckMalformedManifestCli()
    {
        var root = Path.Combine(Path.GetTempPath(), "aegis-p309g-malformed-" + Guid.NewGuid().ToString("N"));
        var bundle = Path.Combine(root, "bundle");
        Directory.CreateDirectory(bundle);
        var manifest = Path.Combine(root, "manifest.json");
        File.WriteAllText(manifest, "{\"schema\":", new UTF8Encoding(false));
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("missing process path");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string argument in new[] { "supervise", Path.Combine(bundle, "bootstrapc.exe"), manifest, Hash(manifest), "1000" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start owned test process.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(cancel.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw new InvalidOperationException("Malformed-manifest CLI test timed out.");
        }
        await Task.WhenAll(stdout, stderr);
        Check(process.ExitCode == 2 && stderr.Result.Contains("SUPERVISOR_PRECHECK_FAILED:", StringComparison.Ordinal),
            "malformed manifest returns an explicit precheck failure");
    }
    private static void TestBundlePins()
    {
        var root = Path.Combine(Path.GetTempPath(), "aegis-p309g-manifest-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "bundle");
        Directory.CreateDirectory(Path.Combine(dir, "locales"));
        var names = new[] { "bootstrapc.exe", "p309f_client.dll", "libcef.dll", "chrome_elf.dll", "locales/en-US.pak" };
        foreach (string name in names) File.WriteAllText(Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar)), "synthetic " + name);
        var files = names.Select(name => new { name, sha256 = Hash(Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar))) }).ToArray();
        string path = Path.Combine(root, "manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { schema = "p309g-bundle-v1", files }), new UTF8Encoding(false));
        string hash = Hash(path);
        using (var validated = BundlePins.Open(dir, path, hash))
        {
            Check(validated.BootstrapHash == files[0].sha256, "independently pinned synthetic bundle accepted");
            Check(Reject(() => { using var change = new FileStream(Path.Combine(dir, "bootstrapc.exe"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }), "verified bundle file is held against writes");
        }
        Check(Reject(() => BundlePins.Open(dir, path, new string('0', 64)).Dispose()), "wrong manifest trust root rejected");
        string hardLink = Path.Combine(root, "synthetic-hardlink.exe");
        if (!CreateHardLink(hardLink, Path.Combine(dir, "bootstrapc.exe"), IntPtr.Zero))
            throw new IOException("Synthetic hard-link test could not execute: " + Marshal.GetLastWin32Error());
        Check(Reject(() => BundlePins.Open(dir, path, hash).Dispose()), "multiply linked bundle file rejected");
        File.Delete(hardLink); // exact synthetic hard link created above
        var unknown = Path.Combine(dir, "unlisted.dll");
        File.WriteAllText(unknown, "synthetic unlisted");
        Check(Reject(() => BundlePins.Open(dir, path, hash).Dispose()), "unlisted bundle module rejected");
        File.Delete(unknown); // exact synthetic file created above, never recursive
        File.WriteAllText(Path.Combine(dir, "libcef.dll"), "modified");
        Check(Reject(() => BundlePins.Open(dir, path, hash).Dispose()), "modified bundle file rejected");
        File.WriteAllText(Path.Combine(dir, "libcef.dll"), "synthetic libcef.dll");
        var duplicate = Path.Combine(root, "duplicate.json");
        File.WriteAllText(duplicate, "{\"schema\":\"p309g-bundle-v1\",\"schema\":\"p309g-bundle-v1\",\"files\":[]}");
        Check(Reject(() => BundlePins.Open(dir, duplicate, Hash(duplicate)).Dispose()), "duplicate manifest field rejected");
        var traversal = Path.Combine(root, "traversal.json");
        File.WriteAllText(traversal, "{\"schema\":\"p309g-bundle-v1\",\"files\":[{\"name\":\"../escape.dll\",\"sha256\":\"" + new string('0', 64) + "\"}]}");
        Check(Reject(() => BundlePins.Open(dir, traversal, Hash(traversal)).Dispose()), "manifest traversal rejected");
        Check(Reject(() => BundlePins.Open(dir, Path.Combine(dir, "bootstrapc.exe"), files[0].sha256).Dispose()), "manifest inside bundle rejected");
        Console.WriteLine("SYNTHETIC_BUNDLE_TEST_DIRECTORY=" + root);
    }
    internal static async Task<int> Run()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("SELF_TEST: NOT_RUN (Windows required)"); return 2; }
            var zero = await Run("zero");
            Check(zero.ProcessExit == "NORMAL" && zero.NativeExitCode == 0, "native zero exit observed");
            Check(zero.ProcessIdentity is { Pid: > 0, CreationFileTimeUtc: > 0 }, "handle-bound process identity observed");
            Check(zero.OutputComplete && zero.Stdout.Text == "zero", "stdout drained before completion");
            Check(zero.SubprocessCleanup == "INCONCLUSIVE" && zero.SandboxEnforcement == "INCONCLUSIVE", "parent exit does not certify descendants or sandbox");
            var nonzero = await Run("nonzero");
            Check(nonzero.ProcessExit == "NONZERO" && nonzero.NativeExitCode == 7, "nonzero native exit retained");
            Check(nonzero.Stderr.Text == "nonzero", "stderr independently captured");
            var delayed = await Run("delay");
            Check(delayed.ProcessExit == "NORMAL" && delayed.Stdout.Text == "late", "delayed child completes normally");
            var burst = await Run("burst");
            Check(burst.ProcessExit == "NORMAL" && !burst.OutputComplete, "large concurrent output drains but is not complete evidence");
            Check(burst.Stdout.Truncated && burst.Stderr.Truncated && burst.Stdout.Text.Length <= 65536 && burst.Stderr.Text.Length <= 65536, "both streams are bounded and truncation reported");
            var timeout = await Run("timeout", 100);
            Check(timeout.ProcessExit == "TIMEOUT" && timeout.NativeExitCode == null, "timeout never reports normal exit");
            Check(timeout.ParentTerminationRequested && timeout.ParentCleanup == "PARENT_TERMINATED", "timeout cleanup targets retained parent handle only");
            Check(timeout.SubprocessCleanup == "INCONCLUSIVE", "parent termination cannot prove child cleanup");
            var wrong = await Run("zero", hashOverride: new string('0', 64));
            Check(wrong.ProcessExit == "UNAVAILABLE" && wrong.ProcessIdentity == null, "incorrect executable pin rejected before launch");
            var invalid = await ProcessRunner.RunAsync("relative.exe", new string('0', 64), Array.Empty<string>(), AppContext.BaseDirectory, 100, true);
            Check(invalid.ProcessExit == "UNAVAILABLE" && invalid.ProcessIdentity == null, "relative executable path rejected");
            var echo = await Run("echo", extra: new[] { "value & $() with spaces" });
            Check(echo.ProcessExit == "NORMAL" && echo.Stdout.Text == "value & $() with spaces", "argument list does not invoke a shell");
            var malformed = await Run("bad-utf8");
            Check(malformed.ProcessExit == "NORMAL" && !malformed.OutputComplete && !malformed.Stdout.ValidUtf8, "malformed UTF-8 cannot become complete diagnostic output");
            var callbackSpec = HelperStartInfo("delay");
            string callbackHash = Hash(callbackSpec.FileName);
            int callbacks = 0;
            var observed = await ProcessRunner.RunAsync(callbackSpec.FileName, callbackHash, callbackSpec.ArgumentList.ToArray(),
                AppContext.BaseDirectory, 5000, true, (id, handle) => { if (!handle.IsInvalid && id.CreationFileTimeUtc > 0) callbacks++; });
            Check(observed.ProcessExit == "NORMAL" && callbacks == 1, "identity callback receives retained handle exactly once");
            var failedDelivery = await ProcessRunner.RunAsync(callbackSpec.FileName, callbackHash, callbackSpec.ArgumentList.ToArray(),
                AppContext.BaseDirectory, 5000, true, (_, _) => throw new InvalidOperationException("synthetic callback failure"));
            Check(failedDelivery.ProcessExit == "UNAVAILABLE" && failedDelivery.Error.StartsWith("IDENTITY_DELIVERY_FAILED"), "failed identity delivery cannot report normal success");
            var child = await Run("spawn");
            Check(child.ProcessExit == "NORMAL" && child.SubprocessCleanup == "INCONCLUSIVE", "parent exit does not establish spawned child exit");
            Check(!string.IsNullOrWhiteSpace(child.ProcessIdentity?.ImageSha256), "identity binds executable hash");
            var badTime = await Run("zero", timeout: 0);
            Check(badTime.ProcessExit == "UNAVAILABLE" && badTime.NativeExitCode == null, "invalid timeout rejected");
            TestBundlePins();
            await CheckMalformedManifestCli();
            Console.WriteLine($"PASS: {checks} process-supervisor checks (synthetic; no CEF or sandbox verification)");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
