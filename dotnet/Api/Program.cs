using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Prometheus;
using SetYazilim.Llm;
using SetYazilim.Llm.Api.Auth;
using SetYazilim.Llm.Api.Endpoints;
using SetYazilim.Llm.Api.Jobs;
using SetYazilim.Llm.Api.Sql;
using SetYazilim.Llm.Context;
using SetYazilim.Llm.Memory;
using SetYazilim.Llm.Retrieval;


// =============================================================================
// Agentic AI Platform — ASP.NET Core 8 Minimal API
// =============================================================================

var builder = WebApplication.CreateBuilder(args);

// Increase limits for vision requests (base64 images can be large).
// Override: appsettings.json → Limits:MaxRequestBodyMB (default 100 MB).
builder.WebHost.ConfigureKestrel((ctx, o) =>
{
    var mb = ctx.Configuration.GetValue<int?>("Limits:MaxRequestBodyMB") ?? 100;
    o.Limits.MaxRequestBodySize = (long)mb * 1024 * 1024;
});

builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables(prefix: "LLM_");

var cfg = builder.Configuration;
var services = builder.Services;

// ── In-process cache (embedding results, etc.) ────────────────────────────────
services.AddMemoryCache(o => o.SizeLimit = 2000);

// ── HTTP client for outbound requests (GitHub API for skill imports, etc.) ────
services.AddHttpClient("health");
services.AddHttpClient("github", c =>
{
    c.DefaultRequestHeaders.Add("User-Agent", "SetYazilim-LLM/1.0");
    c.DefaultRequestHeaders.Add("Accept",     "application/vnd.github+json");
    c.Timeout = TimeSpan.FromSeconds(45);
});

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

// ── OWASP-aligned event logging ───────────────────────────────────────────────
services.AddHttpContextAccessor();
services.AddScoped<IEventLog, EventLog>();

// ── Tools (file generation via Python subprocess) ─────────────────────────────
services.AddSingleton<SetYazilim.Llm.Api.Tools.IFileGenerator, SetYazilim.Llm.Api.Tools.FileGenerator>();
services.AddSingleton<SetYazilim.Llm.Api.Tools.IBenchmarkService, SetYazilim.Llm.Api.Tools.BenchmarkService>();
services.AddHttpClient("bench-internal");

// ── SQL external sources (Phase 1: connection mgmt + test) ────────────────────
services.AddDataProtection().SetApplicationName("set-llm-api");
services.AddSingleton<ISqlConnectionService, SqlConnectionService>();

