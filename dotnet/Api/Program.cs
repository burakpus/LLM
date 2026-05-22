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

// ── In-process cache (embedding results, etc.) ────────────────────────────────
services.AddMemoryCache(o => o.SizeLimit = 2000);

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

services.AddAuthorization(o =>
{
    // "AdminOnly" policy: JWT must contain isAdmin=true claim
    o.AddPolicy("AdminOnly", p =>
        p.RequireAuthenticatedUser()
         .RequireClaim(AppClaims.IsAdmin, "true"));
});

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

// =============================================================================
// ─── Startup: ensure prompt_templates table exists ───────────────────────────
{
    await using var scope = app.Services.CreateAsyncScope();
    var ds0 = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
    await using var c0  = await ds0.OpenConnectionAsync();
    await using var cmd0 = c0.CreateCommand();
    cmd0.CommandText = @"
        CREATE TABLE IF NOT EXISTS skill_examples (
            id                SERIAL PRIMARY KEY,
            skill_id          VARCHAR(200) NOT NULL,
            user_message      TEXT         NOT NULL,
            assistant_message TEXT         NOT NULL,
            sort_order        INTEGER      NOT NULL DEFAULT 0,
            created_at        TIMESTAMPTZ  NOT NULL DEFAULT NOW()
        );
        CREATE TABLE IF NOT EXISTS message_ratings (
            id          SERIAL PRIMARY KEY,
            username    VARCHAR(100) NOT NULL,
            conv_id     VARCHAR(100) NOT NULL,
            message_id  VARCHAR(100) NOT NULL,
            rating      SMALLINT     NOT NULL,
            model       VARCHAR(100),
            created_at  TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            UNIQUE (username, message_id)
        );
        CREATE TABLE IF NOT EXISTS prompt_templates (
            id          SERIAL PRIMARY KEY,
            name        VARCHAR(200) NOT NULL,
            content     TEXT         NOT NULL,
            variables   TEXT         NOT NULL DEFAULT '[]',
            collection  VARCHAR(100) NOT NULL DEFAULT '',
            created_by  VARCHAR(100) NOT NULL DEFAULT '',
            created_at  TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            updated_at  TIMESTAMPTZ  NOT NULL DEFAULT NOW()
        );
        CREATE TABLE IF NOT EXISTS activity_log (
            id          SERIAL PRIMARY KEY,
            username    VARCHAR(100) NOT NULL,
            action      VARCHAR(100) NOT NULL,
            target      VARCHAR(500) NOT NULL DEFAULT '',
            details     TEXT         NOT NULL DEFAULT '',
            created_at  TIMESTAMPTZ  NOT NULL DEFAULT NOW()
        )";
    await cmd0.ExecuteNonQueryAsync();
}

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

    var isAdmin = ldap.IsAdmin(req.Domain, req.Username, req.Password);
    var groups  = ldap.GetUserGroups(req.Domain, req.Username, req.Password);
    var token   = jwt.Generate(req.Username, req.Domain, isAdmin, groups);
    return Results.Ok(token);
});

// GET /api/auth/groups — debug: shows user's actual AD group CNs (authenticated)
app.MapGet("/api/auth/groups", [Authorize] (
    ClaimsPrincipal user,
    IOptions<LdapOptions> ldapOpts) =>
{
    var opts     = ldapOpts.Value;
    var username = user.FindFirstValue(ClaimTypes.Name) ?? "";
    var domain   = user.FindFirstValue("domain") ?? "";

    if (opts.Bypass)
        return Results.Ok(new { username, domain, bypass = true, groups = Array.Empty<string>() });

    if (!opts.Domains.TryGetValue(domain.ToUpperInvariant(), out var cfg))
        return Results.BadRequest(new { error = $"Domain not configured: {domain}" });

    // Re-auth requires password — not available here, so we use a read-only bind attempt
    // by checking the Authorization header for a Basic token if available
    return Results.Ok(new {
        username,
        domain,
        adminUserSet  = opts.AdminUserSet.ToArray(),
        adminGroupSet = opts.AdminGroupSet.ToArray(),
        note = "Password not available after login — use /api/auth/debug-bind to test with credentials"
    });
});

