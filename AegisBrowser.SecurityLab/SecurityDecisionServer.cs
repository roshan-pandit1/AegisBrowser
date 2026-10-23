using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AegisBrowser.SecurityLab;

public sealed class SecurityDecisionServer : IAsyncDisposable
{
    public const string PagePath = "lab/security-decisions";
    public const string EvaluatePath = "api/mock-security/evaluate";
    public const string ProtectedPath = "api/mock-security/protected";
    public const string ResetPath = "api/mock-security/reset";
    private const string CookieName = "aegis-mock-session";
    private sealed class Session { public bool Allowed; public bool Revoked; public long Revision; public DateTimeOffset Expires = DateTimeOffset.UtcNow.AddMinutes(10); }
    private readonly ConcurrentDictionary<string, Session> sessions = new();
    private readonly ConcurrentQueue<EvaluationEvent> evaluations = new();
    private readonly WebApplication app;
    private readonly ISecurityDecisionProvider provider;
    public Uri Origin { get; private set; } = null!;
    public Uri PageUrl => new(Origin, PagePath);
    public EvaluationEvent[] Evaluations => evaluations.ToArray();
    private SecurityDecisionServer(WebApplication app, ISecurityDecisionProvider provider) { this.app = app; this.provider = provider; }

    public static async Task<SecurityDecisionServer> StartAsync(ISecurityDecisionProvider? provider = null, CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var server = new SecurityDecisionServer(app, provider ?? new MockSecurityDecisionProvider());
        app.Use(async (context, next) => {
            if (context.Request.Host.Host != "127.0.0.1") { context.Response.StatusCode = 403; return; }
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && origin != server.Origin?.GetLeftPart(UriPartial.Authority)) { context.Response.StatusCode = 403; return; }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            await next(context);
        });
        app.MapGet("/" + PagePath, () => Results.Content("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Security Decisions — Aegis simulation</title></head><body>
            <h1>Security Decisions: Allow simulation</h1><p>Local synthetic session. No real security provider.</p>
            <p>This page has its own temporary session, separate from the WinForms test runner.</p>
            <button id="evaluate">Evaluate mock Allow</button><button id="protected">Request protected fixture</button>
            <button id="reset">Reset Session</button><pre id="result" role="status">Not Run</pre>
            <script src="/lab/security-decisions.js"></script></body></html>
            """, "text/html"));
        app.MapGet("/lab/security-decisions.js", () => Results.Content("""
            for(const [id,path,method] of [['evaluate','evaluate','POST'],['protected','protected','GET'],['reset','reset','POST']]) {
              document.getElementById(id).onclick=async()=>{try {const r=await fetch('/api/mock-security/'+path,{method});document.getElementById('result').textContent=r.status+' '+await r.text();}catch{document.getElementById('result').textContent='Failed: request unavailable';}};
            }
            """, "text/javascript"));
        app.MapGet("/" + ProtectedPath, (HttpContext context) => {
            var session = server.Find(context);
            bool allowed = false;
            if (session is not null) lock(session) allowed = session.Allowed && !session.Revoked && session.Expires > DateTimeOffset.UtcNow;
            return allowed ? Results.Json(new { fixture = "protected-fixture", access = true }) : Results.Json(new { access = false }, statusCode: 403);
        });
        app.MapPost("/" + EvaluatePath, (Func<HttpContext, Task<IResult>>)server.Evaluate);
        app.MapPost("/" + ResetPath, (HttpContext context) => {
            var id = context.Request.Cookies[CookieName];
            if (id is not null && server.sessions.TryRemove(id, out var session)) lock(session) { session.Revoked = true; session.Allowed = false; session.Revision++; }
            context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/", SameSite = SameSiteMode.Strict });
            return Results.NoContent();
        });
        try { await app.StartAsync(cancellationToken); server.Origin = new Uri(app.Urls.Single().TrimEnd('/') + "/"); return server; }
        catch { await app.DisposeAsync(); throw; }
    }

    private Session? Find(HttpContext context) => context.Request.Cookies[CookieName] is { } id && sessions.TryGetValue(id, out var session) ? session : null;
    private async Task<IResult> Evaluate(HttpContext context)
    {
        var session = Find(context);
        if (session is null || session.Revoked || session.Expires <= DateTimeOffset.UtcNow)
        {
            foreach(var item in sessions.Where(p => p.Value.Expires <= DateTimeOffset.UtcNow)) sessions.TryRemove(item.Key, out _);
            if (sessions.Count >= 256) return Results.StatusCode(503);
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            session = new Session(); sessions[id] = session;
            context.Response.Cookies.Append(CookieName, id, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });
        }
        long revision;
        lock(session) { session.Allowed = false; revision = ++session.Revision; }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var decision = await provider.EvaluateAsync(timeout.Token).WaitAsync(timeout.Token);
            lock(session)
            {
                if (decision is null || !decision.IsValidAllow || session.Revoked || session.Revision != revision || session.Expires <= DateTimeOffset.UtcNow)
                { Record("rejected", null); return Results.StatusCode(503); }
                session.Allowed = true;
            }
            Record("allow", decision.RequestId);
            return Results.Json(decision);
        }
        catch
        {
            // Only fixed labels are recorded. Provider exception text can contain secrets.
            lock(session) { if (session.Revision == revision) session.Allowed = false; }
            Record("unavailable", null); return Results.StatusCode(503);
        }
    }
    private void Record(string outcome, string? requestId)
    {
        evaluations.Enqueue(new(DateTimeOffset.UtcNow, outcome, requestId));
        while (evaluations.Count > 256) evaluations.TryDequeue(out _);
    }
    public void ResetAllSessions()
    {
        foreach(var item in sessions) if (sessions.TryRemove(item.Key, out var session)) lock(session) { session.Revoked = true; session.Allowed = false; session.Revision++; }
    }
    public async ValueTask DisposeAsync() { ResetAllSessions(); await app.StopAsync(); await app.DisposeAsync(); }
}