// ── Background jobs ───────────────────────────────────────────────────────────
services.AddScoped<IJobService, JobService>();
services.AddSingleton<IJobHandler, SqlIngestSchemaJobHandler>();
services.AddSingleton<IJobHandler, SqlSyncSchemaJobHandler>();
services.AddSingleton<IJobHandler, SqlIngestDataJobHandler>();
services.AddSingleton<IJobHandler, SqlSyncDataJobHandler>();
services.AddHostedService<JobWorker>();
services.AddHostedService<AutoSyncScheduler>();
services.AddHostedService<SetYazilim.Llm.Api.Auth.EventLogRetentionService>();
services.AddHostedService<SetYazilim.Llm.Api.Tools.GeneratedFilesCleanupService>();

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
        );
        CREATE TABLE IF NOT EXISTS skill_settings (
            skill_id    VARCHAR(200) PRIMARY KEY,
            order_value INTEGER      NOT NULL DEFAULT 999,
            updated_at  TIMESTAMPTZ  NOT NULL DEFAULT NOW()
        );
        CREATE TABLE IF NOT EXISTS event_log (
            id          BIGSERIAL    PRIMARY KEY,
            ts          TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            category    VARCHAR(20)  NOT NULL,
            severity    VARCHAR(10)  NOT NULL,
            event_type  VARCHAR(100) NOT NULL,
            username    VARCHAR(200),
            source_ip   VARCHAR(64),
            user_agent  VARCHAR(500),
            request_id  VARCHAR(64),
            session_id  VARCHAR(128),
            endpoint    VARCHAR(500),
            action      VARCHAR(100),
            resource    VARCHAR(500),
            result      VARCHAR(20)  NOT NULL,
            reason      TEXT,
            details     JSONB
        );
        CREATE INDEX IF NOT EXISTS idx_event_log_ts        ON event_log (ts DESC);
        CREATE INDEX IF NOT EXISTS idx_event_log_cat_sev   ON event_log (category, severity);
        CREATE INDEX IF NOT EXISTS idx_event_log_user      ON event_log (username);
        CREATE INDEX IF NOT EXISTS idx_event_log_ip        ON event_log (source_ip);
        CREATE INDEX IF NOT EXISTS idx_event_log_type      ON event_log (event_type);
        CREATE TABLE IF NOT EXISTS benchmark_results (
            id                 BIGSERIAL    PRIMARY KEY,
            ts                 TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            model              VARCHAR(50)  NOT NULL,
            concurrency        INTEGER      NOT NULL,
            max_tokens         INTEGER      NOT NULL,
            success_count      INTEGER      NOT NULL,
            fail_count         INTEGER      NOT NULL,
            wall_seconds       DOUBLE PRECISION NOT NULL,
            ttft_p50_ms        DOUBLE PRECISION NOT NULL,
            ttft_p95_ms        DOUBLE PRECISION NOT NULL,
            tps_per_stream_p50 DOUBLE PRECISION NOT NULL,
            tps_per_stream_p95 DOUBLE PRECISION NOT NULL,
            tps_aggregate      DOUBLE PRECISION NOT NULL,
            total_tokens       INTEGER      NOT NULL,
            label              TEXT,
            created_by         VARCHAR(100) NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS idx_bench_ts    ON benchmark_results (ts DESC);
        CREATE INDEX IF NOT EXISTS idx_bench_model ON benchmark_results (model, ts DESC);
        CREATE TABLE IF NOT EXISTS sql_connections (
            id                  SERIAL PRIMARY KEY,
            name                VARCHAR(200) NOT NULL,
            db_type             VARCHAR(20)  NOT NULL,
            host                VARCHAR(200) NOT NULL,
            port                INTEGER      NOT NULL DEFAULT 0,
            database            VARCHAR(200) NOT NULL,
            username            VARCHAR(200) NOT NULL,
            encrypted_password     TEXT         NOT NULL DEFAULT '',
            query_timeout_sec      INTEGER      NOT NULL DEFAULT 120,
            auto_sync_interval_min INTEGER      NOT NULL DEFAULT 0,
            last_auto_sync_at      TIMESTAMPTZ  NULL,
            created_by             VARCHAR(100) NOT NULL DEFAULT '',
            created_at             TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            updated_at             TIMESTAMPTZ  NOT NULL DEFAULT NOW()
        );
        ALTER TABLE sql_connections ADD COLUMN IF NOT EXISTS query_timeout_sec      INTEGER     NOT NULL DEFAULT 120;
        ALTER TABLE sql_connections ADD COLUMN IF NOT EXISTS auto_sync_interval_min INTEGER     NOT NULL DEFAULT 0;
        ALTER TABLE sql_connections ADD COLUMN IF NOT EXISTS last_auto_sync_at      TIMESTAMPTZ NULL;
        CREATE TABLE IF NOT EXISTS sql_ingested_objects (
            id              SERIAL PRIMARY KEY,
            connection_id   INTEGER      NOT NULL,
            collection      VARCHAR(100) NOT NULL,
            object_type     VARCHAR(20)  NOT NULL,
            schema_name     VARCHAR(200) NOT NULL,
            object_name     VARCHAR(200) NOT NULL,
            source          VARCHAR(500) NOT NULL,
            ddl_hash        VARCHAR(64)  NOT NULL,
            chunks_count    INTEGER      NOT NULL DEFAULT 0,
            last_ingested_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            UNIQUE (connection_id, object_type, schema_name, object_name)
        );
        CREATE TABLE IF NOT EXISTS jobs (
            id            BIGSERIAL PRIMARY KEY,
            job_type      VARCHAR(100) NOT NULL,
            status        VARCHAR(20)  NOT NULL DEFAULT 'queued',
            progress_cur  INTEGER      NOT NULL DEFAULT 0,
            progress_tot  INTEGER      NOT NULL DEFAULT 0,
            message       TEXT         NOT NULL DEFAULT '',
            params        TEXT         NOT NULL DEFAULT '{}',
            result        TEXT         NOT NULL DEFAULT '{}',
            error         TEXT         NOT NULL DEFAULT '',
            created_by    VARCHAR(100) NOT NULL DEFAULT '',
            created_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            started_at    TIMESTAMPTZ,
            completed_at  TIMESTAMPTZ
        );
        CREATE INDEX IF NOT EXISTS idx_jobs_status_id ON jobs (status, id);
        CREATE TABLE IF NOT EXISTS sql_table_groups (
            id            SERIAL PRIMARY KEY,
            connection_id INTEGER      NOT NULL,
            name          VARCHAR(200) NOT NULL,
            sort_order    INTEGER      NOT NULL DEFAULT 0,
            created_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            UNIQUE (connection_id, name)
        );
        CREATE TABLE IF NOT EXISTS sql_table_configs (
            id                   SERIAL PRIMARY KEY,
            connection_id        INTEGER      NOT NULL,
            schema_name          VARCHAR(200) NOT NULL,
            table_name           VARCHAR(200) NOT NULL,
            pk_col               VARCHAR(500) NOT NULL DEFAULT '',
            created_col          VARCHAR(200) NOT NULL DEFAULT '',
            updated_col          VARCHAR(200) NOT NULL DEFAULT '',
            row_limit            INTEGER      NOT NULL DEFAULT 1000,
            where_clause         TEXT         NOT NULL DEFAULT '',
            included_columns     TEXT         NOT NULL DEFAULT '[]',
            group_id             INTEGER,
            collection           VARCHAR(100) NOT NULL DEFAULT '',
            last_synced_at       TIMESTAMPTZ,
            last_max_updated_at  TIMESTAMPTZ,
            last_sync_status     VARCHAR(20),
            last_sync_added      INTEGER      NOT NULL DEFAULT 0,
            last_sync_updated    INTEGER      NOT NULL DEFAULT 0,
            last_sync_error      TEXT         NOT NULL DEFAULT '',
            created_at           TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            UNIQUE (connection_id, schema_name, table_name)
        );
        ALTER TABLE sql_table_configs ADD COLUMN IF NOT EXISTS last_sync_status  VARCHAR(20);
        ALTER TABLE sql_table_configs ADD COLUMN IF NOT EXISTS last_sync_added   INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE sql_table_configs ADD COLUMN IF NOT EXISTS last_sync_updated INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE sql_table_configs ADD COLUMN IF NOT EXISTS last_sync_error   TEXT    NOT NULL DEFAULT '';
        CREATE TABLE IF NOT EXISTS sql_ingested_rows (
            id               SERIAL PRIMARY KEY,
            table_config_id  INTEGER      NOT NULL,
            pk_value         VARCHAR(500) NOT NULL,
            content_hash     VARCHAR(64)  NOT NULL,
            source           VARCHAR(500) NOT NULL,
            chunks_count     INTEGER      NOT NULL DEFAULT 0,
            last_ingested_at TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            UNIQUE (table_config_id, pk_value)
        );
        CREATE INDEX IF NOT EXISTS idx_sql_ingested_rows_config ON sql_ingested_rows (table_config_id)";
    await cmd0.ExecuteNonQueryAsync();
}

