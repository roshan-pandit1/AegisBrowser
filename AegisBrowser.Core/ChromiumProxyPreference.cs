using System.Globalization;

namespace AegisBrowser.Core;

/// <summary>
/// Constructs a fixed-server Chromium preference for controlled integration.
/// It does not apply the preference, configure authentication, or establish
/// network containment. Required-proxy profile startup remains blocked.
/// </summary>
public static class ChromiumProxyPreference
{
    public static Dictionary<string, object> Create(ProxyDraft draft)
    {
        ProxyValidation.Validate(draft);
        var scheme = draft.Protocol switch
        {
            ProxyProtocol.Http => "http",
            ProxyProtocol.Socks5 when !draft.AuthenticationRequired => "socks5",
            ProxyProtocol.Socks5 => throw new NotSupportedException("Chromium SOCKS5 authentication has not been established."),
            _ => throw new NotSupportedException("This Chromium proxy protocol has not been established.")
        };
        // Validation accepts unbracketed IPv6 literals only. Chromium's URI
        // syntax requires brackets around literals to separate the port.
        var host = draft.Host.Contains(':') ? "[" + draft.Host + "]" : draft.Host;
        return new Dictionary<string, object>
        {
            ["mode"] = "fixed_servers",
            ["server"] = scheme + "://" + host + ":" + draft.Port.ToString(CultureInfo.InvariantCulture)
        };
    }
}
