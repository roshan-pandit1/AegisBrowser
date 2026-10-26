using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AegisBrowser.Lab;

public sealed class LabServer : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly LabSessionRegistry sessions = new();
    private int disposed;
    public MockSecurityAdapter Security { get; } = new();
    public LabReport Report { get; } = new();
    private X509Certificate2? certificate;
    public bool AllowDnsFailure { get; set; }
    public string TlsOrigin { get; private set; } = "";
    public const string DnsFailureUrl = "http://aegis-lab-dns.invalid/";
    public string Origin { get; private set; } = "";
    private LabServer(WebApplication app) { this.app = app; }
    public static async Task<LabServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders(); // No URL/header/body logging, including request query strings.
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(2));
        var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.UserKeySet);
        builder.WebHost.ConfigureKestrel(o => { o.Listen(IPAddress.Loopback, 0); o.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)); });
        var app = builder.Build();
        var server = new LabServer(app) { certificate = certificate };
        app.Use(async (context, next) => {
            if (context.Request.Host.Host is not ("127.0.0.1" or "localhost") || context.Request.Host.Port != context.Connection.LocalPort) { context.Response.StatusCode = 403; return; }
            if (context.Request.Method != "GET" && context.Request.Headers.Origin.Count > 0 && context.Request.Headers.Origin.ToString() != server.Origin.TrimEnd('/')) { context.Response.StatusCode = 403; return; }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self' http://localhost:" + context.Request.Host.Port + "; connect-src 'self'; frame-src 'none'; frame-ancestors 'none'; object-src 'none'; base-uri 'none'; form-action 'self'";
            await next(context);
        });
        app.MapGet("/", () => Results.Content("""
            <!doctype html><html><head><meta charset="utf-8"><title>Aegis authorized lab</title></head><body>
            <h1>Local security test</h1><p>Synthetic sessions only. No passwords or real providers.</p>
            <button id="login">Create mock session</button><button id="check">Check security decision</button>
            <button id="access">Request protected resource</button><pre id="result">Ready</pre>
            <script src="/app.js"></script><script src="/third-party.js"></script></body></html>
            """, "text/html"));
        app.MapGet("/scenario", () => Results.Content("<html><head><title>Mock security scenario</title></head><body><pre id='result'>Running</pre><script src='/run.js'></script></body></html>", "text/html"));
        app.MapGet("/run.js", () => Results.Content("""
            (async()=>{try {await fetch('/login',{method:'POST'}); const decision=await fetch('/security',{method:'POST'}); const access=await fetch('/protected'); window.labResult={decision:decision.status,access:access.status};document.getElementById('result').textContent=JSON.stringify(window.labResult);}catch{window.labResult={failed:true};document.getElementById('result').textContent='Failed';}})();
            """, "text/javascript"));
        app.MapGet("/app.js", () => Results.Content("""
            for (const [id,path] of [['login','login'],['check','security'],['access','protected']]) {
              document.getElementById(id).onclick=async()=>{try {const r=await fetch('/'+path,{method:path==='protected'?'GET':'POST'});document.getElementById('result').textContent=r.status+' '+await r.text();}catch{document.getElementById('result').textContent='Request failed';}};
            }
            localStorage.setItem('aegis-lab','synthetic');
            const script=document.createElement('script');script.src='http://localhost:'+location.port+'/tracker.js';document.body.append(script);
            """, "text/javascript"));
        app.MapGet("/third-party.js", () => Results.Content("window.firstPartyFixture=true;", "text/javascript"));
        app.MapGet("/tracker.js", () => Results.Content("window.thirdPartyFixture=true;", "text/javascript"));
        app.MapPost("/login", (HttpContext c) => {
            var id = server.sessions.Issue(c.Request.Cookies["aegis-lab-session"]);
            if (id is null) return Results.StatusCode(503);
            c.Response.Cookies.Append("aegis-lab-session", id, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, MaxAge = LabSessionRegistry.Lifetime, Path = "/" });
            server.Report.Add(LabEventKind.Authentication, server.Origin, 200, "Authenticated");
            return Results.Json(new { authenticated = true });
        });
        app.MapPost("/security", async (HttpContext c) => {
            var id = c.Request.Cookies["aegis-lab-session"];
            if (id is null || !server.sessions.Exists(id)) return Results.StatusCode(401);
            // Explicit Allow only. Unknown responses, exceptions and cancellation cannot grant access.
            var version = server.Security.Version;
            SecurityDecision decision;
            try { decision = await server.Security.EvaluateAsync(c.RequestAborted); }
            catch { decision = SecurityDecision.Unavailable; }
            var current = server.Security.Capture();
            if (version != current.Version || c.RequestAborted.IsCancellationRequested) decision = SecurityDecision.Unavailable;
            var granted = server.sessions.Apply(id, version, decision, current, c.RequestAborted.IsCancellationRequested);
            if (decision == SecurityDecision.Allow && !granted) decision = SecurityDecision.Unavailable;
            var status = decision == SecurityDecision.Allow ? 200 : decision == SecurityDecision.Unavailable ? 503 : 403;
            server.Report.Add(LabEventKind.Security, server.Origin, status, decision.ToString());
            return Results.Json(new { decision = decision.ToString(), access = decision == SecurityDecision.Allow }, statusCode: status);
        });
        app.MapPost("/reset", (HttpContext c) => {
            server.sessions.Revoke(c.Request.Cookies["aegis-lab-session"]);
            c.Response.Cookies.Delete("aegis-lab-session", new CookieOptions { Path = "/" });
            return Results.NoContent();
        });
        app.MapGet("/protected", (HttpContext c) => {
            var id = c.Request.Cookies["aegis-lab-session"];
            var allowed = server.sessions.IsAuthorized(id, server.Security.Capture());
            return Results.Json(new { access = allowed }, statusCode: allowed ? 200 : 403);
        });
        app.MapGet("/redirect", () => Results.Redirect("/redirect-final"));
        app.MapGet("/redirect-final", () => Results.Redirect("/"));
        app.MapGet("/unavailable", () => Results.StatusCode(503));
        app.MapGet("/disconnect", (HttpContext c) => { c.Abort(); return Task.CompletedTask; });
        try { await app.StartAsync(); }
        catch { await server.DisposeAsync(); throw; }
        server.Origin = app.Urls.Single(u => u.StartsWith("http:")).TrimEnd('/') + "/";
        server.TlsOrigin = app.Urls.Single(u => u.StartsWith("https:")).TrimEnd('/') + "/";
        return server;
    }
    public bool Contains(string url) => (AllowDnsFailure && url == DnsFailureUrl) || Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        && uri.Host is "127.0.0.1" or "localhost" && (uri.Scheme == "http" ? uri.Port == new Uri(Origin).Port : uri.Port == new Uri(TlsOrigin).Port) && string.IsNullOrEmpty(uri.UserInfo);
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        sessions.Close(); // Revoke before waiting for in-flight HTTP requests to drain.
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(timeout.Token);
        }
        finally
        {
            try { await app.DisposeAsync(); }
            finally { sessions.Clear(); certificate?.Dispose(); }
        }
    }
}