app.UseCors("ui");
app.UseHttpMetrics();          // built-in: http_requests_total, http_request_duration_seconds

// ── OWASP: automatic 401/403 event logging — wraps auth pipeline ──────────────
// Must run BEFORE UseAuthentication/UseAuthorization so we observe their final
// status codes (they short-circuit on failure).
app.Use(async (ctx, next) =>
{
    await next();

    var path = ctx.Request.Path.Value ?? "";
    if (!path.StartsWith("/api/")) return;
    if (path == "/api/auth/login")  return;  // already logged inside handler
    if (ctx.Response.StatusCode != 401 && ctx.Response.StatusCode != 403) return;

    try
    {
        var evt = ctx.RequestServices.GetRequiredService<IEventLog>();
        await evt.LogAsync(
            EventCategory.Authz,
            EventSeverity.Warn,
            ctx.Response.StatusCode == 401 ? "authz.unauthenticated" : "authz.forbidden",
            EventResult.Denied,
            reason: $"HTTP {ctx.Response.StatusCode}",
            action: ctx.Request.Method,
            resource: path,
            ct: ctx.RequestAborted);
    }
    catch { /* never break the response */ }
});

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
// ─── Health / Metrics ────────────────────────────────────────────────────────
app.MapHealth();   // /health, /health/deep, /metrics — see Endpoints/MapHealth.cs

// =============================================================================
// ─── Auth ─────────────────────────────────────────────────────────────────────
app.MapAuth();   // /api/auth/* — see Endpoints/MapAuth.cs


// =============================================================================
// ─── File Extract ─────────────────────────────────────────────────────────────
app.MapFiles();   // /api/files/extract — see Endpoints/MapFiles.cs

