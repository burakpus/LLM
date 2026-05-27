using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Npgsql;
using SetYazilim.Llm;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// /api/admin/usage/* — LiteLLM spend proxy + per-user/model/end-user aggregations.
/// </summary>
public static class UsageEndpoints
{
    private static async Task<IResult> LiteLLMProxy(string path, IOptions<LiteLLMOptions> opts,
        IHttpClientFactory httpFactory, CancellationToken ct)
    {
        using var client = httpFactory.CreateClient("proxy");
        client.Timeout = TimeSpan.FromSeconds(15);
        using var req = new HttpRequestMessage(HttpMethod.Get,
            opts.Value.BaseUrl.TrimEnd('/') + path);
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.Value.ApiKey);
        using var resp = await client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return Results.Text(body, "application/json", statusCode: (int)resp.StatusCode);
    }

    public static IEndpointRouteBuilder MapUsage(this IEndpointRouteBuilder app)
    {
// GET /api/admin/usage/users
app.MapGet("/api/admin/usage/users", [Authorize("AdminOnly")] async (
    IOptions<LiteLLMOptions> opts, IHttpClientFactory http, CancellationToken ct) =>
    await LiteLLMProxy("/spend/users", opts, http, ct));

// GET /api/admin/usage/session-users — token stats from our own session_memories (reliable)
app.MapGet("/api/admin/usage/session-users", [Authorize("AdminOnly")] async (
    NpgsqlDataSource ds, CancellationToken ct) =>
{
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT user_id,
               COUNT(*)                                                        AS messages,
               COALESCE(SUM(CASE WHEN role='user'      THEN token_count ELSE 0 END), 0) AS prompt_tokens,
               COALESCE(SUM(CASE WHEN role='assistant' THEN token_count ELSE 0 END), 0) AS completion_tokens,
               COALESCE(SUM(token_count), 0)                                   AS total_tokens,
               MAX(created_at)                                                  AS last_active
        FROM   session_memories
        GROUP  BY user_id
        ORDER  BY total_tokens DESC
        LIMIT  100;";
    await using var r = await cmd.ExecuteReaderAsync(ct);
    var rows = new List<object>();
    while (await r.ReadAsync(ct))
        rows.Add(new {
            userId           = r.GetString(0),
            messages         = r.GetInt64(1),
            promptTokens     = r.GetInt64(2),
            completionTokens = r.GetInt64(3),
            totalTokens      = r.GetInt64(4),
            lastActive       = r.GetDateTime(5)
        });
    return Results.Ok(rows);
});

// GET /api/admin/usage/models — aggregate from spend logs (reliable after reset)
app.MapGet("/api/admin/usage/models", [Authorize("AdminOnly")] async (
    IOptions<LiteLLMOptions> litellmOpts, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    using var client = httpFactory.CreateClient("proxy");
    client.Timeout = TimeSpan.FromSeconds(30);
    using var req = new HttpRequestMessage(HttpMethod.Get,
        litellmOpts.Value.BaseUrl.TrimEnd('/') + "/spend/logs?limit=5000");
    req.Headers.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", litellmOpts.Value.ApiKey);
    using var resp = await client.SendAsync(req, ct);
    var body = await resp.Content.ReadAsStringAsync(ct);

    var doc  = JsonSerializer.Deserialize<JsonElement>(body);
    var logs = doc.ValueKind == JsonValueKind.Array ? doc
             : doc.TryGetProperty("data", out var d) ? d : default;

    if (logs.ValueKind != JsonValueKind.Array)
        return Results.Ok(Array.Empty<object>());

    var agg = new Dictionary<string, (long tokens, long count)>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in logs.EnumerateArray())
    {
        var model  = entry.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()! : "unknown";
        var total  = entry.TryGetProperty("total_tokens", out var tt) ? tt.GetInt64() : 0;
        var cnt    = agg.TryGetValue(model, out var prev) ? prev : (0, 0);
        agg[model] = (cnt.tokens + total, cnt.count + 1);
    }

    var result = agg
        .OrderByDescending(kv => kv.Value.tokens)
        .Select(kv => new { model = kv.Key, total_tokens = kv.Value.tokens, total_count = kv.Value.count });

    return Results.Ok(result);
});

