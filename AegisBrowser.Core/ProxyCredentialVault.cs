using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AegisBrowser.Core;

// Do not use a record: compiler-generated ToString must never expose credentials.
public sealed class ProxyCredentials
{
    public ProxyCredentials(string username, string password)
    {
        if (string.IsNullOrEmpty(username) || username.Length > 512 || username.Any(char.IsControl) ||
            string.IsNullOrEmpty(password) || password.Length > 4096 || password.Contains('\0'))
            throw new ArgumentException("Invalid proxy credentials.");
        Username = username; Password = password;
    }
    public string Username { get; }
    public string Password { get; }
    public override string ToString() => "[proxy credentials redacted]";
}

public interface IProxyCredentialVault
{
    Guid Store(Guid profileId, ProxyCredentials credentials);
    ProxyCredentials Read(Guid profileId, Guid credentialId);
    void Delete(Guid profileId, Guid credentialId);
}

/// <summary>DPAPI CurrentUser ciphertext, bound to a profile and an opaque credential ID.</summary>
public sealed class ProxyCredentialVault : IProxyCredentialVault
{
    public const string DirectoryName = ".proxy-credentials";
    private const int MaxProtectedBytes = 65536;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string root;

    public ProxyCredentialVault(string root) => this.root = Path.GetFullPath(root);

    public Guid Store(Guid profileId, ProxyCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows user-scoped data protection is required.");
        ValidateId(profileId);
        var id = Guid.NewGuid();
        var directory = CredentialDirectory(profileId, create: true);
        var destination = Path.Combine(directory, id.ToString("N") + ".bin");
        var temporary = Path.Combine(directory, ".credential-" + Guid.NewGuid().ToString("N") + ".tmp");
        var owned = false;
        byte[]? plain = null;
        try
        {
            var userLength = Utf8.GetByteCount(credentials.Username); var passLength = Utf8.GetByteCount(credentials.Password);
            plain = new byte[checked(8 + userLength + passLength)];
            BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(0, 4), userLength);
            BinaryPrimitives.WriteInt32LittleEndian(plain.AsSpan(4, 4), passLength);
            Utf8.GetBytes(credentials.Username, plain.AsSpan(8, userLength));
            Utf8.GetBytes(credentials.Password, plain.AsSpan(8 + userLength, passLength));
            var protectedBytes = ProtectedData.Protect(plain, Entropy(profileId, id), DataProtectionScope.CurrentUser);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { owned = true; file.Write(protectedBytes); file.Flush(true); }
            File.Move(temporary, destination); // Never replace another credential.
            return id;
        }
        catch (EncoderFallbackException) { throw new ArgumentException("Invalid proxy credentials."); }
        catch (CryptographicException) { throw new IOException("Windows could not protect proxy credentials for this user."); }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            if (owned && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public ProxyCredentials Read(Guid profileId, Guid credentialId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows user-scoped data protection is required.");
        ValidateId(profileId); ValidateId(credentialId);
        var directory = CredentialDirectory(profileId, create: false);
        var path = Path.Combine(directory, credentialId.ToString("N") + ".bin");
        ProxyRepository.RejectLink(path);
        byte[]? plain = null;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 1 or > MaxProtectedBytes) throw new IOException("Protected proxy credentials are invalid.");
            var encrypted = new byte[(int)file.Length]; file.ReadExactly(encrypted);
            plain = ProtectedData.Unprotect(encrypted, Entropy(profileId, credentialId), DataProtectionScope.CurrentUser);
            if (plain.Length < 8) throw new IOException("Protected proxy credentials are invalid.");
            var userLength = BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(0, 4));
            var passLength = BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(4, 4));
            if (userLength < 1 || passLength < 1 || userLength > plain.Length - 8 || passLength != plain.Length - 8 - userLength)
                throw new IOException("Protected proxy credentials are invalid.");
            return new ProxyCredentials(Utf8.GetString(plain, 8, userLength), Utf8.GetString(plain, 8 + userLength, passLength));
        }
        catch (Exception ex) when (ex is CryptographicException or DecoderFallbackException or ArgumentException or FileNotFoundException or DirectoryNotFoundException)
        { throw new IOException("Proxy credentials are missing, inaccessible to this Windows user, or invalid."); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }

    public void Delete(Guid profileId, Guid credentialId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows user-scoped data protection is required.");
        ValidateId(profileId); ValidateId(credentialId);
        var directory = CredentialDirectory(profileId, create: false);
        var path = Path.Combine(directory, credentialId.ToString("N") + ".bin");
        ProxyRepository.RejectLink(path);
        File.Delete(path); // Never deletes a directory or any profile/browser data.
    }

    private string CredentialDirectory(Guid profileId, bool create)
    {
        ProxyRepository.RejectLink(root);
        var parent = Path.Combine(root, DirectoryName); ProxyRepository.RejectLink(parent);
        if (create) { Directory.CreateDirectory(parent); ProxyRepository.RejectLink(parent); }
        var directory = Path.Combine(parent, profileId.ToString("N")); ProxyRepository.RejectLink(directory);
        if (create) { Directory.CreateDirectory(directory); ProxyRepository.RejectLink(directory); }
        return directory;
    }

    private static byte[] Entropy(Guid profileId, Guid credentialId) =>
        SHA256.HashData(Encoding.ASCII.GetBytes("AegisBrowser.ProxyCredential.v1/" + profileId.ToString("N") + "/" + credentialId.ToString("N")));

    private static void ValidateId(Guid id) { if (id == Guid.Empty) throw new ArgumentException("A nonempty identifier is required."); }
}
