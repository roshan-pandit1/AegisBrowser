using System.Text;
using System.Text.Json;

// Harmless in-memory byte fixtures only. No fixture is loaded or executed.
internal static class BuildIntegrityVerifierTests
{
    internal static int Run()
    {
        var checks = 0;
        void Check(bool condition)
        {
            checks++;
            if (!condition) throw new InvalidOperationException("Build integrity regression " + checks);
        }

        var inputs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var id in BuildIntegrityVerifier.RequiredIds) inputs.Add(id, Encoding.UTF8.GetBytes(id));
        var args = new[] { "-noconfig", "-nostdlib+", "-deterministic+" };
        byte[] Manifest(IReadOnlyDictionary<string, byte[]> components, string[] arguments, int version = 1)
        {
            var entries = new List<object>();
            foreach (var entry in components)
                entries.Add(new { id = entry.Key, sha256 = BuildIntegrityVerifier.Hash(entry.Value) });
            return JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema_version = version,
                scope = BuildIntegrityVerifier.Scope,
                build_configuration = BuildIntegrityVerifier.Configuration,
                sdk_version = BuildIntegrityVerifier.SdkVersion,
                runtime_version = BuildIntegrityVerifier.RuntimeVersion,
                arguments,
                components = entries
            });
        }
        bool Verify(byte[] manifest, string pin, IReadOnlyDictionary<string, byte[]> actual,
            IReadOnlyList<string> arguments) =>
            BuildIntegrityVerifier.Verify(manifest, pin, actual, arguments, out _);
        Dictionary<string, byte[]> Copy() => new(inputs, StringComparer.Ordinal);

        var valid = Manifest(inputs, args);
        var approved = BuildIntegrityVerifier.Hash(valid);
        Check(Verify(valid, approved, inputs, args));
        Check(!Verify(Array.Empty<byte>(), approved, inputs, args));
        var malformed = Encoding.UTF8.GetBytes("{");
        Check(!Verify(malformed, BuildIntegrityVerifier.Hash(malformed), inputs, args));
        Check(!Verify(valid, "", inputs, args));
        Check(!Verify(valid, new string('0', 64), inputs, args));

        var changed = Copy();
        changed["output:assembly"] = new byte[] { 1, 2, 3 };
        Check(!Verify(valid, approved, changed, args));
        changed = Copy();
        changed["source:AegisBrowser.Tests/TestGovernance.cs"] = new byte[] { 1 };
        Check(!Verify(valid, approved, changed, args));
        changed = Copy();
        changed.Remove("source:AegisBrowser.Tests/TestGovernance.cs");
        Check(!Verify(valid, approved, changed, args));
        changed = Copy();
        changed["tool:csc.dll"] = new byte[] { 2 };
        Check(!Verify(valid, approved, changed, args));
        changed = Copy();
        changed["ref:System.Runtime.dll"] = new byte[] { 3 };
        Check(!Verify(valid, approved, changed, args));
        changed = Copy();
        changed["input:inventory.json"] = new byte[] { 4 };
        Check(!Verify(valid, approved, changed, args));
        changed = Copy();
        changed["runtime:coreclr.dll"] = new byte[] { 5 };
        Check(!Verify(valid, approved, changed, args));
        changed = Copy();
        changed["runtime:runtimeconfig.json"] = new byte[] { 6 };
        Check(!Verify(valid, approved, changed, args));
        Check(!Verify(valid, approved, inputs, new[] { "-noconfig", "-nostdlib+", "-other" }));
        Check(!Verify(valid, approved, inputs, new[] { "-noconfig" }));

        var changedDeclaration = Copy();
        changedDeclaration["source:AegisBrowser.Tests/InventoryBinding.cs"] = new byte[] { 7 };
        var alteredManifest = Manifest(changedDeclaration, args);
        Check(!Verify(alteredManifest, BuildIntegrityVerifier.Hash(alteredManifest), inputs, args));
        changedDeclaration = Copy();
        changedDeclaration.Remove("output:assembly");
        alteredManifest = Manifest(changedDeclaration, args);
        Check(!Verify(alteredManifest, BuildIntegrityVerifier.Hash(alteredManifest), inputs, args));
        changedDeclaration = Copy();
        changedDeclaration.Add("runtime:unexpected.dll", new byte[] { 8 });
        alteredManifest = Manifest(changedDeclaration, args);
        Check(!Verify(alteredManifest, BuildIntegrityVerifier.Hash(alteredManifest), inputs, args));
        alteredManifest = Manifest(inputs, args, 2);
        Check(!Verify(alteredManifest, BuildIntegrityVerifier.Hash(alteredManifest), inputs, args));
        var duplicate = Encoding.UTF8.GetString(valid).Replace("\"schema_version\":1", "\"schema_version\":1,\"schema_version\":1");
        alteredManifest = Encoding.UTF8.GetBytes(duplicate);
        Check(!Verify(alteredManifest, BuildIntegrityVerifier.Hash(alteredManifest), inputs, args));
        var unexpected = Encoding.UTF8.GetString(valid).Replace("\"schema_version\":1", "\"unknown\":true,\"schema_version\":1");
        alteredManifest = Encoding.UTF8.GetBytes(unexpected);
        Check(!Verify(alteredManifest, BuildIntegrityVerifier.Hash(alteredManifest), inputs, args));
        var assemblyEntry = JsonSerializer.Serialize(new
        {
            id = "output:assembly",
            sha256 = BuildIntegrityVerifier.Hash(inputs["output:assembly"])
        });
        var duplicateEntry = Encoding.UTF8.GetString(valid).Replace(assemblyEntry, assemblyEntry + "," + assemblyEntry);
        Check(duplicateEntry != Encoding.UTF8.GetString(valid));
        alteredManifest = Encoding.UTF8.GetBytes(duplicateEntry);
        Check(!Verify(alteredManifest, BuildIntegrityVerifier.Hash(alteredManifest), inputs, args));
        Check(!Verify(valid, approved, inputs, Array.Empty<string>()));
        return checks;
    }
}
