using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// Metadata only: does not load the input or execute its initialization code.
// A matching assembly-reference list is not proof of ABI or runtime safety.
if (args.Length != 2 || !Path.IsPathFullyQualified(args[0]) ||
    !int.TryParse(args[1], out var expectedMajor) || expectedMajor < 1 || expectedMajor > 99)
{
    Console.Error.WriteLine("Usage: FrameworkMetadata <absolute-assembly-path> <maximum-framework-major>");
    return 2;
}

try
{
    using var stream = File.OpenRead(args[0]);
    if (stream.Length == 0 || stream.Length > 256L * 1024 * 1024)
        throw new InvalidDataException("Invalid PE file size.");
    using var pe = new PEReader(stream);
    if (!pe.HasMetadata) throw new InvalidDataException("PE has no managed metadata.");
    var reader = pe.GetMetadataReader();
    if (!reader.IsAssembly) throw new InvalidDataException("PE is not an assembly.");
    var definition = reader.GetAssemblyDefinition();
    var name = reader.GetString(definition.Name);
    Console.WriteLine($"ASSEMBLY={name}");
    var targetFramework = "UNAVAILABLE";
    foreach (var attributeHandle in definition.GetCustomAttributes())
    {
        var attribute = reader.GetCustomAttribute(attributeHandle);
        if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
        var constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
        var type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
        if (reader.GetString(type.Namespace) != "System.Runtime.Versioning" ||
            reader.GetString(type.Name) != "TargetFrameworkAttribute") continue;
        var value = reader.GetBlobReader(attribute.Value);
        if (value.ReadUInt16() != 1) throw new InvalidDataException("Invalid target-framework attribute.");
        targetFramework = value.ReadSerializedString() ?? "UNAVAILABLE";
    }
    Console.WriteLine($"TARGET_FRAMEWORK={targetFramework}");
    var bad = new List<string>();
    var runtime = false;
    foreach (var handle in reader.AssemblyReferences)
    {
        var reference = reader.GetAssemblyReference(handle);
        var referenceName = reader.GetString(reference.Name);
        var version = reference.Version;
        if (referenceName == "System.Runtime") runtime = true;
        Console.WriteLine($"REFERENCE={referenceName}, Version={version}");
        // This deliberately reports framework-name references with a greater
        // major version; it is not a complete dependency compatibility check.
        // Microsoft.VisualBasic assembly versions (10/11) do not track .NET's
        // major version and must not be classified by this heuristic.
        if ((referenceName == "System" || referenceName.StartsWith("System.", StringComparison.Ordinal) ||
             referenceName == "Microsoft.CSharp" || referenceName.StartsWith("Microsoft.Win32.", StringComparison.Ordinal)) && version.Major > expectedMajor)
            bad.Add($"{referenceName} {version}");
    }
    if (!runtime) throw new InvalidDataException("System.Runtime assembly reference is absent.");
    foreach (var reference in bad) Console.Error.WriteLine($"FRAMEWORK_REFERENCE_TOO_NEW={reference}");
    if (bad.Count != 0)
    {
        Console.WriteLine("FRAMEWORK_REFERENCE_CHECK: REJECTED");
        return 1;
    }
    Console.WriteLine("FRAMEWORK_REFERENCE_CHECK: PASS (metadata only; runtime and ABI not verified)");
    return 0;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidDataException or ArgumentException)
{
    Console.Error.WriteLine($"FRAMEWORK_METADATA_ERROR: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