// =============================================================================
// ─── SQL Connections + Table Groups + Table Configs + Schema/Data Ingest ─────
app.MapSql();    // /api/admin/sql-connections/* — see Endpoints/MapSql.cs

// ─── Background Jobs ─────────────────────────────────────────────────────────
app.MapJobs();   // /api/jobs/*, /api/admin/jobs/* — see Endpoints/MapJobs.cs

// =============================================================================
// ─── Activity Log ─────────────────────────────────────────────────────────────

// Local wrapper — forwards to ActivityLogger.LogAsync. Preserves the existing
// `_ = LogActivity(ds, ...)` call sites (16 places) so they don't all need
// to change at once.
Task LogActivity(NpgsqlDataSource ds, string username, string action, string target, string details = "")
    => ActivityLogger.LogAsync(ds, username, action, target, details);

// /api/admin/activity-log + /api/admin/event-log[/summary] — see Endpoints/MapEventLog.cs
app.MapEventLog();

// =============================================================================
// ─── Ratings ─────────────────────────────────────────────────────────────────
app.MapRatings();   // /api/ratings + /api/admin/ratings/stats — see Endpoints/MapRatings.cs

// =============================================================================
// ─── Prompt Templates ────────────────────────────────────────────────────────
app.MapTemplates();   // /api/templates + /api/admin/templates/* — see Endpoints/MapTemplates.cs

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

app.MapGet("/api/skills", [Authorize] async (SkillRegistry registry, NpgsqlDataSource ds,
    Microsoft.Extensions.Caching.Memory.IMemoryCache cache, CancellationToken ct) =>
{
    var overrides = await GetCachedSkillOrders(cache, ds, ct);
    var skills = registry.Metadata.Values
        .Select(m =>
        {
            var ord = overrides.TryGetValue(m.Id, out var ov) ? ov : m.Order;
            return new
            {
                id              = m.Id,
                name            = m.Name,
                description     = m.Description,
                icon            = m.Icon,
                collection      = m.Collection,
                order           = ord,
                isFolder        = m.IsFolder,
                referenceCount  = m.ReferenceCount,
                contentBytes    = m.ContentBytes,
            };
        })
        .OrderBy(s => s.order)
        .ThenBy(s => s.name, StringComparer.OrdinalIgnoreCase);
    return Results.Ok(skills);
});

// Helper — load DB order overrides (cached 30s in IMemoryCache to avoid hit on every /api/skills call)
static async Task<Dictionary<string, int>> LoadSkillOrderOverrides(NpgsqlDataSource ds, CancellationToken ct)
{
    var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    try
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT skill_id, order_value FROM skill_settings";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            dict[r.GetString(0)] = r.GetInt32(1);
    }
    catch { /* table may not exist yet — fall through with empty dict */ }
    return dict;
}

static async Task<Dictionary<string, int>> GetCachedSkillOrders(
    Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
    NpgsqlDataSource ds, CancellationToken ct)
{
    const string key = "skillOrderOverrides";
    if (cache.TryGetValue(key, out object? rawCached) && rawCached is Dictionary<string, int> cached)
        return cached;
    var fresh = await LoadSkillOrderOverrides(ds, ct);
    using var ce = cache.CreateEntry(key);
    ce.Value = fresh;
    ce.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
    ce.Size = 1;
    return fresh;
}

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
app.MapChat();        // /api/chat[/stream] — see Endpoints/MapChat.cs

// =============================================================================
// ─── Ingest + Admin RAG Management ───────────────────────────────────────────
app.MapDocuments();   // /api/ingest, /api/admin/upload, documents, collections — see Endpoints/MapDocuments.cs

// =============================================================================
// ─── Admin — Usage (LiteLLM spend proxy) ─────────────────────────────────────
app.MapUsage();   // /api/admin/usage/* — see Endpoints/MapUsage.cs

// GET /api/admin/skills
app.MapGet("/api/admin/skills", [Authorize("AdminOnly")] async (
    SkillRegistry registry, NpgsqlDataSource ds,
    Microsoft.Extensions.Caching.Memory.IMemoryCache cache, CancellationToken ct) =>
{
    var overrides = await GetCachedSkillOrders(cache, ds, ct);
    var items = registry.Metadata.Values
        .Select(m =>
        {
            var ord = overrides.TryGetValue(m.Id, out var ov) ? ov : m.Order;
            return new
            {
                id              = m.Id,
                name            = m.Name,
                description     = m.Description,
                order           = ord,
                isFolder        = m.IsFolder,
                referenceCount  = m.ReferenceCount,
                size            = (int)m.ContentBytes,
            };
        })
        .OrderBy(s => s.order)
        .ThenBy(s => s.name, StringComparer.OrdinalIgnoreCase);
    return Results.Ok(items);
});