// GET /api/admin/usage/logs?limit=50
// LiteLLM 'limit' parametresini bazı sürümlerde dikkate almıyor — biz de
// üst sınırı sunucu tarafında JSON'u parse edip dilimliyoruz, kullanıcıya
// tam istediği kadar satır dönsün.
app.MapGet("/api/admin/usage/logs", [Authorize("AdminOnly")] async (
    int limit,
    IOptions<LiteLLMOptions> litellmOpts, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var n = Math.Clamp(limit, 1, 1000);
    using var client = httpFactory.CreateClient("proxy");
    client.Timeout = TimeSpan.FromSeconds(30);
    using var req = new HttpRequestMessage(HttpMethod.Get,
        litellmOpts.Value.BaseUrl.TrimEnd('/') + $"/spend/logs?limit={n}");
    req.Headers.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", litellmOpts.Value.ApiKey);
    using var resp = await client.SendAsync(req, ct);
    var body = await resp.Content.ReadAsStringAsync(ct);

    // Parse JSON (array OR {data: array}) and take first N
    try
    {
        var doc = JsonSerializer.Deserialize<JsonElement>(body);
        var arr = doc.ValueKind == JsonValueKind.Array ? doc
                : doc.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d
                : default;
        if (arr.ValueKind == JsonValueKind.Array)
        {
            var sliced = arr.EnumerateArray().Take(n).ToArray();
            return Results.Json(sliced);
        }
    }
    catch { /* fall through to raw body */ }

    return Results.Text(body, "application/json", statusCode: (int)resp.StatusCode);
});

// GET /api/admin/usage/end-users — aggregate end_user spend from LiteLLM logs
app.MapGet("/api/admin/usage/end-users", [Authorize("AdminOnly")] async (
    IOptions<LiteLLMOptions> opts, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    using var client = httpFactory.CreateClient("proxy");
    client.Timeout = TimeSpan.FromSeconds(30);
    using var req = new HttpRequestMessage(HttpMethod.Get,
        opts.Value.BaseUrl.TrimEnd('/') + "/spend/logs?limit=5000");
    req.Headers.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.Value.ApiKey);
    using var resp = await client.SendAsync(req, ct);
    var body = await resp.Content.ReadAsStringAsync(ct);

    var doc  = JsonSerializer.Deserialize<JsonElement>(body);
    var logs = doc.ValueKind == JsonValueKind.Array ? doc
             : doc.TryGetProperty("data", out var d) ? d : default;

    if (logs.ValueKind != JsonValueKind.Array)
        return Results.Ok(Array.Empty<object>());

    // aggregate by end_user (fallback → user field)
    var agg = new Dictionary<string, (long prompt, long completion, long total, int requests, DateTime last)>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in logs.EnumerateArray())
    {
        var userId = entry.TryGetProperty("end_user", out var eu) && eu.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(eu.GetString())
            ? eu.GetString()!
            : entry.TryGetProperty("user",     out var u)  && u.ValueKind  == JsonValueKind.String ? u.GetString()!  : "(anonymous)";

        var prompt     = entry.TryGetProperty("prompt_tokens",     out var pt) ? pt.GetInt64() : 0;
        var completion = entry.TryGetProperty("completion_tokens", out var ct2) ? ct2.GetInt64() : 0;
        var total      = entry.TryGetProperty("total_tokens",      out var tt) ? tt.GetInt64() : prompt + completion;
        var ts         = entry.TryGetProperty("startTime",         out var st) && DateTime.TryParse(st.GetString(), out var d2) ? d2 : DateTime.MinValue;

        if (agg.TryGetValue(userId, out var prev))
            agg[userId] = (prev.prompt + prompt, prev.completion + completion, prev.total + total, prev.requests + 1, ts > prev.last ? ts : prev.last);
        else
            agg[userId] = (prompt, completion, total, 1, ts);
    }

    var result = agg
        .OrderByDescending(kv => kv.Value.total)
        .Select(kv => new {
            userId           = kv.Key,
            messages         = (long)kv.Value.requests,
            promptTokens     = kv.Value.prompt,
            completionTokens = kv.Value.completion,
            totalTokens      = kv.Value.total,
            lastActive       = kv.Value.last == DateTime.MinValue ? (DateTime?)null : kv.Value.last
        });

    return Results.Ok(result);
});

        return app;
    }
}
