using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using AegisBrowser.Core;

// An explicitly selected diagnostic: it uses a new fixture directory and emits
// only a DPAPI error code and file count, never credential values or raw errors.
internal static class DpapiDiagnostic
{
    public static int Run(string path)
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("DPAPI_PROBE=NOT_RUN platform"); return 2; }
        var privateText = Guid.NewGuid().ToString("N");
        var sanitized = ProxyCredentialVault.ProtectionFailure(
            new CryptographicException(privateText, new InvalidOperationException(privateText)));
        if (sanitized.InnerException is not null ||
            sanitized.Message.Contains(privateText, StringComparison.Ordinal) ||
            !Regex.IsMatch(sanitized.Message, @"DPAPI HRESULTs 0x[0-9A-F]{8}"))
        {
            Console.WriteLine("DPAPI_PROBE=FAIL unsafe_diagnostic");
            return 5;
        }
        var root = Path.GetFullPath(path);
        if (Directory.Exists(root) || File.Exists(root))
        {
            Console.WriteLine("DPAPI_PROBE=NOT_RUN fixture_exists");
            return 2;
        }
        Directory.CreateDirectory(root);
        var profile = Guid.NewGuid();
        var username = Guid.NewGuid().ToString("N");
        var password = Guid.NewGuid().ToString("N");
        var vault = new ProxyCredentialVault(root);
        var win32 = Win32DpapiProbe.Run();
        Console.WriteLine(win32.Success ? "DPAPI_WIN32=PASS" : $"DPAPI_WIN32=FAIL error={win32.Error}");
        try
        {
            var id = vault.Store(profile, new ProxyCredentials(username, password));
            var read = vault.Read(profile, id);
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            if (read.Username != username || read.Password != password || files.Length != 1)
                throw new InvalidOperationException("DPAPI probe did not round-trip.");
            var bytes = File.ReadAllBytes(files[0]);
            if (bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(username)) >= 0 ||
                bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(password)) >= 0)
                throw new InvalidOperationException("DPAPI probe found fixture plaintext.");
            Console.WriteLine("DPAPI_PROBE=PASS encrypted_file_count=1 roundtrip=PASS");
            return 0;
        }
        catch (IOException ex)
        {
            var match = Regex.Match(ex.Message, @"\(DPAPI HRESULTs (0x[0-9A-F]{8}(?:,0x[0-9A-F]{8}){0,3})\); no credential was saved\. Check that the Windows user profile is loaded and accessible\.$");
            if (!match.Success) { Console.WriteLine("DPAPI_PROBE=INCONCLUSIVE unexpected_io"); return 4; }
            var count = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length;
            var directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Length;
            Console.WriteLine($"DPAPI_PROBE=FAIL hresults={match.Groups[1].Value} file_count={count} directory_count={directories}");
            return count == 0 && directories == 0 ? 3 : 5;
        }
    }
}
