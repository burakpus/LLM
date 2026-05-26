using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SetYazilim.Llm.Api.Auth;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// /api/auth/* — domains, login (with brute-force RL), groups, debug-ldap, me.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app)
    {
        // GET /api/auth/domains — list available AD domains (public)
        app.MapGet("/api/auth/domains", (ILdapAuthService ldap) =>
            Results.Ok(ldap.GetDomains()));

        // POST /api/auth/login
        app.MapPost("/api/auth/login", async (
            [FromBody] LoginRequest req,
            HttpContext http,
            ILdapAuthService ldap,
            IJwtTokenService jwt,
            IMemoryCache cache,
            IConfiguration appCfg,
            IEventLog evt,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            {
                await evt.LogAsync(EventCategory.Auth, EventSeverity.Warn, "auth.login.bad_request",
                    EventResult.Failure, reason: "missing credentials",
                    action: "login", resource: $"domain:{req.Domain}", username: req.Username, ct: ct);
                return Results.BadRequest(new { error = "Username and password are required." });
            }

            // Brute-force koruması — per (IP, username), Limits:LoginRateLimitPerMinute
            var ip = (http.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim())
                     ?.Length > 0
                     ? http.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim()
                     : (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            var loginMax = appCfg.GetValue<int?>("Limits:LoginRateLimitPerMinute") ?? 5;
            if (!LoginRateLimit.TryAcquire(cache, ip, req.Username, loginMax, out var retryAfter))
            {
                await evt.LogAsync(EventCategory.Security, EventSeverity.Warn, "security.rate_limit",
                    EventResult.Denied, reason: $"login brute-force, retry in {retryAfter}s",
                    action: "login", resource: $"domain:{req.Domain}",
                    details: new { ip, username = req.Username, retryAfter },
                    username: req.Username, ct: ct);
                return Results.Json(new { error = $"Çok fazla deneme. {retryAfter} saniye sonra tekrar deneyin." },
                    statusCode: 429);
            }

            if (!ldap.Authenticate(req.Domain, req.Username, req.Password))
            {
                await evt.AuthFailAsync(req.Username, req.Domain, "ldap_reject", ct);
                return Results.Unauthorized();
            }

            var isAdmin = ldap.IsAdmin(req.Domain, req.Username, req.Password);
            var groups  = ldap.GetUserGroups(req.Domain, req.Username, req.Password);
            var token   = jwt.Generate(req.Username, req.Domain, isAdmin, groups);
            await evt.LogAsync(EventCategory.Auth, EventSeverity.Info, "auth.login.success",
                EventResult.Success, reason: null,
                action: "login", resource: $"domain:{req.Domain}",
                details: new { isAdmin, groupCount = groups.Length },
                username: req.Username, ct: ct);
            return Results.Ok(token);
        });

        // GET /api/auth/groups — shows current admin config (authenticated, admin context)
        app.MapGet("/api/auth/groups", [Authorize] (
            ClaimsPrincipal user,
            IOptions<LdapOptions> ldapOpts) =>
        {
            var opts     = ldapOpts.Value;
            var username = user.FindFirstValue(ClaimTypes.Name) ?? "";
            var domain   = user.FindFirstValue("domain") ?? "";

            if (!opts.Domains.TryGetValue(domain.ToUpperInvariant(), out _))
                return Results.BadRequest(new { error = $"Domain not configured: {domain}" });

            return Results.Ok(new {
                username,
                domain,
                adminUserSet  = opts.AdminUserSet.ToArray(),
                adminGroupSet = opts.AdminGroupSet.ToArray(),
            });
        });

        // POST /api/auth/debug-ldap — comprehensive step-by-step LDAP diagnostic (admin only)
        // Tests: config → connect → TLS → service-account bind → user search → user bind → group fetch
        app.MapPost("/api/auth/debug-ldap", [Authorize("AdminOnly")] (
            [FromBody] LoginRequest req,
            ILdapAuthService ldap) =>
        {
            var result = ldap.Diagnose(req.Domain, req.Username ?? "", req.Password ?? "");
            return Results.Ok(result);
        });

        // GET /api/auth/me
        app.MapGet("/api/auth/me", [Authorize] (ClaimsPrincipal user) =>
        {
            var groupsClaim = user.FindFirstValue(AppClaims.Groups) ?? "";
            var groups = groupsClaim.Split(';', StringSplitOptions.RemoveEmptyEntries);
            return Results.Ok(new
            {
                username = user.FindFirstValue(ClaimTypes.Name),
                domain   = user.FindFirstValue("domain"),
                isAdmin  = user.FindFirstValue(AppClaims.IsAdmin) == "true",
                groups,
            });
        });

        return app;
    }
}
