using System;
using System.Collections.Generic;
using System.IO;

// Separate read-only verifier; it never loads or starts the target assembly.
// A caller-supplied hash is only an independent trust anchor when a human has
// authenticated and approved it outside the requesting process. Path checks and
// later reads are not atomic and do not defeat a same-user filesystem attacker.
internal static class IntegrityVerificationRunner
{
    private const string Workspace = @"C:\Users\rosha\AegisBrowser\artifacts\worktrees\safe-engine-transition";
    private const string Dotnet = @"C:\Program Files\dotnet\dotnet.exe";
    private const string Compiler = @"C:\Program Files\dotnet\sdk\10.0.404\Roslyn\bincore\csc.dll";
    private const string CoreClr = @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.15\coreclr.dll";
    private const string RefRoot = @"C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\10.0.15\ref\net10.0";

    private static readonly string[] Sources =
    {
        "AegisBrowser.Tests/TestGovernance.cs",
        "AegisBrowser.Tests/TestGovernanceTests.cs",
        "AegisBrowser.Tests/InventoryValidation.cs",
        "AegisBrowser.Tests/InventoryBinding.cs",
        "tests/governance/InventoryValidationTests.cs",
        "tests/governance/PureDecisionRunner.cs",
        "tests/governance/BuildIntegrityVerifier.cs",
        "tests/governance/BuildIntegrityVerifierTests.cs"
    };

    private static readonly string[] References =
    {
        "System.Runtime.dll", "System.Console.dll", "System.Collections.dll", "System.Text.Json.dll",
        "System.Security.Cryptography.dll", "System.Memory.dll", "System.Text.Encoding.Extensions.dll",
        "System.IO.FileSystem.dll", "System.Linq.dll"
    };

    private static int Main(string[] args)
    {
        if (args.Length != 1 || !IsDigest(args[0])) return Refuse();
        try
        {
            var root = Path.GetFullPath(Workspace);
            if (!string.Equals(Path.GetFullPath(Directory.GetCurrentDirectory()), root, StringComparison.OrdinalIgnoreCase) ||
                root.StartsWith(@"\\", StringComparison.Ordinal) ||
                new DriveInfo(Path.GetPathRoot(root)!).DriveType != DriveType.Fixed) return Refuse();

            var buildRoot = Path.Combine(root, "artifacts", "governance-build-integrity");
            var target = Path.Combine(buildRoot, "a", "GovernanceIntegrityTests.dll");
            var runtimeConfig = Path.Combine(buildRoot, "a", "GovernanceIntegrityTests.runtimeconfig.json");
            var manifest = ReadOrdinaryFile(Path.Combine(buildRoot, "build-manifest.v3.candidate.json"), 2 * 1024 * 1024);
            if (!string.Equals(BuildIntegrityVerifier.Hash(manifest), args[0], StringComparison.OrdinalIgnoreCase))
                return Refuse();

            var files = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tool:dotnet.exe"] = Dotnet,
                ["tool:csc.dll"] = Compiler,
                ["runtime:coreclr.dll"] = CoreClr,
                ["runtime:runtimeconfig.json"] = runtimeConfig,
                ["input:inventory.json"] = Path.Combine(root, "tests", "governance", "test-safety-inventory.json"),
                ["output:assembly"] = target
            };
            var compilerArguments = new List<string>
            {
                "-noconfig", "-nostdlib+", "-target:exe", "-debug-", "-nullable:enable", "-deterministic+",
                "-out:" + target
            };
            foreach (var name in References)
            {
                var path = Path.Combine(RefRoot, name);
                files.Add("ref:" + name, path);
                compilerArguments.Add("-r:" + path);
            }
            foreach (var name in Sources)
            {
                var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
                files.Add("source:" + name, path);
                compilerArguments.Add(path);
            }

            var observed = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in files) observed.Add(entry.Key, ReadOrdinaryFile(entry.Value, 64 * 1024 * 1024));
            if (!BuildIntegrityVerifier.Verify(manifest, args[0], observed, compilerArguments, out _)) return Refuse();
            Console.WriteLine("BUILD_INTEGRITY=MATCH_ONLY");
            return 0;
        }
        catch (IOException) { return Refuse(); }
        catch (UnauthorizedAccessException) { return Refuse(); }
        catch (ArgumentException) { return Refuse(); }
        catch (NotSupportedException) { return Refuse(); }
    }

    private static byte[] ReadOrdinaryFile(string path, long maxLength)
    {
        var file = new FileInfo(path);
        var current = file.Directory;
        while (current is not null)
        {
            var attributes = File.GetAttributes(current.FullName);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Unsafe ancestor");
            current = current.Parent;
        }
        var fileAttributes = File.GetAttributes(file.FullName);
        if ((fileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            file.Length <= 0 || file.Length > maxLength) throw new IOException("Unsafe file");
        return File.ReadAllBytes(file.FullName);
    }

    private static bool IsDigest(string value)
    {
        if (value.Length != 64) return false;
        foreach (var c in value)
            if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }

    private static int Refuse()
    {
        Console.Error.WriteLine("BUILD_INTEGRITY=REFUSED");
        return 2;
    }
}
