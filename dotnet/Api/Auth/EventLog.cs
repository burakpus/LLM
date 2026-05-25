using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace SetYazilim.Llm.Api.Auth;

// ─────────────────────────────────────────────────────────────────────────────
// OWASP-aligned event logging (Logging Cheat Sheet / ASVS L2)
//
// Captures security-relevant events with full who/what/where/why/when context.
// All events are persisted to the `event_log` table for audit and forensics.
// ─────────────────────────────────────────────────────────────────────────────

public enum EventCategory
{
    /// <summary>Login, logout, password change, MFA, lockout</summary>
    Auth,
    /// <summary>Permission checks, admin access grant/deny</summary>
    Authz,
    /// <summary>Session creation, expiry, hijack attempts</summary>
    Session,
    /// <summary>Input validation failures (injection, XSS, malformed)</summary>
    Input,
    /// <summary>Configuration changes (settings, LDAP, models)</summary>
    Config,
    /// <summary>Data access: read/write/delete on sensitive resources</summary>
    Data,
    /// <summary>Security events: rate limit hit, suspicious patterns</summary>
    Security,
    /// <summary>System: startup, shutdown, errors, jobs</summary>
    System,
}

public enum EventSeverity
{
    Debug,
    Info,
    Warn,
    Error,
    Critical,
}

public enum EventResult
{
    Success,
    Failure,
    Denied,
    Error,
}

public sealed record EventRecord(
    string         Category,
    string         Severity,
    string         EventType,
    string?        Username,
    string?        SourceIp,
    string?        UserAgent,
    string?        RequestId,
    string?        SessionId,
    string?        Endpoint,
    string?        Action,
    string?        Resource,
    string         Result,
    string?        Reason,
    string?        DetailsJson);

public interface IEventLog
{
    /// <summary>Low-level logging entry point with full event context.</summary>
    Task LogAsync(EventCategory cat, EventSeverity sev, string eventType,
                  EventResult result, string? reason = null,
                  string? action = null, string? resource = null,
                  object? details = null, string? username = null,
                  CancellationToken ct = default);

    // ── Convenience wrappers (most common cases) ─────────────────────────────
    Task AuthSuccessAsync(string username, string domain, CancellationToken ct = default);
    Task AuthFailAsync(string username, string domain, string reason, CancellationToken ct = default);
    Task AuthzDeniedAsync(string username, string resource, string reason, CancellationToken ct = default);
    Task SecurityAsync(string eventType, string? reason, object? details = null, CancellationToken ct = default);
    Task ConfigChangeAsync(string what, string? before, string? after, CancellationToken ct = default);
    Task DataAccessAsync(string action, string resource, EventResult result, object? details = null, CancellationToken ct = default);
}

public sealed class EventLog : IEventLog
{
    private readonly NpgsqlDataSource _ds;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<EventLog> _log;

    public EventLog(NpgsqlDataSource ds, IHttpContextAccessor http, ILogger<EventLog> log)
    {
        _ds = ds; _http = http; _log = log;
    }

    public async Task LogAsync(EventCategory cat, EventSeverity sev, string eventType,
                               EventResult result, string? reason = null,
                               string? action = null, string? resource = null,
                               object? details = null, string? username = null,
                               CancellationToken ct = default)
    {
        var ctx       = _http.HttpContext;
        var ip        = ExtractIp(ctx);
        var ua        = ctx?.Request.Headers.UserAgent.ToString();
        var endpoint  = ctx?.Request.Path.Value;
        var requestId = ctx?.TraceIdentifier;
        var sessionId = ctx?.Request.Headers["X-Session-Id"].ToString();

        // Resolve username from claims if not provided
        username ??= ctx?.User?.Identity?.Name;

        var detailsJson = details != null
            ? JsonSerializer.Serialize(details, new JsonSerializerOptions { WriteIndented = false })
            : null;

        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO event_log
                (category, severity, event_type, username, source_ip, user_agent,
                 request_id, session_id, endpoint, action, resource, result, reason, details, ts)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14::jsonb, NOW())";
            cmd.Parameters.AddWithValue(cat.ToString());
            cmd.Parameters.AddWithValue(sev.ToString());
            cmd.Parameters.AddWithValue(eventType);
            cmd.Parameters.AddWithValue((object?)username ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)ip ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)Truncate(ua, 500) ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)requestId ?? DBNull.Value);
            cmd.Parameters.AddWithValue(string.IsNullOrEmpty(sessionId) ? DBNull.Value : sessionId);
            cmd.Parameters.AddWithValue((object?)Truncate(endpoint, 500) ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)action ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)Truncate(resource, 500) ?? DBNull.Value);
            cmd.Parameters.AddWithValue(result.ToString());
            cmd.Parameters.AddWithValue((object?)Truncate(reason, 1000) ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)detailsJson ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            // Logging must never break the request. Surface to ILogger only.
            _log.LogWarning("EventLog write failed: {Msg}", ex.Message);
        }
    }

    public Task AuthSuccessAsync(string username, string domain, CancellationToken ct = default) =>
        LogAsync(EventCategory.Auth, EventSeverity.Info, "auth.login.success",
            EventResult.Success, reason: null,
            action: "login", resource: $"domain:{domain}",
            username: username, ct: ct);

    public Task AuthFailAsync(string username, string domain, string reason, CancellationToken ct = default) =>
        LogAsync(EventCategory.Auth, EventSeverity.Warn, "auth.login.fail",
            EventResult.Failure, reason: reason,
            action: "login", resource: $"domain:{domain}",
            username: username, ct: ct);

    public Task AuthzDeniedAsync(string username, string resource, string reason, CancellationToken ct = default) =>
        LogAsync(EventCategory.Authz, EventSeverity.Warn, "authz.access.denied",
            EventResult.Denied, reason: reason,
            action: "access", resource: resource,
            username: username, ct: ct);

    public Task SecurityAsync(string eventType, string? reason, object? details = null, CancellationToken ct = default) =>
        LogAsync(EventCategory.Security, EventSeverity.Warn, eventType,
            EventResult.Denied, reason: reason, details: details, ct: ct);

    public Task ConfigChangeAsync(string what, string? before, string? after, CancellationToken ct = default) =>
        LogAsync(EventCategory.Config, EventSeverity.Info, "config.change",
            EventResult.Success, reason: null,
            action: "update", resource: what,
            details: new { before = Truncate(before, 500), after = Truncate(after, 500) },
            ct: ct);

    public Task DataAccessAsync(string action, string resource, EventResult result, object? details = null, CancellationToken ct = default) =>
        LogAsync(EventCategory.Data,
            result == EventResult.Success ? EventSeverity.Info : EventSeverity.Warn,
            $"data.{action}", result, reason: null,
            action: action, resource: resource, details: details, ct: ct);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string? ExtractIp(HttpContext? ctx)
    {
        if (ctx == null) return null;
        // Honor X-Forwarded-For (single hop) since we're behind nginx
        var fwd = ctx.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrEmpty(fwd))
        {
            var first = fwd.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(first)) return first;
        }
        return ctx.Connection.RemoteIpAddress?.ToString();
    }

    private static string? Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? s : s.Length > max ? s[..max] : s;
}
