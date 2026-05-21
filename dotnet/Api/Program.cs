using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using SetYazilim.Llm;
using SetYazilim.Llm.Api.Auth;
using SetYazilim.Llm.Context;
using SetYazilim.Llm.Memory;
using SetYazilim.Llm.Retrieval;

// =============================================================================
// Agentic AI Platform — ASP.NET Core 8 Minimal API
// =============================================================================

var builder = WebApplication.CreateBuilder(args);

// Increase limits for vision requests (base64 images can be large)
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 100 * 1024 * 1024; // 100 MB
});

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables(prefix: "LLM_");

var cfg = builder.Configuration;
var services = builder.Services;

// ── JWT ───────────────────────────────────────────────────────────────────────
services.AddOptions<JwtOptions>()
    .BindConfiguration(JwtOptions.SectionName)
    .ValidateDataAnnotations();

services.AddSingleton<IJwtTokenService, JwtTokenService>();

services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        var jwtCfg = cfg.GetSection(JwtOptions.SectionName);
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer      = true,
            ValidIssuer         = jwtCfg["Issuer"] ?? "set-llm-api",
            ValidateAudience    = true,
            ValidAudience       = jwtCfg["Audience"] ?? "set-llm-ui",
            ValidateLifetime    = true,
            IssuerSigningKey    = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtCfg["Secret"] ?? "")),
            ClockSkew           = TimeSpan.FromMinutes(2)
        };
        // Allow token from query string for SSE (EventSource can't set headers)
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var t = ctx.Request.Query["token"].ToString();
                if (!string.IsNullOrEmpty(t)) ctx.Token = t;
                return Task.CompletedTask;
            }
        };
    });

services.AddAuthorization();

// ── LDAP ──────────────────────────────────────────────────────────────────────
services.AddOptions<LdapOptions>()
    .BindConfiguration(LdapOptions.SectionName);
services.AddSingleton<ILdapAuthService, LdapAuthService>();

// ── LLM + VectorStore + AgentStack ───────────────────────────────────────────
services.AddLiteLLMClient();
services.AddVectorStore();

var skillsPath = cfg["Agent:SkillsDirectory"]
              ?? Path.Combine(AppContext.BaseDirectory, "Skills");
services.AddAgentStack(skillsDirectory: skillsPath);
services.AddSingleton<IDocumentIngestion, DocumentIngestion>();

// ── CORS ──────────────────────────────────────────────────────────────────────
services.AddCors(o => o.AddPolicy("ui", p =>
{
    var origins = cfg.GetSection("Cors:Origins").Get<string[]>()
                  ?? ["http://localhost:5173", "http://localhost:5080"];
    p.WithOrigins(origins)
     .AllowAnyHeader()
     .AllowAnyMethod()
     .AllowCredentials();
}));

services.AddEndpointsApiExplorer();
services.AddSwaggerGen();
services.AddHttpClient("proxy");

// =============================================================================
var app = builder.Build();

app.UseCors("ui");
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Serve React build from wwwroot/
app.UseDefaultFiles();
app.UseStaticFiles();

// =============================================================================
// ─── Health ──────────────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "ok", ts = DateTime.UtcNow }));

// =============================================================================
// ─── Auth ─────────────────────────────────────────────────────────────────────

// GET /api/auth/domains — list available AD domains (public)
app.MapGet("/api/auth/domains", (ILdapAuthService ldap) =>
    Results.Ok(ldap.GetDomains()));

// POST /api/auth/login
app.MapPost("/api/auth/login", (
    [FromBody] LoginRequest req,
    ILdapAuthService ldap,
    IJwtTokenService jwt) =>
{
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "Username and password are required." });

    if (!ldap.Authenticate(req.Domain, req.Username, req.Password))
        return Results.Unauthorized();

    var token = jwt.Generate(req.Username, req.Domain);
    return Results.Ok(token);
});

