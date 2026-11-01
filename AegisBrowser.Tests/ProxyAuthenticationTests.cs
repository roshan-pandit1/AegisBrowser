using AegisBrowser.Core;

internal static class ProxyAuthenticationTests
{
    public static void Run()
    {
        var count = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL proxy authentication scope: " + name); count++; }
        var settings = new ProxyDraft(ProxyProtocol.Http, "proxy.example", 8080, true, Guid.NewGuid(), "", true);
        Check(ProxyAuthenticationScope.Allows(settings, true, "PROXY.EXAMPLE", 8080, "basic"), "configured proxy may receive Basic credentials");
        Check(!ProxyAuthenticationScope.Allows(settings, false, "proxy.example", 8080, "basic"), "origin challenge denied even for matching host");
        Check(!ProxyAuthenticationScope.Allows(settings, true, "other.example", 8080, "basic"), "different host denied");
        Check(!ProxyAuthenticationScope.Allows(settings, true, "proxy.example", 8081, "basic"), "different port denied");
        Check(!ProxyAuthenticationScope.Allows(settings, true, "proxy.example", 8080, "digest"), "unverified authentication scheme denied");
        Check(!ProxyAuthenticationScope.Allows(settings with { Protocol = ProxyProtocol.Socks5 }, true, "proxy.example", 8080, "basic"), "SOCKS5 authentication denied");
        Check(!ProxyAuthenticationScope.Allows(settings with { CredentialId = null }, true, "proxy.example", 8080, "basic"), "missing credential denied");
        Check(!ProxyAuthenticationScope.Allows(settings, true, null, 8080, "basic"), "missing host denied");
        var ipv6 = settings with { Host = "2001:db8::1" };
        Check(ProxyAuthenticationScope.Allows(ipv6, true, "[2001:0db8:0:0:0:0:0:1]", 8080, "BASIC"), "equivalent IPv6 address accepted");
        var ipv4 = settings with { Host = "127.0.0.1" };
        Check(!ProxyAuthenticationScope.Allows(ipv4, true, "127.1", 8080, "basic"), "ambiguous IPv4 address denied");
        Console.WriteLine($"PASS: {count} proxy authentication scope checks");
    }
}
