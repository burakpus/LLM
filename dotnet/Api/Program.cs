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
    CancellationToken ct) =>
{
    using var reader = new StreamReader(http.Request.Body);
    var bodyStr = await reader.ReadToEndAsync(ct);

    var opts = litellmOpts.Value;
    var targetUrl = opts.BaseUrl.TrimEnd('/') + "/v1/chat/completions";

    using var client = httpFactory.CreateClient("proxy");
    client.Timeout = TimeSpan.FromSeconds(600);

    using var req = new HttpRequestMessage(HttpMethod.Post, targetUrl);
    req.Content = new StringContent(bodyStr, Encoding.UTF8, "application/json");
    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.ApiKey);

    using var resp = await client.SendAsync(req,
        HttpCompletionOption.ResponseHeadersRead, ct);

    http.Response.StatusCode = (int)resp.StatusCode;
    foreach (var h in resp.Headers)
        if (!h.Key.StartsWith("Transfer", StringComparison.OrdinalIgnoreCase))
            http.Response.Headers[h.Key] = h.Value.ToArray();
    foreach (var h in resp.Content.Headers)
        http.Response.Headers[h.Key] = h.Value.ToArray();

    await resp.Content.CopyToAsync(http.Response.Body, ct);
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