// GET /api/auth/me
app.MapGet("/api/auth/me", [Authorize] (ClaimsPrincipal user) =>
    Results.Ok(new
    {
        username = user.FindFirstValue(ClaimTypes.Name),
        domain   = user.FindFirstValue("domain")
    }));

// =============================================================================
// ─── Skills ──────────────────────────────────────────────────────────────────

// GET /api/skills — list skills with metadata
app.MapGet("/api/skills", [Authorize] (SkillRegistry registry) =>
{
    var skills = registry.All.Select(kv =>
    {
        var content = kv.Value;
        var name = kv.Key;
        var desc = "";
        var icon = "sparkles";
        var collection = (string?)null;

        // Parse frontmatter if present
        if (content.TrimStart().StartsWith("---"))
        {
            var trimmed = content.TrimStart();
            var end = trimmed.IndexOf("---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                foreach (var line in trimmed[3..end].Split('\n'))
                {
                    var colon = line.IndexOf(':');
                    if (colon < 0) continue;
                    var k = line[..colon].Trim().ToLowerInvariant();
                    var v = line[(colon + 1)..].Trim();
                    if (k == "name")        name = v;
                    if (k == "description") desc = v;
                    if (k == "icon")        icon = v;
                    if (k == "collection")  collection = v;
                }
            }
        }

        return new { id = kv.Key, name, description = desc, icon, collection };
    });

    return Results.Ok(skills);
});

// GET /api/skills/{id} — get skill system prompt body (no frontmatter)
app.MapGet("/api/skills/{id}", [Authorize] (string id, SkillRegistry registry) =>
{
    var prompt = registry.GetSystemPrompt("default", id);
    return Results.Text(prompt, "text/plain; charset=utf-8");
});

// =============================================================================
// ─── Chat ────────────────────────────────────────────────────────────────────

app.MapPost("/api/chat", [Authorize] async (
    [FromBody] ApiChatRequest req,
    IAgentChat agentChat,
    ClaimsPrincipal user,
    CancellationToken ct) =>
{
    var userId = user.FindFirstValue(ClaimTypes.Name) ?? "anonymous";

    var result = await agentChat.ChatAsync(new ChatRequest
    {
        SessionId      = req.SessionId,
        UserId         = userId,
        AgentId        = req.AgentId,
        SkillName      = req.SkillName,
        Message        = req.Message,
        Collections    = req.Collections,
        MetadataFilter = req.MetadataFilter,
        TokenBudget    = req.TokenBudget ?? 4000
    }, ct);

    return Results.Ok(new ApiChatResponse(
        result.Content,
        result.SessionId,
        result.KbHits,
        result.MemoryHits,
        result.EstTokens));
});

// POST /api/chat/stream — SSE streaming (token query string for EventSource)
app.MapPost("/api/chat/stream", [Authorize] async (
    [FromBody] ApiChatRequest req,
    IAgentChat agentChat,
    ClaimsPrincipal user,
    HttpContext http,
    CancellationToken ct) =>
{
    var userId = user.FindFirstValue(ClaimTypes.Name) ?? "anonymous";

    http.Response.Headers.ContentType  = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers.Connection   = "keep-alive";

    await foreach (var token in agentChat.StreamAsync(new ChatRequest
    {
        SessionId      = req.SessionId,
        UserId         = userId,
        AgentId        = req.AgentId,
        SkillName      = req.SkillName,
        Message        = req.Message,
        Collections    = req.Collections,
        MetadataFilter = req.MetadataFilter,
        TokenBudget    = req.TokenBudget ?? 4000,
        Stream         = true
    }, ct))
    {
        var line = Encoding.UTF8.GetBytes($"data: {JsonSerializer.Serialize(new { token })}\n\n");
        await http.Response.Body.WriteAsync(line, ct);
        await http.Response.Body.FlushAsync(ct);
    }

    await http.Response.Body.WriteAsync(
        Encoding.UTF8.GetBytes("data: [DONE]\n\n"), ct);
});