// POST /api/auth/debug-bind — test LDAP group lookup with credentials (debug only)
app.MapPost("/api/auth/debug-bind", [Authorize] (
    [FromBody] LoginRequest req,
    IOptions<LdapOptions> ldapOpts) =>
{
    var opts = ldapOpts.Value;
    if (!opts.Domains.TryGetValue(req.Domain.ToUpperInvariant(), out var cfg))
        return Results.BadRequest(new { error = $"Domain not configured: {req.Domain}" });

    try
    {
        var host   = new Uri(cfg.Path.Replace("LDAP://", "ldap://", StringComparison.OrdinalIgnoreCase)).Host;
        var baseDn = string.Join(",", host.Split('.').Select(p => $"DC={p}"));

        using var conn = new Novell.Directory.Ldap.LdapConnection();
        conn.Connect(host, 389);
        conn.Bind($"{cfg.Domain}\\{req.Username}", req.Password);

        var results = conn.Search(baseDn, Novell.Directory.Ldap.LdapConnection.ScopeSub,
            $"(sAMAccountName={req.Username})", new[] { "memberOf" }, false);

        var rawDns = new List<string>();
        bool found = false;
        while (results.HasMore())
        {
            Novell.Directory.Ldap.LdapEntry entry;
            try { entry = results.Next(); }
            catch (Novell.Directory.Ldap.LdapException) { break; }

            found = true;
            var attr = entry.GetAttributeSet().GetAttribute("memberOf");
            if (attr != null)
                rawDns.AddRange(attr.StringValueArray);
        }
        conn.Disconnect();

        if (!found)
            return Results.Ok(new { found = false, baseDn, entries = 0 });

        var cns = rawDns.Select(dn =>
            dn.Split(',').FirstOrDefault(p => p.TrimStart().StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                ?.Substring(3).Trim() ?? "").ToArray();

        return Results.Ok(new {
            found    = true,
            baseDn,
            rawDns   = rawDns.ToArray(),
            cns,
            adminGroupSet = opts.AdminGroupSet.ToArray(),
            isAdmin  = cns.Any(cn => opts.AdminGroupSet.Contains(cn))
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { error = ex.Message });
    }
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


// =============================================================================
// ─── File Extract ─────────────────────────────────────────────────────────────

// POST /api/files/extract — parse uploaded document and return plain text (for chat injection)
// Supported: .txt, .md, .csv, .pdf, .docx, .xlsx
app.MapPost("/api/files/extract", [Authorize] async (HttpContext http, CancellationToken ct) =>
{
    if (!http.Request.HasFormContentType)
        return Results.BadRequest(new { error = "multipart/form-data required" });

    var form = await http.Request.ReadFormAsync(ct);
    var file = form.Files.FirstOrDefault();
    if (file == null)
        return Results.BadRequest(new { error = "No file uploaded" });

    const int MaxChars = 16_000; // ~4000 tokens

    using var ms = new MemoryStream();
    await file.CopyToAsync(ms, ct);

    string text;
    try
    {
        text = SetYazilim.Llm.Api.Admin.DocumentParser.ExtractText(ms.ToArray(), file.FileName);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Dosya okunamadı: {ex.Message}" });
    }

    var truncated = text.Length > MaxChars;
    if (truncated) text = text[..MaxChars];

    return Results.Ok(new { filename = file.FileName, text, truncated });
});

// =============================================================================
// ─── Activity Log ─────────────────────────────────────────────────────────────

// Helper — fire-and-forget activity insert (non-blocking, swallows errors)
async Task LogActivity(NpgsqlDataSource ds, string username, string action, string target, string details = "")
{
    try
    {
        await using var conn = await ds.OpenConnectionAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO activity_log (username, action, target, details) VALUES ($1,$2,$3,$4)";
        cmd.Parameters.AddWithValue(username);
        cmd.Parameters.AddWithValue(action);
        cmd.Parameters.AddWithValue(target);
        cmd.Parameters.AddWithValue(details);
        await cmd.ExecuteNonQueryAsync();
    }
    catch { /* non-critical, never fail the request */ }
}

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

// =============================================================================
// ─── Ratings ─────────────────────────────────────────────────────────────────

// POST /api/ratings — submit or update a rating (all authenticated users)
app.MapPost("/api/ratings", [Authorize] async (
    [FromBody] RatingRequest req,
    ClaimsPrincipal user,
    NpgsqlDataSource ds,
    CancellationToken ct) =>
{
    if (req.Rating != 1 && req.Rating != -1)
        return Results.BadRequest(new { error = "Rating must be 1 or -1" });

    var username = user.FindFirstValue(ClaimTypes.Name) ?? "anonymous";
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = @"
        INSERT INTO message_ratings (username, conv_id, message_id, rating, model)
        VALUES ($1, $2, $3, $4, $5)
        ON CONFLICT (username, message_id)
        DO UPDATE SET rating = $4, model = $5";
    cmd.Parameters.AddWithValue(username);
    cmd.Parameters.AddWithValue(req.ConvId);
    cmd.Parameters.AddWithValue(req.MessageId);
    cmd.Parameters.AddWithValue((short)req.Rating);
    cmd.Parameters.AddWithValue(string.IsNullOrEmpty(req.Model) ? (object)DBNull.Value : req.Model);
    await cmd.ExecuteNonQueryAsync(ct);
    return Results.Ok(new { ok = true });
});

// GET /api/admin/ratings/stats — rating statistics (admin only)
app.MapGet("/api/admin/ratings/stats", [Authorize("AdminOnly")] async (
    NpgsqlDataSource ds, CancellationToken ct) =>
{
    await using var conn = await ds.OpenConnectionAsync(ct);

    // Overall totals
    await using var c1 = conn.CreateCommand();
    c1.CommandText = @"SELECT COUNT(*),
                              COALESCE(SUM(CASE WHEN rating=1  THEN 1 ELSE 0 END),0),
                              COALESCE(SUM(CASE WHEN rating=-1 THEN 1 ELSE 0 END),0)
                       FROM message_ratings";
    await using var r1 = await c1.ExecuteReaderAsync(ct);
    await r1.ReadAsync(ct);
    var total = r1.GetInt64(0); var ups = r1.GetInt64(1); var downs = r1.GetInt64(2);
    await r1.CloseAsync();

    // By model
    await using var c2 = conn.CreateCommand();
    c2.CommandText = @"SELECT COALESCE(model,'unknown'),
                              COUNT(*),
                              COALESCE(SUM(CASE WHEN rating=1  THEN 1 ELSE 0 END),0),
                              COALESCE(SUM(CASE WHEN rating=-1 THEN 1 ELSE 0 END),0)
                       FROM message_ratings
                       GROUP BY model ORDER BY COUNT(*) DESC";
    await using var r2 = await c2.ExecuteReaderAsync(ct);
    var byModel = new List<object>();
    while (await r2.ReadAsync(ct))
        byModel.Add(new { model = r2.GetString(0), total = r2.GetInt64(1), ups = r2.GetInt64(2), downs = r2.GetInt64(3) });
    await r2.CloseAsync();

    // Recent 20
    await using var c3 = conn.CreateCommand();
    c3.CommandText = @"SELECT username, rating, COALESCE(model,'?'), created_at
                       FROM message_ratings ORDER BY created_at DESC LIMIT 20";
    await using var r3 = await c3.ExecuteReaderAsync(ct);
    var recent = new List<object>();
    while (await r3.ReadAsync(ct))
        recent.Add(new { username = r3.GetString(0), rating = (int)r3.GetInt16(1), model = r3.GetString(2), createdAt = r3.GetDateTime(3) });

    return Results.Ok(new { total, ups, downs, byModel, recent });
});

// =============================================================================
// ─── Prompt Templates ────────────────────────────────────────────────────────

// Extract {{variable}} names from template content
string[] ExtractTemplateVars(string content) =>
    System.Text.RegularExpressions.Regex.Matches(content, @"\{\{(\w+)\}\}")
        .Cast<System.Text.RegularExpressions.Match>()
        .Select(m => m.Groups[1].Value)
        .Distinct()
        .ToArray();

// GET /api/templates — list all (all authenticated users, for chat slash picker)
app.MapGet("/api/templates", [Authorize] async (NpgsqlDataSource ds, CancellationToken ct) =>
{
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = @"SELECT id, name, content, variables, collection, created_by, created_at
                        FROM prompt_templates ORDER BY collection, name";
    await using var r = await cmd.ExecuteReaderAsync(ct);
    var rows = new List<object>();
    while (await r.ReadAsync(ct))
        rows.Add(new {
            id         = r.GetInt32(0),
            name       = r.GetString(1),
            content    = r.GetString(2),
            variables  = JsonSerializer.Deserialize<string[]>(r.GetString(3)) ?? Array.Empty<string>(),
            collection = r.GetString(4),
            createdBy  = r.GetString(5),
            createdAt  = r.GetDateTime(6),
        });
    return Results.Ok(rows);
});

// POST /api/admin/templates — create (admin only)
app.MapPost("/api/admin/templates", [Authorize("AdminOnly")] async (
    [FromBody] TemplateUpsertRequest req,
    ClaimsPrincipal user,
    NpgsqlDataSource ds,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Content))
        return Results.BadRequest(new { error = "Name and Content are required" });

    var vars     = ExtractTemplateVars(req.Content);
    var varsJson = JsonSerializer.Serialize(vars);
    var username = user.FindFirstValue(ClaimTypes.Name) ?? "admin";

    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = @"INSERT INTO prompt_templates (name, content, variables, collection, created_by)
                        VALUES ($1, $2, $3, $4, $5) RETURNING id";
    cmd.Parameters.AddWithValue(req.Name.Trim());
    cmd.Parameters.AddWithValue(req.Content);
    cmd.Parameters.AddWithValue(varsJson);
    cmd.Parameters.AddWithValue(req.Collection?.Trim() ?? "");
    cmd.Parameters.AddWithValue(username);
    var id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    _ = LogActivity(ds, username, "template.create", req.Name.Trim(), $"collection={req.Collection ?? ""}");
    return Results.Ok(new { id, name = req.Name.Trim(), variables = vars });
});

// PUT /api/admin/templates/{id} — update (admin only)
app.MapPut("/api/admin/templates/{id:int}", [Authorize("AdminOnly")] async (
    int id,
    [FromBody] TemplateUpsertRequest req,
    ClaimsPrincipal user,
    NpgsqlDataSource ds,
    CancellationToken ct) =>
{
    var vars     = ExtractTemplateVars(req.Content);
    var varsJson = JsonSerializer.Serialize(vars);
    var username = user.FindFirstValue(ClaimTypes.Name) ?? "admin";

    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = @"UPDATE prompt_templates
                        SET name=$1, content=$2, variables=$3, collection=$4, updated_at=NOW()
                        WHERE id=$5";
    cmd.Parameters.AddWithValue(req.Name.Trim());
    cmd.Parameters.AddWithValue(req.Content);
    cmd.Parameters.AddWithValue(varsJson);
    cmd.Parameters.AddWithValue(req.Collection?.Trim() ?? "");
    cmd.Parameters.AddWithValue(id);
    var rows = await cmd.ExecuteNonQueryAsync(ct);
    if (rows > 0) _ = LogActivity(ds, username, "template.update", req.Name.Trim());
    return rows == 0 ? Results.NotFound() : Results.Ok(new { id, variables = vars });
});

// DELETE /api/admin/templates/{id} — delete (admin only)
app.MapDelete("/api/admin/templates/{id:int}", [Authorize("AdminOnly")] async (
    int id, ClaimsPrincipal user, NpgsqlDataSource ds, CancellationToken ct) =>
{
    var username = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM prompt_templates WHERE id=$1";
    cmd.Parameters.AddWithValue(id);
    await cmd.ExecuteNonQueryAsync(ct);
    _ = LogActivity(ds, username, "template.delete", $"id={id}");
    return Results.NoContent();
});

// =============================================================================
// ─── Skills ──────────────────────────────────────────────────────────────────

// GET /api/skills — list skills with metadata
// GET /api/models/capabilities — model feature matrix for frontend routing decisions
app.MapGet("/api/models/capabilities", [Authorize] () => Results.Ok(new Dictionary<string, object>
{
    ["chat"]   = new { supportsVision = true,  supportsTools = false, contextWindow = 32768,
                       description = "Gemma 4 26B — genel asistan, doküman analizi, görsel anlama" },
    ["code"]   = new { supportsVision = false, supportsTools = true,  contextWindow = 32768,
                       description = "Qwen3.6 27B — kod üretimi, ajansal görevler, tool kullanımı" },
    ["reason"] = new { supportsVision = false, supportsTools = true,  contextWindow = 32768,
                       description = "GPT-OSS 120B — derin muhakeme, agent orchestration" },
    ["embed"]  = new { supportsVision = false, supportsTools = false, contextWindow = 2048,
                       description = "nomic-embed-text-v1.5 — 768 boyut embedding" },
}));

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
// ─── Skill Examples (Few-Shot) ────────────────────────────────────────────────

// GET /api/skills/{id}/examples — list examples (all authenticated users)
app.MapGet("/api/skills/{id}/examples", [Authorize] async (
    string id, NpgsqlDataSource ds, CancellationToken ct) =>
{
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = @"SELECT id, user_message, assistant_message, sort_order
                        FROM skill_examples WHERE skill_id=$1 ORDER BY sort_order, id";
    cmd.Parameters.AddWithValue(id);
    await using var r = await cmd.ExecuteReaderAsync(ct);
    var rows = new List<object>();
    while (await r.ReadAsync(ct))
        rows.Add(new { id = r.GetInt32(0), userMessage = r.GetString(1), assistantMessage = r.GetString(2), sortOrder = r.GetInt32(3) });
    return Results.Ok(rows);
});

// POST /api/admin/skills/{id}/examples — add example (admin only)
app.MapPost("/api/admin/skills/{id}/examples", [Authorize("AdminOnly")] async (
    string id, [FromBody] SkillExampleRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
{
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = @"INSERT INTO skill_examples (skill_id, user_message, assistant_message, sort_order)
                        VALUES ($1, $2, $3, (SELECT COALESCE(MAX(sort_order)+1, 0) FROM skill_examples WHERE skill_id=$1))
                        RETURNING id, sort_order";
    cmd.Parameters.AddWithValue(id);
    cmd.Parameters.AddWithValue(req.UserMessage);
    cmd.Parameters.AddWithValue(req.AssistantMessage);
    await using var r = await cmd.ExecuteReaderAsync(ct);
    await r.ReadAsync(ct);
    return Results.Ok(new { id = r.GetInt32(0), sortOrder = r.GetInt32(1) });
});

// PUT /api/admin/skills/{id}/examples/{exId} — update example (admin only)
app.MapPut("/api/admin/skills/{id}/examples/{exId:int}", [Authorize("AdminOnly")] async (
    string id, int exId, [FromBody] SkillExampleRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
{
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = @"UPDATE skill_examples SET user_message=$1, assistant_message=$2
                        WHERE id=$3 AND skill_id=$4";
    cmd.Parameters.AddWithValue(req.UserMessage);
    cmd.Parameters.AddWithValue(req.AssistantMessage);
    cmd.Parameters.AddWithValue(exId);
    cmd.Parameters.AddWithValue(id);
    var rows = await cmd.ExecuteNonQueryAsync(ct);
    return rows == 0 ? Results.NotFound() : Results.Ok(new { ok = true });
});

// DELETE /api/admin/skills/{id}/examples/{exId} — delete example (admin only)
app.MapDelete("/api/admin/skills/{id}/examples/{exId:int}", [Authorize("AdminOnly")] async (
    string id, int exId, NpgsqlDataSource ds, CancellationToken ct) =>
{
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM skill_examples WHERE id=$1 AND skill_id=$2";
    cmd.Parameters.AddWithValue(exId);
    cmd.Parameters.AddWithValue(id);
    await cmd.ExecuteNonQueryAsync(ct);
    return Results.NoContent();
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
app.MapPost("/api/admin/upload", [Authorize("AdminOnly")] async (
    HttpContext http,
    IDocumentIngestion ingestion,
    ClaimsPrincipal user,
    NpgsqlDataSource ds,
    CancellationToken ct) =>
{
    if (!http.Request.HasFormContentType)
        return Results.BadRequest(new { error = "multipart/form-data required" });

    var form       = await http.Request.ReadFormAsync(ct);
    var collection = form["collection"].FirstOrDefault() ?? "default";
    var username   = user.FindFirstValue(ClaimTypes.Name) ?? "unknown";
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
            _ = LogActivity(ds, username, "document.upload", file.FileName, $"collection={collection} chunks={r.ChunksCreated}");
        }
        catch (Exception ex)
        {
            results.Add(new { file = file.FileName, ok = false, error = ex.Message });
        }
    }
    return Results.Ok(results);
});

// GET /api/admin/documents?collection=xxx&page=1&pageSize=20
app.MapGet("/api/admin/documents", [Authorize("AdminOnly")] async (
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
app.MapGet("/api/admin/collections", [Authorize("AdminOnly")] async (
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
app.MapDelete("/api/admin/documents/{collection}/{*source}", [Authorize("AdminOnly")] async (
    string collection, string source,
    IDocumentIngestion ingestion,
    ClaimsPrincipal user,
    NpgsqlDataSource ds,
    CancellationToken ct) =>
{
    var n        = await ingestion.DeleteSourceAsync(collection, source, ct);
    var username = user.FindFirstValue(ClaimTypes.Name) ?? "unknown";
    _ = LogActivity(ds, username, "document.delete", source, $"collection={collection} chunks={n}");
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
app.MapGet("/api/admin/usage/logs", [Authorize("AdminOnly")] async (
    int limit,
    IOptions<LiteLLMOptions> opts, IHttpClientFactory http, CancellationToken ct) =>
    await LiteLLMProxy($"/spend/logs?limit={Math.Clamp(limit, 1, 200)}", opts, http, ct));

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

// GET /api/admin/skills
app.MapGet("/api/admin/skills", [Authorize("AdminOnly")] (SkillRegistry registry) =>
    Results.Ok(registry.All.Select(kv => new { id = kv.Key, size = kv.Value.Length })));

// GET /api/admin/skills/{id}
app.MapGet("/api/admin/skills/{id}", [Authorize("AdminOnly")] (string id, SkillRegistry registry) =>
{
    if (!registry.All.ContainsKey(id)) return Results.NotFound();
    return Results.Text(registry.All[id], "text/plain; charset=utf-8");
});

// POST /api/admin/skills — upload a .md skill file
app.MapPost("/api/admin/skills", [Authorize("AdminOnly")] async (
    HttpContext http,
    SkillRegistry registry,
    ClaimsPrincipal user,
    NpgsqlDataSource ds,
    CancellationToken ct) =>
{
    if (!http.Request.HasFormContentType)
        return Results.BadRequest(new { error = "multipart/form-data required" });

    var form     = await http.Request.ReadFormAsync(ct);
    var username = user.FindFirstValue(ClaimTypes.Name) ?? "unknown";
    var results  = new List<object>();

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

        if (registry.SkillsPath is not null)
        {
            var filePath = Path.Combine(registry.SkillsPath, file.FileName);
            await File.WriteAllTextAsync(filePath, content, ct);
        }

        registry.Register(skillId, content);
        results.Add(new { file = file.FileName, ok = true, id = skillId });
        _ = LogActivity(ds, username, "skill.upload", skillId, $"size={file.Length}");
    }

    return Results.Ok(results);
});

// DELETE /api/admin/skills/{id}
app.MapDelete("/api/admin/skills/{id}", [Authorize("AdminOnly")] (
    string id, SkillRegistry registry,
    ClaimsPrincipal user, NpgsqlDataSource ds) =>
{
    if (!registry.All.ContainsKey(id))
        return Results.NotFound(new { error = $"Skill '{id}' not found" });

    if (registry.SkillsPath is not null)
    {
        var filePath = Path.Combine(registry.SkillsPath, id + ".md");
        if (File.Exists(filePath)) File.Delete(filePath);
    }

    registry.Remove(id);
    var username = user.FindFirstValue(ClaimTypes.Name) ?? "unknown";
    _ = LogActivity(ds, username, "skill.delete", id);
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

    var debug = Environment.GetEnvironmentVariable("LLM_DEBUG_VISION") == "1";
    var rid   = debug ? Guid.NewGuid().ToString("N").Substring(0, 6) : "";
    if (debug) app.Logger.LogInformation("[VISION {Rid}] B1. /api/llm/completions hit — body={Bytes}B", rid, bodyStr.Length);

    // Inject authenticated username so LiteLLM tracks usage per user
    var username = principal.FindFirstValue(ClaimTypes.Name) ?? "anonymous";
    try
    {
        var doc = System.Text.Json.JsonDocument.Parse(bodyStr);
        if (debug)
        {
            var modelName = doc.RootElement.TryGetProperty("model", out var mdl) ? mdl.GetString() ?? "?" : "?";
            var msgCount  = doc.RootElement.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array
                ? msgs.GetArrayLength() : 0;
            var hasVision = false;
            if (doc.RootElement.TryGetProperty("messages", out var ms) && ms.ValueKind == JsonValueKind.Array)
                foreach (var m in ms.EnumerateArray())
                    if (m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
                        foreach (var part in c.EnumerateArray())
                            if (part.TryGetProperty("type", out var t) && t.GetString() == "image_url")
                            { hasVision = true; break; }
            app.Logger.LogInformation("[VISION {Rid}] B2. parsed — user={User} model={Model} msgs={Msgs} hasVision={Vision}",
                rid, username, modelName, msgCount, hasVision);
        }

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
        if (debug) app.Logger.LogError(ex, "[VISION {Rid}] B2. JSON parse failed", rid);
    }

    var opts      = litellmOpts.Value;
    var targetUrl = opts.BaseUrl.TrimEnd('/') + "/v1/chat/completions";
    var bearerKey = opts.ApiKey;

    using var client = httpFactory.CreateClient("proxy");
    client.Timeout = TimeSpan.FromSeconds(600);

    using var req = new HttpRequestMessage(HttpMethod.Post, targetUrl);
    req.Content = new StringContent(bodyStr, Encoding.UTF8, "application/json");
    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerKey);
    req.Headers.TryAddWithoutValidation("x-litellm-user", username);

    using var resp = await client.SendAsync(req,
        HttpCompletionOption.ResponseHeadersRead, ct);
    if (debug) app.Logger.LogInformation("[VISION {Rid}] B5. upstream status={Status}", rid, (int)resp.StatusCode);

    // ── Warming-up detection ──────────────────────────────────────────────────
    // LiteLLM returns 500 with "Connection error" when the vLLM container is
    // still loading the model. Convert this to a 503 with a friendly message
    // so the frontend can show a warning instead of a hard error.
    if (!resp.IsSuccessStatusCode)
    {
        var errBody = await resp.Content.ReadAsStringAsync(ct);
        var isWarmingUp =
            errBody.Contains("Connection error", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("No fallback model group", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("ServiceUnavailable", StringComparison.OrdinalIgnoreCase) ||
            errBody.Contains("health check", StringComparison.OrdinalIgnoreCase);

        if (isWarmingUp)
        {
            app.Logger.LogWarning("Model warming up – upstream error for user {User}: {Error}",
                username, errBody[..Math.Min(300, errBody.Length)]);
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

    http.Response.StatusCode = (int)resp.StatusCode;
    foreach (var h in resp.Headers)
        if (!h.Key.StartsWith("Transfer", StringComparison.OrdinalIgnoreCase))
            http.Response.Headers[h.Key] = h.Value.ToArray();
    foreach (var h in resp.Content.Headers)
        http.Response.Headers[h.Key] = h.Value.ToArray();

    await resp.Content.CopyToAsync(http.Response.Body, ct);
    if (debug) app.Logger.LogInformation("[VISION {Rid}] B6. response streamed", rid);
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

// =============================================================================
// ─── Project file management ──────────────────────────────────────────────────
// Files stored at ~/llm-projects/{userId}/{projectId}/  (sandboxed per user)

string ProjectRoot(string userId, string projectId)
{
    var home  = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var root  = Path.Combine(home, "llm-projects",
        string.Join("_", userId.Split(Path.GetInvalidFileNameChars())),
        string.Join("_", projectId.Split(Path.GetInvalidFileNameChars())));
    Directory.CreateDirectory(root);
    return root;
}

string SafeJoin(string root, string relPath)
{
    var full = Path.GetFullPath(Path.Combine(root, relPath.TrimStart('/')));
    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        throw new UnauthorizedAccessException("Path traversal detected");
    return full;
}

// GET /api/projects/{projectId}/files
app.MapGet("/api/projects/{projectId}/files", [Authorize] (
    string projectId, ClaimsPrincipal principal) =>
{
    var root  = ProjectRoot(principal.FindFirstValue(ClaimTypes.Name) ?? "anon", projectId);
    var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Select(f => new {
            path      = Path.GetRelativePath(root, f).Replace('\\', '/'),
            updatedAt = File.GetLastWriteTimeUtc(f),
            size      = new FileInfo(f).Length,
        })
        .OrderBy(f => f.path);
    return Results.Ok(files);
});

// GET /api/projects/{projectId}/files/{*path}
app.MapGet("/api/projects/{projectId}/files/{*path}", [Authorize] async (
    string projectId, string path, ClaimsPrincipal principal, CancellationToken ct) =>
{
    var root = ProjectRoot(principal.FindFirstValue(ClaimTypes.Name) ?? "anon", projectId);
    var full = SafeJoin(root, path);
    if (!File.Exists(full)) return Results.NotFound();
    var content = await File.ReadAllTextAsync(full, ct);
    return Results.Ok(new { path, content });
});

// PUT /api/projects/{projectId}/files/{*path}
app.MapPut("/api/projects/{projectId}/files/{*path}", [Authorize] async (
    string projectId, string path, HttpContext http,
    ClaimsPrincipal principal, CancellationToken ct) =>
{
    var root  = ProjectRoot(principal.FindFirstValue(ClaimTypes.Name) ?? "anon", projectId);
    var full  = SafeJoin(root, path);
    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    using var reader = new StreamReader(http.Request.Body);
    var body    = await reader.ReadToEndAsync(ct);
    var payload = System.Text.Json.JsonDocument.Parse(body);
    var content = payload.RootElement.GetProperty("content").GetString() ?? "";
    await File.WriteAllTextAsync(full, content, ct);
    return Results.Ok(new { path, updatedAt = File.GetLastWriteTimeUtc(full) });
});

// DELETE /api/projects/{projectId}/files/{*path}
app.MapDelete("/api/projects/{projectId}/files/{*path}", [Authorize] (
    string projectId, string path, ClaimsPrincipal principal) =>
{
    var root = ProjectRoot(principal.FindFirstValue(ClaimTypes.Name) ?? "anon", projectId);
    var full = SafeJoin(root, path);
    if (File.Exists(full)) File.Delete(full);
    return Results.NoContent();
});

// DELETE /api/projects/{projectId} — delete entire project directory
app.MapDelete("/api/projects/{projectId}", [Authorize] (
    string projectId, ClaimsPrincipal principal) =>
{
    var userId = principal.FindFirstValue(ClaimTypes.Name) ?? "anon";
    var home   = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var root   = Path.Combine(home, "llm-projects",
        string.Join("_", userId.Split(Path.GetInvalidFileNameChars())),
        string.Join("_", projectId.Split(Path.GetInvalidFileNameChars())));

    if (!Directory.Exists(root)) return Results.NotFound(new { error = "Project not found" });

    Directory.Delete(root, recursive: true);
    return Results.Ok(new { deleted = true, project = projectId });
});

// SPA fallback — all unknown routes → index.html (React router)
app.MapFallbackToFile("index.html");

app.Run();

// =============================================================================
// DTOs
// =============================================================================

public sealed record LoginRequest(string Username, string Password, string Domain);
public sealed record TemplateUpsertRequest(string Name, string Content, string? Collection);
public sealed record RatingRequest(string MessageId, string ConvId, int Rating, string? Model);
public sealed record SkillExampleRequest(string UserMessage, string AssistantMessage);

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
