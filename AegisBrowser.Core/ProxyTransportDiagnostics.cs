using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AegisBrowser.Core;

public enum ProxyTransportOutcome
{
    Connected, AuthenticationFailed, MissingCredentials, NameResolutionFailed, ConnectionFailed,
    TimedOut, TlsFailed, EndpointOrProxyFailed, Unsupported, Inconclusive, Canceled
}

/// <summary>A .NET transport observation, never a claim about the active Chromium route.</summary>
public sealed record ProxyTransportResult(ProxyProtocol Protocol, ProxyTransportOutcome Outcome, DateTimeOffset TestedUtc,
    TimeSpan Elapsed, string? ReportedIp, string Detail);

public interface IProxyTransportTester
{
    Task<ProxyTransportResult> TestAsync(ProxyDraft draft, ProxyCredentials? credentials, string endpoint, CancellationToken cancellation);
}

/// <summary>Opt-in preflight using a fixed proxy, standard TLS and no browser state.</summary>
public sealed class ProxyTransportTester : IProxyTransportTester
{
    private const int MaxResponseBytes = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly TimeSpan timeout;

    public ProxyTransportTester(TimeSpan? timeout = null)
    {
        this.timeout = timeout ?? TimeSpan.FromSeconds(15);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Diagnostic timeout must be positive and at most 60 seconds.");
    }

    public async Task<ProxyTransportResult> TestAsync(ProxyDraft draft, ProxyCredentials? credentials, string endpoint, CancellationToken cancellation)
    {
        ProxyValidation.Validate(draft);
        var destination = ValidateEndpoint(endpoint);
        var timer = Stopwatch.StartNew();
        ProxyTransportResult Finish(ProxyTransportOutcome outcome, string detail, string? ip = null) =>
            new(draft.Protocol, outcome, DateTimeOffset.UtcNow, timer.Elapsed, ip, detail);
        if (draft.Protocol == ProxyProtocol.Direct)
            return Finish(ProxyTransportOutcome.Unsupported, "Browser-default networking is not tested by this proxy preflight.");
        if (draft.Protocol == ProxyProtocol.Socks5 && draft.AuthenticationRequired)
            return Finish(ProxyTransportOutcome.Unsupported, "SOCKS5 authentication is not supported by the documented Chromium implementation.");
        if (draft.AuthenticationRequired && credentials is null)
            return Finish(ProxyTransportOutcome.MissingCredentials, "The saved proxy credentials are unavailable.");
        if (!draft.AuthenticationRequired && credentials is not null) throw new ArgumentException("Credentials were supplied for an unauthenticated proxy.");
        var proxyUri = new UriBuilder(draft.Protocol switch
        {
            ProxyProtocol.Http => "http", ProxyProtocol.Https => "https", ProxyProtocol.Socks5 => "socks5",
            _ => throw new ArgumentException("Unsupported proxy protocol.")
        }, draft.Host, draft.Port).Uri;
        if (string.Equals(proxyUri.Host, destination.Host, StringComparison.OrdinalIgnoreCase) && proxyUri.Port == destination.Port)
            throw new ArgumentException("The diagnostic endpoint must differ from the proxy endpoint.");
        var proxy = new FixedProxy(proxyUri, credentials is null ? null : new NetworkCredential(credentials.Username, credentials.Password));
        using var handler = new HttpClientHandler
        {
            UseProxy = true, Proxy = proxy, UseCookies = false, AllowAutoRedirect = false,
            UseDefaultCredentials = false, Credentials = null, PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None, MaxResponseHeadersLength = 16
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, destination)
        { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        request.Headers.ConnectionClose = true;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation); bounded.CancelAfter(timeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                return Finish(ProxyTransportOutcome.AuthenticationFailed, "The proxy rejected authentication.");
            if (!response.IsSuccessStatusCode)
                return Finish(ProxyTransportOutcome.EndpointOrProxyFailed, "The endpoint or proxy returned HTTP " + (int)response.StatusCode + ".");
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                return Finish(ProxyTransportOutcome.Inconclusive, "The diagnostic response exceeded the size limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(bounded.Token).ConfigureAwait(false);
            var buffer = new byte[MaxResponseBytes + 1]; var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), bounded.Token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
            if (length > MaxResponseBytes) return Finish(ProxyTransportOutcome.Inconclusive, "The diagnostic response exceeded the size limit.");
            var ip = ParseReportedIp(buffer.AsSpan(0, length));
            return Finish(ProxyTransportOutcome.Connected, ip is null
                ? "The .NET transport received a successful proxied response; no valid IP address was reported."
                : "The .NET transport received a successful proxied response that reported an IP address.", ip);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { return Finish(ProxyTransportOutcome.Canceled, "The diagnostic was canceled."); }
        catch (OperationCanceledException)
        { return Finish(ProxyTransportOutcome.TimedOut, "The diagnostic timed out; the proxy or endpoint may be unavailable."); }
        catch (HttpRequestException ex)
        {
            if (ex.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                return Finish(ProxyTransportOutcome.AuthenticationFailed, "Proxy authentication failed or was rejected.");
            return ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => Finish(ProxyTransportOutcome.NameResolutionFailed, "Proxy or endpoint name resolution failed."),
                HttpRequestError.ConnectionError => Finish(ProxyTransportOutcome.ConnectionFailed, "A connection to the proxy or endpoint failed."),
                HttpRequestError.SecureConnectionError => Finish(ProxyTransportOutcome.TlsFailed, "A TLS handshake with the proxy or endpoint failed."),
                HttpRequestError.UserAuthenticationError => Finish(ProxyTransportOutcome.EndpointOrProxyFailed, "Authentication at the proxy or endpoint could not be completed."),
                HttpRequestError.ProxyTunnelError => Finish(ProxyTransportOutcome.EndpointOrProxyFailed, "The proxy tunnel could not be established."),
                _ => Finish(ProxyTransportOutcome.Inconclusive, "The transport test failed without a definitive diagnosis.")
            };
        }
        catch (SocketException) { return Finish(ProxyTransportOutcome.ConnectionFailed, "A connection to the proxy or endpoint failed."); }
        catch (IOException) { return Finish(ProxyTransportOutcome.Inconclusive, "The transport ended without a definitive diagnosis."); }
        catch (NotSupportedException) { return Finish(ProxyTransportOutcome.Unsupported, "This .NET proxy transport is unavailable."); }
    }

    public static Uri ValidateEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 2048 || endpoint != endpoint.Trim() || endpoint.Any(char.IsControl) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host.Length == 0 || uri.Port is < 1 or > 65535 ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
            throw new ArgumentException("Use an HTTPS diagnostic URL without credentials, query or fragment; HTTP is allowed only for loopback fixtures.");
        return uri;
    }

    private static string? ParseReportedIp(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var text = Utf8.GetString(bytes).Trim();
            if (IPAddress.TryParse(text, out var ip)) return ip.ToString();
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            var values = json.RootElement.EnumerateObject().Where(item => item.Name.Equals("ip", StringComparison.OrdinalIgnoreCase)).ToArray();
            return values.Length == 1 && values[0].Value.ValueKind == JsonValueKind.String &&
                IPAddress.TryParse(values[0].Value.GetString(), out ip) ? ip.ToString() : null;
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or ArgumentException) { return null; }
    }

    private sealed class FixedProxy(Uri address, ICredentials? credentials) : IWebProxy
    {
        public ICredentials? Credentials { get; set; } = credentials;
        public Uri GetProxy(Uri destination) => address;
        public bool IsBypassed(Uri host) => false;
    }
}