// =============================================================================
// ─── Ingest ──────────────────────────────────────────────────────────────────

app.MapPost("/api/ingest", [Authorize] async (
    [FromBody] ApiIngestRequest req,
    IDocumentIngestion ingestion,
    CancellationToken ct) =>
{
    var result = await ingestion.IngestAsync(new IngestRequest
    {
        Collection   = req.Collection,
        Source       = req.Source,
        Title        = req.Title,
        Content      = req.Content,
        Metadata     = req.Metadata ?? "{}",
        ChunkSize    = req.ChunkSize   ?? 1600,
        ChunkOverlap = req.ChunkOverlap ?? 200
    }, ct);
    return Results.Ok(result);
});

app.MapDelete("/api/ingest/{collection}/{*source}", [Authorize] async (
    string collection, string source,
    IDocumentIngestion ingestion, CancellationToken ct) =>
{
    var n = await ingestion.DeleteSourceAsync(collection, source, ct);
    return Results.Ok(new { deleted = n });
});

// =============================================================================
// ─── Admin — RAG Management ──────────────────────────────────────────────────

// POST /api/admin/upload — multipart file upload, auto-parse and ingest
app.MapPost("/api/admin/upload", [Authorize] async (
    HttpContext http,
    IDocumentIngestion ingestion,
    CancellationToken ct) =>
{
    if (!http.Request.HasFormContentType)
        return Results.BadRequest(new { error = "multipart/form-data required" });

    var form       = await http.Request.ReadFormAsync(ct);
    var collection = form["collection"].FirstOrDefault() ?? "default";
    var results    = new List<object>();

    foreach (var file in form.Files)
    {
        try
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var text = SetYazilim.Llm.Api.Admin.DocumentParser.ExtractText(ms.ToArray(), file.FileName);

            if (string.IsNullOrWhiteSpace(text))
            {
                results.Add(new { file = file.FileName, ok = false, error = "No text extracted" });
                continue;
            }

            var r = await ingestion.IngestAsync(new IngestRequest
            {
                Collection   = collection,
                Source       = file.FileName,
                Title        = Path.GetFileNameWithoutExtension(file.FileName),
                Content      = text,
                Metadata     = $"{{\"filename\":\"{file.FileName}\",\"size\":{file.Length}}}",
                ChunkSize    = 1600,
                ChunkOverlap = 200
            }, ct);

            results.Add(new { file = file.FileName, ok = true, chunks = r.ChunksCreated, tokens = r.TokensEstimate });
        }
        catch (Exception ex)
        {
            results.Add(new { file = file.FileName, ok = false, error = ex.Message });
        }
    }
    return Results.Ok(results);
});

// GET /api/admin/documents?collection=xxx&page=1&pageSize=20
app.MapGet("/api/admin/documents", [Authorize] async (
    string? collection,
    int page,
    int pageSize,
    NpgsqlDataSource ds,
    CancellationToken ct) =>
{
    page     = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 5, 100);
    var offset = (page - 1) * pageSize;

    await using var conn = await ds.OpenConnectionAsync(ct);

    // Total count
    await using var countCmd = conn.CreateCommand();
    countCmd.CommandText = collection is null
        ? "SELECT COUNT(DISTINCT source) FROM kb_documents"
        : "SELECT COUNT(DISTINCT source) FROM kb_documents WHERE collection = $1";
    if (collection is not null) countCmd.Parameters.AddWithValue(collection);
    var total = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct));

    // Paginated sources
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = collection is null
        ? @"SELECT collection, source, MAX(title) as title,
                   COUNT(*) as chunks, MAX(updated_at) as updated_at
            FROM kb_documents
            GROUP BY collection, source
            ORDER BY MAX(updated_at) DESC
            LIMIT $1 OFFSET $2"
        : @"SELECT collection, source, MAX(title) as title,
                   COUNT(*) as chunks, MAX(updated_at) as updated_at
            FROM kb_documents
            WHERE collection = $3
            GROUP BY collection, source
            ORDER BY MAX(updated_at) DESC
            LIMIT $1 OFFSET $2";
    cmd.Parameters.AddWithValue(pageSize);
    cmd.Parameters.AddWithValue(offset);
    if (collection is not null) cmd.Parameters.AddWithValue(collection);

    await using var reader = await cmd.ExecuteReaderAsync(ct);
    var docs = new List<object>();
    while (await reader.ReadAsync(ct))
        docs.Add(new {
            collection = reader.GetString(0),
            source     = reader.GetString(1),
            title      = reader.GetString(2),
            chunks     = reader.GetInt64(3),
            updatedAt  = reader.GetDateTime(4)
        });

    return Results.Ok(new { total, page, pageSize, items = docs });
});