// GET /api/admin/skills/{id}
app.MapGet("/api/admin/skills/{id}", [Authorize("AdminOnly")] (string id, SkillRegistry registry) =>
{
    if (!registry.All.ContainsKey(id)) return Results.NotFound();
    return Results.Text(registry.All[id], "text/plain; charset=utf-8");
});

// POST /api/admin/skills — upload a .md skill file OR a .zip skill folder
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
        var fname = file.FileName;
        var ext   = Path.GetExtension(fname).ToLowerInvariant();

        if (ext == ".zip")
        {
            // Folder-based skill upload via zip
            if (registry.SkillsPath is null)
            {
                results.Add(new { file = fname, ok = false, error = "SkillsPath not configured" });
                continue;
            }
            try
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, ct);
                ms.Position = 0;
                using var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);

                // Determine skill id from zip: look for SKILL.md entry
                string? skillId = null;
                foreach (var entry in zip.Entries)
                {
                    if (entry.Name.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                    {
                        skillId = entry.FullName.Contains('/')
                            ? entry.FullName.Split('/')[0]
                            : Path.GetFileNameWithoutExtension(fname);
                        break;
                    }
                }
                skillId ??= Path.GetFileNameWithoutExtension(fname);

                var destDir = Path.GetFullPath(Path.Combine(registry.SkillsPath, skillId));
                if (!destDir.StartsWith(registry.SkillsPath, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new { file = fname, ok = false, error = "invalid skill id" });
                    continue;
                }
                Directory.CreateDirectory(destDir);

                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
                    var relPath = entry.FullName;
                    if (relPath.StartsWith(skillId + "/", StringComparison.OrdinalIgnoreCase))
                        relPath = relPath[(skillId.Length + 1)..];

                    var destPath = Path.GetFullPath(Path.Combine(destDir, relPath));
                    if (!destPath.StartsWith(destDir + Path.DirectorySeparatorChar) &&
                        !destPath.Equals(destDir, StringComparison.OrdinalIgnoreCase))
                        continue; // zip-slip guard

                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    using var stream = entry.Open();
                    using var fs     = File.Create(destPath);
                    await stream.CopyToAsync(fs, ct);
                }

                // Hot reload skills
                registry.LoadFromDirectory(registry.SkillsPath);
                results.Add(new { file = fname, ok = true, id = skillId });
                _ = LogActivity(ds, username, "skill.upload", skillId, $"type=zip size={file.Length}");
            }
            catch (Exception ex)
            {
                results.Add(new { file = fname, ok = false, error = ex.Message });
            }
            continue;
        }

        if (ext != ".md")
        {
            results.Add(new { file = fname, ok = false, error = "Only .md or .zip files allowed" });
            continue;
        }

        using var reader = new StreamReader(file.OpenReadStream());
        var content = (await reader.ReadToEndAsync(ct)).Trim();
        var skillId2 = Path.GetFileNameWithoutExtension(fname);

        if (registry.SkillsPath is not null)
        {
            var filePath = Path.Combine(registry.SkillsPath, fname);
            await File.WriteAllTextAsync(filePath, content, ct);
        }

        registry.Register(skillId2, content);
        results.Add(new { file = fname, ok = true, id = skillId2 });
        _ = LogActivity(ds, username, "skill.upload", skillId2, $"size={file.Length}");
    }

    return Results.Ok(results);
});

