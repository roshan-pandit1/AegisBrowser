using System.Net;
using System.Net.Sockets;

namespace AegisBrowser.Core;

/// <summary>Decides only whether a Basic challenge may use a profile's proxy credential.</summary>
public static class ProxyAuthenticationScope
{
    public static bool Allows(ProxyDraft settings, bool isProxy, string? host, int port, string? scheme)
    {
        if (!isProxy || settings is null || host is null || !string.Equals(scheme, "basic", StringComparison.OrdinalIgnoreCase) ||
            settings.Protocol is not (ProxyProtocol.Http or ProxyProtocol.Https) || !settings.AuthenticationRequired || port != settings.Port)
            return false;
        try { ProxyValidation.Validate(settings); }
        catch (ArgumentException) { return false; }
        var bracketed = host.Length > 2 && host[0] == '[' && host[^1] == ']';
        var requested = bracketed ? host[1..^1] : host;
        if (IPAddress.TryParse(settings.Host, out var configuredIp))
        {
            if (bracketed && configuredIp.AddressFamily != AddressFamily.InterNetworkV6) return false;
            if (!IPAddress.TryParse(requested, out var requestedIp) || configuredIp.AddressFamily != requestedIp.AddressFamily) return false;
            // Reject legacy abbreviated or octal-looking IPv4 spellings.
            if (requestedIp.AddressFamily == AddressFamily.InterNetwork && requested != requestedIp.ToString()) return false;
            return configuredIp.Equals(requestedIp);
        }
        if (bracketed) return false;
        return string.Equals(settings.Host, requested, StringComparison.OrdinalIgnoreCase);
    }
}