// GET /api/admin/collections
app.MapGet("/api/admin/collections", [Authorize] async (
    NpgsqlDataSource ds, CancellationToken ct) =>
{
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = @"SELECT collection, COUNT(DISTINCT source) as sources,
                               COUNT(*) as chunks, MAX(updated_at) as last_updated
                        FROM kb_documents GROUP BY collection ORDER BY collection";
    await using var r = await cmd.ExecuteReaderAsync(ct);
    var cols = new List<object>();
    while (await r.ReadAsync(ct))
        cols.Add(new {
            collection  = r.GetString(0),
            sources     = r.GetInt64(1),
            chunks      = r.GetInt64(2),
            lastUpdated = r.GetDateTime(3)
        });
    return Results.Ok(cols);
});

// DELETE /api/admin/documents/{collection}/{*source}
app.MapDelete("/api/admin/documents/{collection}/{*source}", [Authorize] async (
    string collection, string source,
    IDocumentIngestion ingestion, CancellationToken ct) =>
{
    var n = await ingestion.DeleteSourceAsync(collection, source, ct);
    return Results.Ok(new { deleted = n });
});

// =============================================================================
// ─── Admin — Usage (LiteLLM spend proxy) ─────────────────────────────────────

async Task<IResult> LiteLLMProxy(string path, IOptions<LiteLLMOptions> opts,
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

// GET /api/admin/usage/users
app.MapGet("/api/admin/usage/users", [Authorize] async (
    IOptions<LiteLLMOptions> opts, IHttpClientFactory http, CancellationToken ct) =>
    await LiteLLMProxy("/spend/users", opts, http, ct));

// GET /api/admin/usage/session-users — token stats from our own session_memories (reliable)
app.MapGet("/api/admin/usage/session-users", [Authorize] async (
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
app.MapGet("/api/admin/usage/models", [Authorize] async (
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
app.MapGet("/api/admin/usage/logs", [Authorize] async (
    int limit,
    IOptions<LiteLLMOptions> opts, IHttpClientFactory http, CancellationToken ct) =>
    await LiteLLMProxy($"/spend/logs?limit={Math.Clamp(limit, 1, 200)}", opts, http, ct));

// GET /api/admin/usage/end-users — aggregate end_user spend from LiteLLM logs
app.MapGet("/api/admin/usage/end-users", [Authorize] async (
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

// GET /api/admin/skills
app.MapGet("/api/admin/skills", [Authorize] (SkillRegistry registry) =>
    Results.Ok(registry.All.Select(kv => new { id = kv.Key, size = kv.Value.Length })));

// GET /api/admin/skills/{id}
app.MapGet("/api/admin/skills/{id}", [Authorize] (string id, SkillRegistry registry) =>
{
    if (!registry.All.ContainsKey(id)) return Results.NotFound();
    return Results.Text(registry.All[id], "text/plain; charset=utf-8");
});

// POST /api/admin/skills — upload a .md skill file
app.MapPost("/api/admin/skills", [Authorize] async (
    HttpContext http,
    SkillRegistry registry,
    CancellationToken ct) =>
{
    if (!http.Request.HasFormContentType)
        return Results.BadRequest(new { error = "multipart/form-data required" });

    var form = await http.Request.ReadFormAsync(ct);
    var results = new List<object>();

    foreach (var file in form.Files)
    {
        if (!file.FileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new { file = file.FileName, ok = false, error = "Only .md files allowed" });
            continue;
        }

        using var reader = new StreamReader(file.OpenReadStream());
        var content = (await reader.ReadToEndAsync(ct)).Trim();

        var skillId = Path.GetFileNameWithoutExtension(file.FileName);

        // Write to disk if path is known
        if (registry.SkillsPath is not null)
        {
            var filePath = Path.Combine(registry.SkillsPath, file.FileName);
            await File.WriteAllTextAsync(filePath, content, ct);
        }

        registry.Register(skillId, content);
        results.Add(new { file = file.FileName, ok = true, id = skillId });
    }

    return Results.Ok(results);
});

// DELETE /api/admin/skills/{id}
app.MapDelete("/api/admin/skills/{id}", [Authorize] (string id, SkillRegistry registry) =>
{
    if (!registry.All.ContainsKey(id))
        return Results.NotFound(new { error = $"Skill '{id}' not found" });

    // Remove from disk
    if (registry.SkillsPath is not null)
    {
        var filePath = Path.Combine(registry.SkillsPath, id + ".md");
        if (File.Exists(filePath)) File.Delete(filePath);
    }

    registry.Remove(id);
    return Results.Ok(new { deleted = id });
});

// =============================================================================
// ─── Session ─────────────────────────────────────────────────────────────────

app.MapGet("/api/session/{sessionId}", [Authorize] async (
    string sessionId,
    ISessionMemory session,
    ClaimsPrincipal user,
    CancellationToken ct) =>
{
    var userId = user.FindFirstValue(ClaimTypes.Name) ?? "anonymous";
    var msgs = await session.GetWindowAsync(sessionId, userId, maxTokens: 8000, ct: ct);
    return Results.Ok(msgs);
});

app.MapDelete("/api/session/{sessionId}", [Authorize] async (
    string sessionId,
    ISessionMemory session,
    CancellationToken ct) =>
{
    await session.ClearAsync(sessionId, ct);
    return Results.NoContent();
});

// =============================================================================
// ─── Tool Proxy (CORS bypass for agentic tool calls) ─────────────────────────

app.MapPost("/api/proxy", [Authorize] async (
    [FromBody] ProxyRequest req,
    IHttpClientFactory httpFactory,
    CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(req.Url))
        return Results.BadRequest(new { error = "url is required" });

    try
    {
        using var client = httpFactory.CreateClient("proxy");
        client.Timeout = TimeSpan.FromSeconds(15);

        var method  = new HttpMethod(req.Method?.ToUpperInvariant() ?? "GET");
        using var msg = new HttpRequestMessage(method, req.Url);
        msg.Headers.Add("User-Agent", "SET-LLM-Agent/2.0");

        if (method != HttpMethod.Get && req.Body is not null)
            msg.Content = new StringContent(req.Body, Encoding.UTF8,
                req.ContentType ?? "application/json");

        using var resp = await client.SendAsync(msg, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return Results.Text(body, "application/json", statusCode: (int)resp.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

// =============================================================================
// ─── LLM proxy (authenticated, server-side LiteLLM API key) ──────────────────

// POST /api/llm/completions — authenticated proxy to LiteLLM (keeps API key server-side)
app.MapPost("/api/llm/completions", [Authorize] [RequestSizeLimit(100 * 1024 * 1024)] async (
    HttpContext http,
    IOptions<LiteLLMOptions> litellmOpts,
    IHttpClientFactory httpFactory,
    ClaimsPrincipal principal,
    CancellationToken ct) =>
{
    using var reader = new StreamReader(http.Request.Body);
    var bodyStr = await reader.ReadToEndAsync(ct);

    var rid = Guid.NewGuid().ToString("N").Substring(0, 6);
    app.Logger.LogInformation("[VISION {Rid}] B1. /api/llm/completions hit — body={Bytes}B", rid, bodyStr.Length);

    // Inject authenticated username so LiteLLM tracks usage per user
    var username = principal.FindFirstValue(ClaimTypes.Name) ?? "anonymous";
    var hasVision = false;
    var msgCount = 0;
    var modelName = "?";
    try
    {
        var doc = System.Text.Json.JsonDocument.Parse(bodyStr);

        if (doc.RootElement.TryGetProperty("model", out var mdl))
            modelName = mdl.GetString() ?? "?";

        // Detect vision content (image_url in any message). When present, we
        // bypass LiteLLM and go straight to vLLM — LiteLLM's request reshaping
        // breaks Gemma 4's chat template (list-index-out-of-range).
        if (doc.RootElement.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
        {
            msgCount = msgs.GetArrayLength();
            foreach (var m in msgs.EnumerateArray())
                if (m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
                    foreach (var part in c.EnumerateArray())
                        if (part.TryGetProperty("type", out var t) && t.GetString() == "image_url")
                        { hasVision = true; break; }
        }

        app.Logger.LogInformation("[VISION {Rid}] B2. parsed — user={User} model={Model} msgs={Msgs} hasVision={Vision}",
            rid, username, modelName, msgCount, hasVision);

        if (!doc.RootElement.TryGetProperty("user", out _))
        {
            var obj = new Dictionary<string, object>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                obj[prop.Name] = prop.Value.Clone();
            obj["user"] = username;
            bodyStr = System.Text.Json.JsonSerializer.Serialize(obj);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "[VISION {Rid}] B2. JSON parse failed", rid);
    }

    var opts = litellmOpts.Value;
    // Vision requests: bypass LiteLLM, go directly to vLLM (Gemma chat model)
    var targetUrl = hasVision
        ? "http://localhost:8000/v1/chat/completions"
        : opts.BaseUrl.TrimEnd('/') + "/v1/chat/completions";
    var bearerKey = hasVision
        ? (Environment.GetEnvironmentVariable("LLM_VLLM_KEY") ?? opts.ApiKey)
        : opts.ApiKey;

    if (hasVision)
    {
        // For direct vLLM, rewrite model name (chat → gemma4-26b)
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(bodyStr);
            var obj = new Dictionary<string, object>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                obj[prop.Name] = prop.Value.Clone();
            obj["model"] = "gemma4-26b";
            bodyStr = System.Text.Json.JsonSerializer.Serialize(obj);
        }
        catch { /* leave unchanged */ }
        app.Logger.LogInformation("[VISION {Rid}] B3. routing → DIRECT vLLM {Url} (bypass LiteLLM)", rid, targetUrl);
    }
    else
    {
        app.Logger.LogInformation("[VISION {Rid}] B3. routing → LiteLLM {Url}", rid, targetUrl);
    }

    using var client = httpFactory.CreateClient("proxy");
    client.Timeout = TimeSpan.FromSeconds(600);

    using var req = new HttpRequestMessage(HttpMethod.Post, targetUrl);
    req.Content = new StringContent(bodyStr, Encoding.UTF8, "application/json");
    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerKey);
    if (!hasVision)
        req.Headers.TryAddWithoutValidation("x-litellm-user", username);  // end-user tracking

    app.Logger.LogInformation("[VISION {Rid}] B4. sending request to {Url}", rid, targetUrl);
    using var resp = await client.SendAsync(req,
        HttpCompletionOption.ResponseHeadersRead, ct);
    app.Logger.LogInformation("[VISION {Rid}] B5. response from upstream — status={Status}", rid, (int)resp.StatusCode);

    // ── Warming-up detection ──────────────────────────────────────────────────
    // LiteLLM returns 500 with "Connection error" when the vLLM container is
    // still loading the model. Convert this to a 503 with a friendly message
    // so the frontend can show a warning instead of a hard error.
    if (!resp.IsSuccessStatusCode)
    {
        var errBody = await resp.Content.ReadAsStringAsync(ct);
        app.Logger.LogWarning("[VISION {Rid}] B6. upstream error body={Body}",
            rid, errBody[..Math.Min(400, errBody.Length)]);
        var isWarmingUp =
            errBody.Contains("Connection error", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("No fallback model group", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("ServiceUnavailable", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("health check", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("list index out of range", StringComparison.OrdinalIgnoreCase);

        if (isWarmingUp)
        {
            app.Logger.LogWarning("[VISION {Rid}] B7. returning 503 warming_up to client", rid);
            return Results.Json(
                new { error = "warming_up", message = "Model henüz yükleniyor, lütfen birkaç saniye sonra tekrar deneyin." },
                statusCode: 503);
        }

        // Other non-success: proxy as-is
        http.Response.StatusCode = (int)resp.StatusCode;
        foreach (var h in resp.Headers)
            if (!h.Key.StartsWith("Transfer", StringComparison.OrdinalIgnoreCase))
                http.Response.Headers[h.Key] = h.Value.ToArray();
        foreach (var h in resp.Content.Headers)
            http.Response.Headers[h.Key] = h.Value.ToArray();
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(errBody, ct);
        return Results.Empty;
    }

    app.Logger.LogInformation("[VISION {Rid}] B7. streaming success response to client", rid);
    http.Response.StatusCode = (int)resp.StatusCode;
    foreach (var h in resp.Headers)
        if (!h.Key.StartsWith("Transfer", StringComparison.OrdinalIgnoreCase))
            http.Response.Headers[h.Key] = h.Value.ToArray();
    foreach (var h in resp.Content.Headers)
        http.Response.Headers[h.Key] = h.Value.ToArray();

    await resp.Content.CopyToAsync(http.Response.Body, ct);
    app.Logger.LogInformation("[VISION {Rid}] B8. response fully streamed", rid);
    return Results.Empty;
});

// =============================================================================
// ─── Error log ───────────────────────────────────────────────────────────────

app.MapPost("/api/log/error", [Authorize] async (
    [FromBody] ErrorLogRequest req,
    ClaimsPrincipal user,
    IWebHostEnvironment env) =>
{
    var logDir  = Path.Combine(env.ContentRootPath, "Logs");
    Directory.CreateDirectory(logDir);
    var logPath = Path.Combine(logDir, $"error_{DateTime.Now:yyyy-MM-dd}.log");
    var line    = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] USER: {user.FindFirstValue(ClaimTypes.Name)}\n{req.Message}\n{new string('-', 40)}\n";
    await File.AppendAllTextAsync(logPath, line);
    return Results.Ok();
});

// SPA fallback — all unknown routes → index.html (React router)
app.MapFallbackToFile("index.html");

app.Run();

// =============================================================================
// DTOs
// =============================================================================

public sealed record LoginRequest(string Username, string Password, string Domain);

public sealed record ApiChatRequest(
    string    SessionId,
    string    AgentId,
    string    SkillName,
    string    Message,
    string[]? Collections    = null,
    string?   MetadataFilter = null,
    int?      TokenBudget    = null);

public sealed record ApiChatResponse(
    string Content,
    string SessionId,
    int    KbHits,
    int    MemoryHits,
    int    EstTokens);

public sealed record ApiIngestRequest(
    string  Collection,
    string  Source,
    string  Title,
    string  Content,
    string? Metadata     = null,
    int?    ChunkSize    = null,
    int?    ChunkOverlap = null);

public sealed record ProxyRequest(
    string?  Url,
    string?  Method,
    string?  Body,
    string?  ContentType);

public sealed record ErrorLogRequest(string Message);
