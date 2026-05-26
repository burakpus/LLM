using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// /api/admin/activity-log + /api/admin/event-log[/summary] — admin observability endpoints.
/// </summary>
public static class EventLogEndpoints
{
    public static IEndpointRouteBuilder MapEventLog(this IEndpointRouteBuilder app)
    {
        // GET /api/admin/activity-log?page=1&pageSize=50&action=
        app.MapGet("/api/admin/activity-log", [Authorize("AdminOnly")] async (
            int page, int pageSize, string? action,
            NpgsqlDataSource ds, CancellationToken ct) =>
        {
            page     = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 10, 200);
            var offset = (page - 1) * pageSize;

            await using var conn = await ds.OpenConnectionAsync(ct);

            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = string.IsNullOrEmpty(action)
                ? "SELECT COUNT(*) FROM activity_log"
                : "SELECT COUNT(*) FROM activity_log WHERE action=$1";
            if (!string.IsNullOrEmpty(action)) countCmd.Parameters.AddWithValue(action);
            var total = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct));

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = string.IsNullOrEmpty(action)
                ? @"SELECT id, username, action, target, details, created_at
                    FROM activity_log ORDER BY created_at DESC LIMIT $1 OFFSET $2"
                : @"SELECT id, username, action, target, details, created_at
                    FROM activity_log WHERE action=$3 ORDER BY created_at DESC LIMIT $1 OFFSET $2";
            cmd.Parameters.AddWithValue(pageSize);
            cmd.Parameters.AddWithValue(offset);
            if (!string.IsNullOrEmpty(action)) cmd.Parameters.AddWithValue(action);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            var items = new List<object>();
            while (await r.ReadAsync(ct))
                items.Add(new {
                    id        = r.GetInt64(0),
                    username  = r.GetString(1),
                    action    = r.GetString(2),
                    target    = r.GetString(3),
                    details   = r.GetString(4),
                    createdAt = r.GetDateTime(5),
                });

            return Results.Ok(new { total, page, pageSize, items });
        });

        // GET /api/admin/event-log — OWASP-aligned event log query
        // Filters: category, severity, eventType, username, sourceIp, result, q (free text), since, until
        app.MapGet("/api/admin/event-log", [Authorize("AdminOnly")] async (
            int? page, int? pageSize,
            string? category, string? severity, string? eventType,
            string? username, string? sourceIp, string? result, string? q,
            DateTime? since, DateTime? until,
            NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var p  = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 50, 10, 500);
            var offset = (p - 1) * ps;

            var clauses = new List<string>();
            var args    = new List<object>();
            void Add(string col, string? v)
            {
                if (string.IsNullOrEmpty(v)) return;
                args.Add(v);
                clauses.Add($"{col}=${args.Count}");
            }

            Add("category",   category);
            Add("severity",   severity);
            Add("event_type", eventType);
            Add("username",   username);
            Add("source_ip",  sourceIp);
            Add("result",     result);

            if (!string.IsNullOrEmpty(q))
            {
                args.Add($"%{q}%");
                clauses.Add($"(event_type ILIKE ${args.Count} OR action ILIKE ${args.Count} OR resource ILIKE ${args.Count} OR reason ILIKE ${args.Count})");
            }
            if (since.HasValue)
            {
                args.Add(since.Value);
                clauses.Add($"ts >= ${args.Count}");
            }
            if (until.HasValue)
            {
                args.Add(until.Value);
                clauses.Add($"ts <= ${args.Count}");
            }

            var where = clauses.Count > 0 ? " WHERE " + string.Join(" AND ", clauses) : "";

            await using var conn = await ds.OpenConnectionAsync(ct);

            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM event_log" + where;
            foreach (var a in args) countCmd.Parameters.AddWithValue(a);
            var total = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct));

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT id, ts, category, severity, event_type, username, source_ip,
                                       user_agent, request_id, session_id, endpoint, action, resource,
                                       result, reason, details
                                FROM event_log" + where +
                $" ORDER BY ts DESC LIMIT ${args.Count + 1} OFFSET ${args.Count + 2}";
            foreach (var a in args) cmd.Parameters.AddWithValue(a);
            cmd.Parameters.AddWithValue(ps);
            cmd.Parameters.AddWithValue(offset);

            var items = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                items.Add(new
                {
                    id         = r.GetInt64(0),
                    ts         = r.GetDateTime(1),
                    category   = r.GetString(2),
                    severity   = r.GetString(3),
                    eventType  = r.GetString(4),
                    username   = r.IsDBNull(5)  ? null : r.GetString(5),
                    sourceIp   = r.IsDBNull(6)  ? null : r.GetString(6),
                    userAgent  = r.IsDBNull(7)  ? null : r.GetString(7),
                    requestId  = r.IsDBNull(8)  ? null : r.GetString(8),
                    sessionId  = r.IsDBNull(9)  ? null : r.GetString(9),
                    endpoint   = r.IsDBNull(10) ? null : r.GetString(10),
                    action     = r.IsDBNull(11) ? null : r.GetString(11),
                    resource   = r.IsDBNull(12) ? null : r.GetString(12),
                    result     = r.GetString(13),
                    reason     = r.IsDBNull(14) ? null : r.GetString(14),
                    details    = r.IsDBNull(15) ? null : r.GetString(15),
                });
            }

            return Results.Ok(new { total, page = p, pageSize = ps, items });
        });

        // GET /api/admin/event-log/summary — counts by category and severity for last 24h
        app.MapGet("/api/admin/event-log/summary", [Authorize("AdminOnly")] async (
            NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = @"SELECT category, severity, COUNT(*)::bigint AS n
                                FROM event_log
                                WHERE ts >= NOW() - INTERVAL '24 hours'
                                GROUP BY category, severity
                                ORDER BY category, severity";
            var rows = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new { category = r.GetString(0), severity = r.GetString(1), count = r.GetInt64(2) });
            return Results.Ok(new { since = DateTime.UtcNow.AddHours(-24), rows });
        });

        return app;
    }
}