// POST /api/admin/skills/import-anthropic — download selected skills from anthropics/skills GitHub repo
app.MapPost("/api/admin/skills/import-anthropic", [Authorize("AdminOnly")] async (
    [FromBody] AnthropicSkillImportRequest req,
    SkillRegistry registry,
    ClaimsPrincipal user,
    NpgsqlDataSource ds,
    IHttpClientFactory httpClientFactory,
    CancellationToken ct) =>
{
    if (registry.SkillsPath is null)
        return Results.BadRequest(new { error = "SkillsPath not configured" });
    if (req.Skills is null || req.Skills.Length == 0)
        return Results.BadRequest(new { error = "No skills specified" });

    var username = user.FindFirstValue(ClaimTypes.Name) ?? "unknown";
    var http     = httpClientFactory.CreateClient("github");
    var results  = new List<object>();

    // Subdirectory names we never download — scripts/templates/schemas etc.
    var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "scripts", "schemas", "templates", "assets", "canvas-fonts", "core", "eval-viewer" };

    // Get full repo tree once (single API call)
    List<GithubTreeItem>? tree = null;
    try
    {
        var treeResp = await http.GetFromJsonAsync<GithubTreeResponse>(
            "https://api.github.com/repos/anthropics/skills/git/trees/main?recursive=1",
            ct);
        tree = treeResp?.Tree ?? new();
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"GitHub tree fetch failed: {ex.Message}" });
    }

    foreach (var rawName in req.Skills)
    {
        ct.ThrowIfCancellationRequested();
        var skillName = (rawName ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(skillName)) continue;

        try
        {
            // Path-safety: skillName must be a simple folder name
            if (skillName.Contains('/') || skillName.Contains('\\') || skillName.Contains(".."))
            {
                results.Add(new { skill = skillName, ok = false, error = "invalid skill name" });
                continue;
            }

            var destDir = Path.GetFullPath(Path.Combine(registry.SkillsPath, skillName));
            if (!destDir.StartsWith(registry.SkillsPath, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new { skill = skillName, ok = false, error = "path traversal" });
                continue;
            }

            if (Directory.Exists(destDir) && !req.Overwrite)
            {
                results.Add(new { skill = skillName, ok = true, action = "skipped (already exists)" });
                continue;
            }
            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
            Directory.CreateDirectory(destDir);

            var prefix = $"skills/{skillName}/";
            var mdFiles = tree
                .Where(i => i.Type == "blob"
                            && i.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            && i.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (mdFiles.Count == 0)
            {
                results.Add(new { skill = skillName, ok = false, error = "no .md files found in repo" });
                Directory.Delete(destDir, recursive: true);
                continue;
            }

            var downloaded = new List<string>();
            foreach (var item in mdFiles)
            {
                var pathInSkill = item.Path[prefix.Length..];
                var firstSeg    = pathInSkill.Contains('/') ? pathInSkill.Split('/')[0] : "";
                if (!string.IsNullOrEmpty(firstSeg) && skipDirs.Contains(firstSeg)) continue;

                var rawUrl = $"https://raw.githubusercontent.com/anthropics/skills/main/{item.Path}";
                var content = await http.GetStringAsync(rawUrl, ct);
                var localPath = Path.GetFullPath(Path.Combine(destDir, pathInSkill));
                if (!localPath.StartsWith(destDir + Path.DirectorySeparatorChar) &&
                    !localPath.Equals(destDir, StringComparison.OrdinalIgnoreCase))
                    continue; // path-traversal guard
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                await File.WriteAllTextAsync(localPath, content, ct);
                downloaded.Add(pathInSkill);
            }

            if (!downloaded.Any(f => f.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)))
            {
                Directory.Delete(destDir, recursive: true);
                results.Add(new { skill = skillName, ok = false, error = "SKILL.md missing after download" });
                continue;
            }

            results.Add(new { skill = skillName, ok = true, action = "imported", files = downloaded.Count });
            _ = LogActivity(ds, username, "skill.import", skillName, $"files={downloaded.Count}");
        }
        catch (Exception ex)
        {
            results.Add(new { skill = skillName, ok = false, error = ex.Message });
        }
    }

    // Hot-reload all skills from directory
    registry.LoadFromDirectory(registry.SkillsPath);

    var imported = results.Count(r =>
    {
        var ok = r.GetType().GetProperty("ok")?.GetValue(r);
        var action = r.GetType().GetProperty("action")?.GetValue(r) as string;
        return ok is true && action != null && action.StartsWith("imported");
    });
    return Results.Ok(new { results, imported });
});

