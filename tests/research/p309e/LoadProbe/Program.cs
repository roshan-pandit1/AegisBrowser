using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

// Reflection-only probe for an explicitly supplied, disposable test build.
// Loading a native library executes loader initialization; only provide files
// obtained and validated in the isolated research environment. CEF methods
// are inspected, never called. This does not validate ABI or browser startup.
if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]))
{
    Console.Error.WriteLine("Usage: LoadProbe <absolute-disposable-build-directory>");
    return 2;
}
if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
{
    Console.Error.WriteLine("LOAD_PROBE_UNSUPPORTED: requires Windows x64 process");
    return 2;
}

try
{
    var directory = Path.GetFullPath(args[0]);
    var files = new[] { "CefSharp.Core.Runtime.dll", "CefSharp.dll", "libcef.dll", "chrome_elf.dll", "Ijwhost.dll" };
    foreach (var name in files)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"LOAD_PROBE_INCOMPLETE: missing {name}");
            return 1;
        }
        using var input = File.OpenRead(path);
        Console.WriteLine($"SHA256 {name} {Convert.ToHexString(SHA256.HashData(input))}");
    }

    // Load these two dependencies by explicit path. Other dependencies are
    // still resolved according to Windows loader rules; the directory must
    // therefore be an operator-controlled, isolated build environment.
    _ = NativeLibrary.Load(Path.Combine(directory, "chrome_elf.dll"));
    _ = NativeLibrary.Load(Path.Combine(directory, "libcef.dll"));
    _ = Assembly.LoadFrom(Path.Combine(directory, "CefSharp.dll"));
    var assembly = Assembly.LoadFrom(Path.Combine(directory, "CefSharp.Core.Runtime.dll"));
    var cef = assembly.GetType("CefSharp.Core.Cef", throwOnError: true)!;
    var methods = cef.GetMethods(BindingFlags.Public | BindingFlags.Static);
    var initialize = methods.Count(method => method.Name == "Initialize");
    var shutdown = methods.Count(method => method.Name == "Shutdown");
    if (initialize == 0 || shutdown == 0)
    {
        Console.Error.WriteLine("LOAD_PROBE_INCOMPLETE: required public methods were not found");
        return 1;
    }
    Console.WriteLine($"LOADED_ASSEMBLY: {assembly.GetName().Name}");
    Console.WriteLine($"PUBLIC_METHODS: Initialize={initialize} Shutdown={shutdown}");
    foreach (var reference in assembly.GetReferencedAssemblies().OrderBy(item => item.Name, StringComparer.Ordinal))
        Console.WriteLine($"ASSEMBLY_REFERENCE: {reference.Name} {reference.Version}");
    Console.WriteLine("PASS: experimental binding loaded for reflection; CEF initialization NOT RUN; ABI NOT VERIFIED");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"LOAD_PROBE_FAILED: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
