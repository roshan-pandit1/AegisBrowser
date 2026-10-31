using System.Net;
using System.Net.Sockets;

namespace AegisBrowser.Core;

// These values describe saved intent only. None is a claim of runtime support.
public enum ProxyProtocol { Direct = 0, Http = 1, Https = 2, Socks5 = 3 }

public sealed record ProxyDraft(ProxyProtocol Protocol, string Host, int Port,
    bool AuthenticationRequired, Guid? CredentialId, string Label, bool RequireProxy);

public sealed record ProxyConfiguration(Guid Id, Guid ProfileId, ProxyDraft Settings,
    int Version, long Revision, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);

public interface IProxyRepository
{
    ProxyConfiguration? Get(ProfileDefinition profile, ProfileLease lease);
    ProxyConfiguration Save(ProfileDefinition profile, ProfileLease lease, ProxyDraft draft, long expectedRevision);
}

public enum SavedProxyState { NotInspected, BrowserDefaults, RequiredDraft, NeedsRepair }

public interface IProxyStatusProvider
{
    IReadOnlyDictionary<Guid, SavedProxyState> Inspect(IReadOnlyList<ProfileDefinition> profiles);
}

public static class ProxyValidation
{
    public static void Validate(ProxyDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!Enum.IsDefined(draft.Protocol)) throw new ArgumentException("Unsupported proxy protocol.");
        if (draft.Label is null || draft.Label.Length > 120 || draft.Label != draft.Label.Trim() || draft.Label.Any(char.IsControl))
            throw new ArgumentException("Proxy label must be at most 120 characters without surrounding whitespace or control characters.");
        if (draft.Protocol == ProxyProtocol.Direct)
        {
            if (draft.Host != "" || draft.Port != 0 || draft.AuthenticationRequired || draft.CredentialId is not null || draft.RequireProxy)
                throw new ArgumentException("Direct mode cannot contain a proxy endpoint or credentials.");
            return;
        }
        if (!draft.RequireProxy) throw new ArgumentException("A configured proxy must be required; direct fallback is not supported.");
        if (draft.Port is < 1 or > 65535) throw new ArgumentException("Proxy port must be between 1 and 65535.");
        if ((draft.AuthenticationRequired && (draft.CredentialId is not Guid id || id == Guid.Empty)) ||
            (!draft.AuthenticationRequired && draft.CredentialId is not null))
            throw new ArgumentException("Proxy authentication requires a credential reference, and unauthenticated proxies cannot reference one.");
        ValidateHost(draft.Host);
    }

    private static void ValidateHost(string host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253 || host.Any(c => c > 127 || char.IsWhiteSpace(c) || char.IsControl(c)) ||
            host.IndexOfAny(['/', '\\', '@', '[', ']', '%', '?', '#']) >= 0)
            throw new ArgumentException("Invalid proxy host. Enter a hostname or IP address without a scheme, port or credentials.");
        if (host.Contains(':'))
        {
            if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
                throw new ArgumentException("Invalid IPv6 proxy address.");
            return;
        }
        if (host.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            var parts = host.Split('.');
            if (parts.Length != 4 || parts.Any(p => p.Length == 0 || p.Length > 3 || (p.Length > 1 && p[0] == '0') || !byte.TryParse(p, out _)))
                throw new ArgumentException("Invalid IPv4 proxy address.");
            return;
        }
        var labels = host.Split('.');
        if (labels.Any(label => label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' ||
            label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new ArgumentException("Invalid proxy hostname.");
    }
}