// PUT /api/admin/skills/{id}/order — update skill order (DB-stored, survives deploys)
// body: { order: number }
app.MapPut("/api/admin/skills/{id}/order", [Authorize("AdminOnly")] async (
    string id, [FromBody] SkillOrderRequest req,
    SkillRegistry registry, ClaimsPrincipal user, NpgsqlDataSource ds,
    Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
    CancellationToken ct) =>
{
    if (!registry.All.ContainsKey(id))
        return Results.NotFound(new { error = $"Skill '{id}' not found" });

    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = @"INSERT INTO skill_settings (skill_id, order_value, updated_at)
                        VALUES ($1, $2, NOW())
                        ON CONFLICT (skill_id) DO UPDATE
                        SET order_value = $2, updated_at = NOW()";
    cmd.Parameters.AddWithValue(id);
    cmd.Parameters.AddWithValue(req.Order);
    await cmd.ExecuteNonQueryAsync(ct);

    cache.Remove("skillOrderOverrides");  // invalidate cached list

    var username = user.FindFirstValue(ClaimTypes.Name) ?? "unknown";
    _ = LogActivity(ds, username, "skill.order.update", id, $"order={req.Order}");
    return Results.Ok(new { id, order = req.Order });
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
        // Folder-based skill?
        var folderPath = Path.Combine(registry.SkillsPath, id);
        if (Directory.Exists(folderPath))
        {
            Directory.Delete(folderPath, recursive: true);
        }
        else
        {
            var filePath = Path.Combine(registry.SkillsPath, id + ".md");
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }

    registry.Remove(id);

    // Cleanup DB-stored order override
    try
    {
        using var c = ds.OpenConnection();
        using var d = c.CreateCommand();
        d.CommandText = "DELETE FROM skill_settings WHERE skill_id=$1";
        d.Parameters.AddWithValue(id);
        d.ExecuteNonQuery();
    }
    catch { /* table may be missing — non-fatal */ }

    var username = user.FindFirstValue(ClaimTypes.Name) ?? "unknown";
    _ = LogActivity(ds, username, "skill.delete", id);
    return Results.Ok(new { deleted = id });
});

// =============================================================================
// ─── Session ─────────────────────────────────────────────────────────────────
app.MapSession();   // /api/session/* — see Endpoints/MapSession.cs

// =============================================================================
// ─── Tools (file gen) + Admin Benchmark (LLM concurrency test) ───────────────
app.MapTools();   // see Endpoints/MapTools.cs

// =============================================================================
// ─── Tool Proxy (CORS bypass for agentic tool calls) ─────────────────────────
app.MapProxy();   // /api/proxy — see Endpoints/MapProxy.cs

// =============================================================================
// ─── LLM proxy (authenticated, server-side LiteLLM API key) ──────────────────
app.MapLlm();        // /api/llm/completions — see Endpoints/MapLlm.cs

// =============================================================================
// ─── Error log ───────────────────────────────────────────────────────────────
app.MapErrorLog();   // /api/log/error — see Endpoints/MapErrorLog.cs

// =============================================================================
// ─── Project file management ─────────────────────────────────────────────────
app.MapProjects();   // /api/projects/* — see Endpoints/MapProjects.cs

// SPA fallback — all unknown routes → index.html (React router)
app.MapFallbackToFile("index.html");

app.Run();

// =============================================================================
// DTOs
// =============================================================================

public sealed record LoginRequest(string Username, string Password, string Domain);
public sealed record TemplateUpsertRequest(string Name, string Content, string? Collection);

// ── Rate limit for SQL connection test endpoints ──────────────────────────────
// Sliding 1-minute window per (user) — limit configurable via Limits:SqlTestRateLimitPerMinute.
static class SqlConnTestRateLimit
{
    public static bool TryAcquire(Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
        string user, int maxPerMinute, out int retryAfterSec)
        => SlidingRateLimit.TryAcquire(cache, $"sqlConnTest:{user}", maxPerMinute, out retryAfterSec);
}

// ── Rate limit for /api/auth/login (brute-force koruması) ─────────────────────
// Per (IP, username) tuple — limit configurable via Limits:LoginRateLimitPerMinute.
// Doğru login sayacı sıfırlamaz — saldırgan aynı pencerede zorlamayı sürdüremez.
static class LoginRateLimit
{
    public static bool TryAcquire(Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
        string ip, string username, int maxPerMinute, out int retryAfterSec)
        => SlidingRateLimit.TryAcquire(cache, $"loginRL:{ip}:{username.ToLowerInvariant()}",
                                       maxPerMinute, out retryAfterSec);
}

