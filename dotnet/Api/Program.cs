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
        CREATE TABLE IF NOT EXISTS collection_settings (
            collection   TEXT         PRIMARY KEY,
            priority     TEXT         NOT NULL DEFAULT 'normal'
                         CHECK (priority IN ('high','normal','low','hidden')),
            data_type    TEXT         NOT NULL DEFAULT '',
            description  TEXT         NOT NULL DEFAULT '',
            updated_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW()
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
// All call sites moved to ActivityLogger.LogAsync (see Endpoints/ActivityLogger.cs).
// /api/admin/activity-log + /api/admin/event-log[/summary] — see Endpoints/MapEventLog.cs
app.MapEventLog();

// =============================================================================
// ─── Ratings ─────────────────────────────────────────────────────────────────
app.MapRatings();   // /api/ratings + /api/admin/ratings/stats — see Endpoints/MapRatings.cs

// =============================================================================
// ─── Prompt Templates ────────────────────────────────────────────────────────
app.MapTemplates();   // /api/templates + /api/admin/templates/* — see Endpoints/MapTemplates.cs

// =============================================================================
// ─── Skills + Skill Examples + /api/models/capabilities + /api/admin/skills ──
app.MapSkills();   // see Endpoints/MapSkills.cs

// =============================================================================
// ─── Chat ────────────────────────────────────────────────────────────────────
app.MapChat();        // /api/chat[/stream] — see Endpoints/MapChat.cs

// =============================================================================
// ─── Ingest + Admin RAG Management ───────────────────────────────────────────
app.MapDocuments();   // /api/ingest, /api/admin/upload, documents, collections — see Endpoints/MapDocuments.cs

// =============================================================================
// ─── Admin — Usage (LiteLLM spend proxy) ─────────────────────────────────────
app.MapUsage();   // /api/admin/usage/* — see Endpoints/MapUsage.cs


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

public sealed record CollectionSettingsRequest(
    string? Priority,
    string? DataType,
    string? Description);