// ── Reusable sliding-window rate limiter (IMemoryCache backed) ───────────────
// 1-minute window. Same logic both endpoints share.
static class SlidingRateLimit
{
    public static bool TryAcquire(Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
        string key, int maxPerMinute, out int retryAfterSec)
    {
        if (maxPerMinute <= 0) maxPerMinute = 5;  // defensive: refuse misconfig
        var now = DateTimeOffset.UtcNow;
        (DateTimeOffset start, int count) entry;
        if (cache.TryGetValue(key, out object? raw)
            && raw is ValueTuple<DateTimeOffset, int> cached
            && (now - cached.Item1).TotalSeconds < 60)
        {
            entry = (cached.Item1, cached.Item2);
        }
        else
        {
            entry = (now, 0);
        }

        if (entry.count >= maxPerMinute)
        {
            retryAfterSec = Math.Max(1, 60 - (int)(now - entry.start).TotalSeconds);
            return false;
        }

        entry = (entry.start, entry.count + 1);
        using var ce = cache.CreateEntry(key);
        ce.Value = (entry.start, entry.count);
        ce.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
        ce.Size = 1;
        retryAfterSec = 0;
        return true;
    }
}

// ── Prometheus custom metrics ─────────────────────────────────────────────────
static class LlmMetrics
{
    public static readonly Counter RequestsTotal = Metrics.CreateCounter(
        "setllm_llm_requests_total",
        "Total LLM completion requests",
        labelNames: ["model", "status"]);   // status: success | error | warming

    public static readonly Histogram DurationSeconds = Metrics.CreateHistogram(
        "setllm_llm_request_duration_seconds",
        "LLM completion request duration in seconds",
        labelNames: ["model"],
        new HistogramConfiguration { Buckets = [0.5, 1, 2, 5, 10, 20, 30, 60, 120] });

    public static readonly Counter IngestChunksTotal = Metrics.CreateCounter(
        "setllm_rag_ingest_chunks_total",
        "Total RAG document chunks ingested",
        labelNames: ["collection"]);

    public static readonly Counter RatingsTotal = Metrics.CreateCounter(
        "setllm_ratings_total",
        "Total message ratings",
        labelNames: ["rating"]);            // rating: up | down
}
public sealed record RatingRequest(string MessageId, string ConvId, int Rating, string? Model);
public sealed record SkillExampleRequest(string UserMessage, string AssistantMessage);
public sealed record BulkAssignGroupRequest(int[] TableConfigIds, int? GroupId);

public sealed record AnthropicSkillImportRequest(string[] Skills, bool Overwrite = false);
public sealed record SkillOrderRequest(int Order);

// Upsert a single key/value into a markdown YAML-frontmatter block.
// If the file has no frontmatter, one is created at the top.
// If the key exists, the value is replaced. Otherwise the key is appended before `---`.
public static class SkillFrontmatter
{
    public static string UpsertKey(string content, string key, string value)
    {
        var nl = content.Contains("\r\n") ? "\r\n" : "\n";
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith("---"))
        {
            var end = trimmed.IndexOf("---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                var fm   = trimmed[3..end];
                var rest = trimmed[(end + 3)..];
                var lines = fm.Trim('\n', '\r').Split('\n').Select(l => l.TrimEnd()).ToList();
                var ki = lines.FindIndex(l => l.TrimStart().StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));
                var newLine = $"{key}: {value}";
                if (ki >= 0) lines[ki] = newLine;
                else lines.Add(newLine);
                lines = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                return "---" + nl + string.Join(nl, lines) + nl + "---" + rest;
            }
        }
        return "---" + nl + $"{key}: {value}" + nl + "---" + nl + nl + content;
    }
}

public sealed class GithubTreeResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("tree")]
    public List<GithubTreeItem> Tree { get; set; } = new();
}

public sealed class GithubTreeItem
{
    [System.Text.Json.Serialization.JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string Type { get; set; } = "";
}

public sealed record SqlConnectionUpsertRequest(
    string  Name,
    string  DbType,
    string  Host,
    int     Port,
    string  Database,
    string? Username,
    string? Password,
    int     QueryTimeoutSec = 120,
    int     AutoSyncIntervalMin = 0);

public sealed record SchemaIngestRequest(
    string?   Collection,
    string[]? IncludeTypes);  // ["table","view","procedure","function","trigger"]

public sealed record TableSampleSpec(string Schema, string Name, int Limit, string? Where);
public sealed record DataIngestRequest(
    string?            Collection,
    int                DefaultLimit,
    TableSampleSpec[]? Tables);

public sealed record TableGroupUpsert(string Name, int SortOrder);
public sealed record TableConfigUpsert(
    string    Schema,
    string    Table,
    string?   PkCol,
    string?   CreatedCol,
    string?   UpdatedCol,
    int       RowLimit,
    string?   WhereClause,
    string[]? IncludedColumns,
    int?      GroupId,
    string?   Collection);
public sealed record SyncDataRequest(int[]? TableConfigIds);

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
