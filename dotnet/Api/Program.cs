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
using SetYazilim.Llm.Api.Jobs;
using SetYazilim.Llm.Api.Sql;
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

// ── HTTP client for outbound requests (GitHub API for skill imports, etc.) ────
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
// ─── Health ──────────────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "ok", ts = DateTime.UtcNow }));
app.MapMetrics("/metrics");    // Prometheus scrape endpoint (no auth — internal only)

// =============================================================================
// ─── Auth ─────────────────────────────────────────────────────────────────────

// GET /api/auth/domains — list available AD domains (public)
app.MapGet("/api/auth/domains", (ILdapAuthService ldap) =>
    Results.Ok(ldap.GetDomains()));

// POST /api/auth/login
app.MapPost("/api/auth/login", async (
    [FromBody] LoginRequest req,
    ILdapAuthService ldap,
    IJwtTokenService jwt,
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
// ─── SQL Connections (Phase 1: CRUD + Test) ──────────────────────────────────

static DbType ParseDbType(string s) => s.ToLowerInvariant() switch
{
    "mssql"   or "sqlserver"  => DbType.MsSql,
    "postgres" or "postgresql" => DbType.Postgres,
    "mysql"   or "mariadb"    => DbType.MySql,
    "oracle"                  => DbType.Oracle,
    _                         => throw new ArgumentException($"Unsupported db type: {s}"),
};
static string DbTypeToStr(DbType t) => t switch
{
    DbType.MsSql    => "mssql",
    DbType.Postgres => "postgres",
    DbType.MySql    => "mysql",
    DbType.Oracle   => "oracle",
    _               => "unknown",
};

// Shared projection — list element + single-record reads
static async Task<object?> ReadSqlConnectionRecord(NpgsqlConnection conn, int id, CancellationToken ct)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = @"SELECT id, name, db_type, host, port, database, username,
                               COALESCE(query_timeout_sec, 120),
                               COALESCE(auto_sync_interval_min, 0),
                               created_by, created_at
                        FROM sql_connections WHERE id=$1";
    cmd.Parameters.AddWithValue(id);
    await using var r = await cmd.ExecuteReaderAsync(ct);
    if (!await r.ReadAsync(ct)) return null;
    return new {
        id                  = r.GetInt32(0),
        name                = r.GetString(1),
        dbType              = r.GetString(2),
        host                = r.GetString(3),
        port                = r.GetInt32(4),
        database            = r.GetString(5),
        username            = r.GetString(6),
        queryTimeoutSec     = r.GetInt32(7),
        autoSyncIntervalMin = r.GetInt32(8),
        createdBy           = r.GetString(9),
        createdAt           = r.GetDateTime(10),
    };
}

// GET /api/admin/sql-connections — list (password not returned)
app.MapGet("/api/admin/sql-connections", [Authorize("AdminOnly")] async (
    NpgsqlDataSource ds, CancellationToken ct) =>
{
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = @"SELECT id, name, db_type, host, port, database, username,
                               COALESCE(query_timeout_sec, 120),
                               COALESCE(auto_sync_interval_min, 0),
                               created_by, created_at
                        FROM sql_connections ORDER BY name";
    await using var r = await cmd.ExecuteReaderAsync(ct);
    var items = new List<object>();
    while (await r.ReadAsync(ct))
        items.Add(new {
            id                  = r.GetInt32(0),
            name                = r.GetString(1),
            dbType              = r.GetString(2),
            host                = r.GetString(3),
            port                = r.GetInt32(4),
            database            = r.GetString(5),
            username            = r.GetString(6),
            queryTimeoutSec     = r.GetInt32(7),
            autoSyncIntervalMin = r.GetInt32(8),
            createdBy           = r.GetString(9),
            createdAt           = r.GetDateTime(10),
        });
    return Results.Ok(items);
});

// GET /api/admin/sql-connections/{id} — single record (surgical refresh)
app.MapGet("/api/admin/sql-connections/{id:int}", [Authorize("AdminOnly")] async (
    int id, NpgsqlDataSource ds, CancellationToken ct) =>
{
    await using var conn = await ds.OpenConnectionAsync(ct);
    var rec = await ReadSqlConnectionRecord(conn, id, ct);
    return rec == null ? Results.NotFound() : Results.Ok(rec);
});

// POST /api/admin/sql-connections — create
app.MapPost("/api/admin/sql-connections", [Authorize("AdminOnly")] async (
    [FromBody] SqlConnectionUpsertRequest req,
    ClaimsPrincipal user,
    ISqlConnectionService svc,
    NpgsqlDataSource ds,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Host) || string.IsNullOrWhiteSpace(req.Database))
        return Results.BadRequest(new { error = "Name, Host, Database are required" });

    DbType dbType;
    try { dbType = ParseDbType(req.DbType); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

    var username = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
    var port     = req.Port > 0 ? req.Port : svc.DefaultPort(dbType);
    var encrypted = string.IsNullOrEmpty(req.Password) ? "" : svc.Encrypt(req.Password);

    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = @"INSERT INTO sql_connections
                          (name, db_type, host, port, database, username, encrypted_password,
                           query_timeout_sec, auto_sync_interval_min, created_by)
                        VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10) RETURNING id";
    cmd.Parameters.AddWithValue(req.Name.Trim());
    cmd.Parameters.AddWithValue(DbTypeToStr(dbType));
    cmd.Parameters.AddWithValue(req.Host.Trim());
    cmd.Parameters.AddWithValue(port);
    cmd.Parameters.AddWithValue(req.Database.Trim());
    cmd.Parameters.AddWithValue(req.Username?.Trim() ?? "");
    cmd.Parameters.AddWithValue(encrypted);
    cmd.Parameters.AddWithValue(Math.Clamp(req.QueryTimeoutSec, 5, 3600));
    cmd.Parameters.AddWithValue(Math.Max(0, req.AutoSyncIntervalMin));
    cmd.Parameters.AddWithValue(username);
    var id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    _ = LogActivity(ds, username, "sql.connection.create", req.Name.Trim(), $"db={DbTypeToStr(dbType)} host={req.Host}");
    // Return full record so frontend can patch list state without refetch
    var rec = await ReadSqlConnectionRecord(conn, id, ct);
    return Results.Ok(rec);
});

// PUT /api/admin/sql-connections/{id} — update (password optional)
app.MapPut("/api/admin/sql-connections/{id:int}", [Authorize("AdminOnly")] async (
    int id, [FromBody] SqlConnectionUpsertRequest req,
    ClaimsPrincipal user, ISqlConnectionService svc, NpgsqlDataSource ds, CancellationToken ct) =>
{
    DbType dbType;
    try { dbType = ParseDbType(req.DbType); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

    var username = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
    var port     = req.Port > 0 ? req.Port : svc.DefaultPort(dbType);

    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    var qts = Math.Clamp(req.QueryTimeoutSec, 5, 3600);
    var asi = Math.Max(0, req.AutoSyncIntervalMin);
    if (string.IsNullOrEmpty(req.Password))
    {
        cmd.CommandText = @"UPDATE sql_connections
                            SET name=$1, db_type=$2, host=$3, port=$4, database=$5, username=$6,
                                query_timeout_sec=$7, auto_sync_interval_min=$8, updated_at=NOW()
                            WHERE id=$9";
        cmd.Parameters.AddWithValue(req.Name.Trim());
        cmd.Parameters.AddWithValue(DbTypeToStr(dbType));
        cmd.Parameters.AddWithValue(req.Host.Trim());
        cmd.Parameters.AddWithValue(port);
        cmd.Parameters.AddWithValue(req.Database.Trim());
        cmd.Parameters.AddWithValue(req.Username?.Trim() ?? "");
        cmd.Parameters.AddWithValue(qts);
        cmd.Parameters.AddWithValue(asi);
        cmd.Parameters.AddWithValue(id);
    }
    else
    {
        cmd.CommandText = @"UPDATE sql_connections
                            SET name=$1, db_type=$2, host=$3, port=$4, database=$5, username=$6,
                                encrypted_password=$7, query_timeout_sec=$8, auto_sync_interval_min=$9, updated_at=NOW()
                            WHERE id=$10";
        cmd.Parameters.AddWithValue(req.Name.Trim());
        cmd.Parameters.AddWithValue(DbTypeToStr(dbType));
        cmd.Parameters.AddWithValue(req.Host.Trim());
        cmd.Parameters.AddWithValue(port);
        cmd.Parameters.AddWithValue(req.Database.Trim());
        cmd.Parameters.AddWithValue(req.Username?.Trim() ?? "");
        cmd.Parameters.AddWithValue(svc.Encrypt(req.Password));
        cmd.Parameters.AddWithValue(qts);
        cmd.Parameters.AddWithValue(asi);
        cmd.Parameters.AddWithValue(id);
    }

    var rows = await cmd.ExecuteNonQueryAsync(ct);
    if (rows == 0) return Results.NotFound();
    _ = LogActivity(ds, username, "sql.connection.update", req.Name.Trim());
    var rec = await ReadSqlConnectionRecord(conn, id, ct);
    return Results.Ok(rec);
});

// DELETE /api/admin/sql-connections/{id}
app.MapDelete("/api/admin/sql-connections/{id:int}", [Authorize("AdminOnly")] async (
    int id, ClaimsPrincipal user, NpgsqlDataSource ds, CancellationToken ct) =>
{
    var username = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM sql_connections WHERE id=$1";
    cmd.Parameters.AddWithValue(id);
    await cmd.ExecuteNonQueryAsync(ct);
    _ = LogActivity(ds, username, "sql.connection.delete", $"id={id}");
    return Results.NoContent();
});

// POST /api/admin/sql-connections/{id}/test — test connection using stored password
app.MapPost("/api/admin/sql-connections/{id:int}/test", [Authorize("AdminOnly")] async (
    int id, ISqlConnectionService svc, NpgsqlDataSource ds,
    Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
    System.Security.Claims.ClaimsPrincipal user, IEventLog evt, CancellationToken ct) =>
{
    var who = user.FindFirstValue(ClaimTypes.Name) ?? "anon";
    if (!SqlConnTestRateLimit.TryAcquire(cache, who, out var retryAfter))
    {
        await evt.SecurityAsync("security.rate_limit", "sql-conn-test", new { retryAfter, connectionId = id }, ct);
        return Results.Json(new { ok = false, error = $"Çok fazla istek. {retryAfter}sn sonra tekrar deneyin." }, statusCode: 429);
    }

    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = "SELECT db_type, host, port, database, username, encrypted_password, COALESCE(query_timeout_sec, 120) FROM sql_connections WHERE id=$1";
    cmd.Parameters.AddWithValue(id);
    await using var r = await cmd.ExecuteReaderAsync(ct);
    if (!await r.ReadAsync(ct)) return Results.NotFound();

    var dbType   = ParseDbType(r.GetString(0));
    var host     = r.GetString(1);
    var port     = r.GetInt32(2);
    var database = r.GetString(3);
    var username = r.GetString(4);
    var password = string.IsNullOrEmpty(r.GetString(5)) ? "" : svc.Decrypt(r.GetString(5));
    var qts      = r.GetInt32(6);
    await r.CloseAsync();

    var err = await svc.TestConnectionAsync(dbType, host, port, database, username, password, ct, qts);
    return err == null
        ? Results.Ok(new { ok = true })
        : Results.Ok(new { ok = false, error = err });
});

// GET /api/admin/sql-connections/{id}/ingested-stats — what's currently in RAG for this connection
app.MapGet("/api/admin/sql-connections/{id:int}/ingested-stats", [Authorize("AdminOnly")] async (
    int id, NpgsqlDataSource ds, CancellationToken ct) =>
{
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT object_type, COUNT(*) AS cnt, SUM(chunks_count) AS chunks, MAX(last_ingested_at) AS last_at,
               MAX(collection) AS coll
        FROM sql_ingested_objects WHERE connection_id=$1
        GROUP BY object_type ORDER BY object_type";
    cmd.Parameters.AddWithValue(id);

    var byType = new List<object>();
    long total = 0; long totalChunks = 0;
    DateTime? lastIngestedAt = null; string? collection = null;
    await using var r = await cmd.ExecuteReaderAsync(ct);
    while (await r.ReadAsync(ct))
    {
        var t = r.GetString(0);
        var c = r.GetInt64(1);
        var ch = r.GetInt64(2);
        var d = r.IsDBNull(3) ? (DateTime?)null : r.GetDateTime(3);
        var col = r.IsDBNull(4) ? null : r.GetString(4);
        byType.Add(new { type = t, count = c, chunks = ch });
        total += c; totalChunks += ch;
        if (d.HasValue && (!lastIngestedAt.HasValue || d > lastIngestedAt)) lastIngestedAt = d;
        if (collection is null && col is not null) collection = col;
    }
    return Results.Ok(new { total, chunks = totalChunks, byType, lastIngestedAt, collection });
});

// POST /api/admin/sql-connections/{id}/list-objects — preview before ingest
app.MapPost("/api/admin/sql-connections/{id:int}/list-objects", [Authorize("AdminOnly")] async (
    int id, ISqlConnectionService svc, NpgsqlDataSource ds, CancellationToken ct) =>
{
    var (dbType, connStr) = await LoadConnectionAsync(id, svc, ds, ct);
    if (connStr is null) return Results.NotFound();

    try
    {
        var provider = SqlSchemaProviderFactory.Get(dbType);
        var objects = await provider.ListObjectsAsync(connStr, null, ct);
        var grouped = objects.GroupBy(o => o.TypeStr)
            .Select(g => new { type = g.Key, count = g.Count() }).ToList();
        return Results.Ok(new { total = objects.Count, byType = grouped });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Helper — SHA256 hex of a string
static string Sha256(string s)
{
    using var sha = System.Security.Cryptography.SHA256.Create();
    var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
    return Convert.ToHexString(bytes).ToLowerInvariant();
}

// POST /api/admin/sql-connections/{id}/ingest-schema — enqueue background job
app.MapPost("/api/admin/sql-connections/{id:int}/ingest-schema", [Authorize("AdminOnly")] async (
    int id, [FromBody] SchemaIngestRequest req, IJobService jobs, ClaimsPrincipal user, CancellationToken ct) =>
{
    var username = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
    var jobId = await jobs.EnqueueAsync("sql.ingest-schema", new SqlIngestSchemaParams(
        ConnectionId: id,
        Collection:   string.IsNullOrWhiteSpace(req.Collection) ? "sql-schema" : req.Collection.Trim(),
        IncludeTypes: req.IncludeTypes), username, ct);
    return Results.Ok(new { jobId });
});

// Legacy synchronous version moved to /sync (kept for any remaining direct calls)
app.MapPost("/api/admin/sql-connections/{id:int}/ingest-schema-sync", [Authorize("AdminOnly")] async (
    int id,
    [FromBody] SchemaIngestRequest req,
    ISqlConnectionService svc,
    IDocumentIngestion ingestion,
    ClaimsPrincipal user,
    NpgsqlDataSource ds,
    CancellationToken ct) =>
{
    var (dbType, connStr) = await LoadConnectionAsync(id, svc, ds, ct);
    if (connStr is null) return Results.NotFound();

    var username = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
    var collection = string.IsNullOrWhiteSpace(req.Collection) ? "sql-schema" : req.Collection.Trim();

    HashSet<DbObjectType>? includeTypes = null;
    if (req.IncludeTypes is { Length: > 0 })
    {
        includeTypes = new HashSet<DbObjectType>();
        foreach (var s in req.IncludeTypes)
            if (Enum.TryParse<DbObjectType>(s, true, out var t)) includeTypes.Add(t);
    }

    try
    {
        var provider = SqlSchemaProviderFactory.Get(dbType);
        var objects  = await provider.ListObjectsAsync(connStr, includeTypes, ct);

        var failures = new List<object>();
        var successCount = 0;
        var totalChunks  = 0;

        foreach (var obj in objects)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var ddl = await provider.GetCreateScriptAsync(connStr, obj, ct);
                if (string.IsNullOrWhiteSpace(ddl))
                {
                    failures.Add(new { name = obj.QualifiedName, error = "empty DDL" });
                    continue;
                }

                var source = $"sql://{dbType.ToString().ToLower()}/conn-{id}/{obj.TypeStr}/{obj.QualifiedName}";
                var title  = $"{obj.TypeStr.ToUpperInvariant()} {obj.QualifiedName}";
                var meta   = $"{{\"db_type\":\"{dbType.ToString().ToLower()}\",\"object_type\":\"{obj.TypeStr}\",\"schema\":\"{obj.Schema}\",\"name\":\"{obj.Name}\",\"connection_id\":{id}}}";

                // Delete any existing chunks for this source (idempotent re-ingest)
                await ingestion.DeleteSourceAsync(collection, source, ct);

                var result = await ingestion.IngestAsync(new IngestRequest
                {
                    Collection   = collection,
                    Source       = source,
                    Title        = title,
                    Content      = ddl,
                    Metadata     = meta,
                    ChunkSize    = 1600,
                    ChunkOverlap = 200,
                }, ct);

                // Upsert tracking record
                var hash = Sha256(ddl);
                await using var trackConn = await ds.OpenConnectionAsync(ct);
                await using var trackCmd  = trackConn.CreateCommand();
                trackCmd.CommandText = @"
                    INSERT INTO sql_ingested_objects (connection_id, collection, object_type, schema_name, object_name, source, ddl_hash, chunks_count, last_ingested_at)
                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8,NOW())
                    ON CONFLICT (connection_id, object_type, schema_name, object_name) DO UPDATE
                    SET collection=$2, source=$6, ddl_hash=$7, chunks_count=$8, last_ingested_at=NOW()";
                trackCmd.Parameters.AddWithValue(id);
                trackCmd.Parameters.AddWithValue(collection);
                trackCmd.Parameters.AddWithValue(obj.TypeStr);
                trackCmd.Parameters.AddWithValue(obj.Schema);
                trackCmd.Parameters.AddWithValue(obj.Name);
                trackCmd.Parameters.AddWithValue(source);
                trackCmd.Parameters.AddWithValue(hash);
                trackCmd.Parameters.AddWithValue(result.ChunksCreated);
                await trackCmd.ExecuteNonQueryAsync(ct);

                successCount++;
                totalChunks += result.ChunksCreated;
            }
            catch (Exception ex)
            {
                failures.Add(new { name = obj.QualifiedName, error = ex.Message });
            }
        }

        _ = LogActivity(ds, username, "sql.ingest.schema", $"conn-{id}",
            $"collection={collection} total={objects.Count} success={successCount} chunks={totalChunks}");

        return Results.Ok(new {
            total       = objects.Count,
            success     = successCount,
            chunks      = totalChunks,
            failures,
            collection,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// GET /api/admin/sql-connections/{id}/tables — list tables with columns + row count
app.MapGet("/api/admin/sql-connections/{id:int}/tables", [Authorize("AdminOnly")] async (
    int id, ISqlConnectionService svc, NpgsqlDataSource ds, CancellationToken ct) =>
{
    var (dbType, connStr) = await LoadConnectionAsync(id, svc, ds, ct);
    if (connStr is null) return Results.NotFound();

    try
    {
        var tables = await SqlDataSampler.ListTablesAsync(dbType, connStr, ct);
        return Results.Ok(tables.Select(t => new {
            schema        = t.Schema,
            name          = t.Name,
            estimatedRows = t.EstimatedRows,
            columns       = t.Columns.Select(c => new { name = c.Name, dataType = c.DataType, isPII = c.IsPII }),
        }));
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// POST /api/admin/sql-connections/{id}/ingest-data — enqueue background data sampling
app.MapPost("/api/admin/sql-connections/{id:int}/ingest-data", [Authorize("AdminOnly")] async (
    int id, [FromBody] DataIngestRequest req, IJobService jobs, ClaimsPrincipal user, CancellationToken ct) =>
{
    var username = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
    var tables = req.Tables?.Select(t => new SqlTableSpecDto(t.Schema, t.Name, t.Limit, t.Where)).ToArray()
                 ?? Array.Empty<SqlTableSpecDto>();
    if (tables.Length == 0) return Results.BadRequest(new { error = "At least one table required" });

    var jobId = await jobs.EnqueueAsync("sql.ingest-data", new SqlIngestDataParams(
        ConnectionId: id,
        Collection:   string.IsNullOrWhiteSpace(req.Collection) ? "sql-data" : req.Collection.Trim(),
        DefaultLimit: req.DefaultLimit,
        Tables:       tables), username, ct);
    return Results.Ok(new { jobId });
});

// Legacy synchronous version
app.MapPost("/api/admin/sql-connections/{id:int}/ingest-data-sync", [Authorize("AdminOnly")] async (
    int id,
    [FromBody] DataIngestRequest req,
    ISqlConnectionService svc,
    IDocumentIngestion ingestion,
    ClaimsPrincipal user,
    NpgsqlDataSource ds,
    CancellationToken ct) =>
{
    var (dbType, connStr) = await LoadConnectionAsync(id, svc, ds, ct);
    if (connStr is null) return Results.NotFound();

    var username   = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
    var collection = string.IsNullOrWhiteSpace(req.Collection) ? "sql-data" : req.Collection.Trim();
    var defaultLimit = req.DefaultLimit > 0 ? req.DefaultLimit : 1000;

    if (req.Tables is null || req.Tables.Length == 0)
        return Results.BadRequest(new { error = "At least one table required" });

    var success = 0;
    var totalRows = 0;
    var totalChunks = 0;
    var failures = new List<object>();

    foreach (var t in req.Tables)
    {
        ct.ThrowIfCancellationRequested();
        var sampleReq = new TableSampleRequest(
            t.Schema, t.Name,
            t.Limit > 0 ? t.Limit : defaultLimit,
            t.Where);

        try
        {
            var (cols, rows) = await SqlDataSampler.SampleAsync(dbType, connStr, sampleReq, ct);
            if (rows.Count == 0)
            {
                failures.Add(new { name = $"{t.Schema}.{t.Name}", error = "no rows returned" });
                continue;
            }

            var md     = SqlDataSampler.FormatAsMarkdown(t.Schema, t.Name, cols, rows);
            var source = $"sql://{dbType.ToString().ToLower()}/conn-{id}/data/{t.Schema}.{t.Name}";
            var title  = $"DATA {t.Schema}.{t.Name} ({rows.Count} rows)";
            var meta   = $"{{\"db_type\":\"{dbType.ToString().ToLower()}\",\"object_type\":\"data\",\"schema\":\"{t.Schema}\",\"name\":\"{t.Name}\",\"rows\":{rows.Count},\"connection_id\":{id}}}";

            // Idempotent: remove old chunks first
            await ingestion.DeleteSourceAsync(collection, source, ct);

            var result = await ingestion.IngestAsync(new IngestRequest
            {
                Collection = collection, Source = source, Title = title,
                Content    = md, Metadata = meta,
                ChunkSize  = 2000, ChunkOverlap = 100,
            }, ct);

            success++;
            totalRows   += rows.Count;
            totalChunks += result.ChunksCreated;
        }
        catch (Exception ex)
        {
            failures.Add(new { name = $"{t.Schema}.{t.Name}", error = ex.Message });
        }
    }

    _ = LogActivity(ds, username, "sql.ingest.data", $"conn-{id}",
        $"collection={collection} tables={req.Tables.Length} success={success} rows={totalRows}");

    return Results.Ok(new {
        success,
        total = req.Tables.Length,
        rows  = totalRows,
        chunks = totalChunks,
        failures,
        collection,
    });
});

// POST /api/admin/sql-connections/{id}/sync-schema — enqueue background sync job
app.MapPost("/api/admin/sql-connections/{id:int}/sync-schema", [Authorize("AdminOnly")] async (
    int id, IJobService jobs, ClaimsPrincipal user, CancellationToken ct) =>
{
    var username = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
    var jobId = await jobs.EnqueueAsync("sql.sync-schema", new SqlSyncSchemaParams(id), username, ct);
    return Results.Ok(new { jobId });
});

// Legacy synchronous (kept under /sync-schema-sync for back-compat)
app.MapPost("/api/admin/sql-connections/{id:int}/sync-schema-sync", [Authorize("AdminOnly")] async (
    int id,
    ISqlConnectionService svc,
    IDocumentIngestion ingestion,
    ClaimsPrincipal user,
    NpgsqlDataSource ds,
    CancellationToken ct) =>
{
    var (dbType, connStr) = await LoadConnectionAsync(id, svc, ds, ct);
    if (connStr is null) return Results.NotFound();

    var username = user.FindFirstValue(ClaimTypes.Name) ?? "admin";

    // Load existing tracking records (collection comes from first row — assumed single per connection)
    var existing = new Dictionary<string, (string Source, string Hash, string Collection)>();
    string defaultCollection = "sql-schema";
    await using (var loadConn = await ds.OpenConnectionAsync(ct))
    await using (var loadCmd = loadConn.CreateCommand())
    {
        loadCmd.CommandText = "SELECT object_type, schema_name, object_name, source, ddl_hash, collection FROM sql_ingested_objects WHERE connection_id=$1";
        loadCmd.Parameters.AddWithValue(id);
        await using var r = await loadCmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var key = $"{r.GetString(0)}|{r.GetString(1)}|{r.GetString(2)}";
            existing[key] = (r.GetString(3), r.GetString(4), r.GetString(5));
            defaultCollection = r.GetString(5);
        }
    }

    if (existing.Count == 0)
        return Results.BadRequest(new { error = "Bu bağlantı için henüz şema çıkarımı yapılmamış. Önce 'Şema Çıkar' kullanın." });

    try
    {
        var provider = SqlSchemaProviderFactory.Get(dbType);
        var currentObjects = await provider.ListObjectsAsync(connStr, null, ct);

        var added    = new List<string>();
        var updated  = new List<string>();
        var unchanged = 0;
        var failures = new List<object>();
        var currentKeys = new HashSet<string>();
        var totalChunks  = 0;

        foreach (var obj in currentObjects)
        {
            ct.ThrowIfCancellationRequested();
            var key = $"{obj.TypeStr}|{obj.Schema}|{obj.Name}";
            currentKeys.Add(key);

            try
            {
                var ddl = await provider.GetCreateScriptAsync(connStr, obj, ct);
                if (string.IsNullOrWhiteSpace(ddl))
                {
                    failures.Add(new { name = obj.QualifiedName, error = "empty DDL" });
                    continue;
                }

                var hash   = Sha256(ddl);
                var source = $"sql://{dbType.ToString().ToLower()}/conn-{id}/{obj.TypeStr}/{obj.QualifiedName}";
                var title  = $"{obj.TypeStr.ToUpperInvariant()} {obj.QualifiedName}";
                var meta   = $"{{\"db_type\":\"{dbType.ToString().ToLower()}\",\"object_type\":\"{obj.TypeStr}\",\"schema\":\"{obj.Schema}\",\"name\":\"{obj.Name}\",\"connection_id\":{id}}}";

                if (existing.TryGetValue(key, out var prev))
                {
                    if (prev.Hash == hash)
                    {
                        unchanged++;
                        continue;  // no change
                    }
                    // Changed → delete old chunks first
                    await ingestion.DeleteSourceAsync(prev.Collection, prev.Source, ct);
                    updated.Add(obj.QualifiedName);
                }
                else
                {
                    added.Add(obj.QualifiedName);
                }

                var collection = existing.TryGetValue(key, out var p2) ? p2.Collection : defaultCollection;
                var result = await ingestion.IngestAsync(new IngestRequest
                {
                    Collection = collection, Source = source, Title = title,
                    Content    = ddl, Metadata = meta, ChunkSize = 1600, ChunkOverlap = 200,
                }, ct);
                totalChunks += result.ChunksCreated;

                // Upsert
                await using var trackConn = await ds.OpenConnectionAsync(ct);
                await using var trackCmd  = trackConn.CreateCommand();
                trackCmd.CommandText = @"
                    INSERT INTO sql_ingested_objects (connection_id, collection, object_type, schema_name, object_name, source, ddl_hash, chunks_count, last_ingested_at)
                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8,NOW())
                    ON CONFLICT (connection_id, object_type, schema_name, object_name) DO UPDATE
                    SET ddl_hash=$7, chunks_count=$8, last_ingested_at=NOW()";
                trackCmd.Parameters.AddWithValue(id);
                trackCmd.Parameters.AddWithValue(collection);
                trackCmd.Parameters.AddWithValue(obj.TypeStr);
                trackCmd.Parameters.AddWithValue(obj.Schema);
                trackCmd.Parameters.AddWithValue(obj.Name);
                trackCmd.Parameters.AddWithValue(source);
                trackCmd.Parameters.AddWithValue(hash);
                trackCmd.Parameters.AddWithValue(result.ChunksCreated);
                await trackCmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                failures.Add(new { name = obj.QualifiedName, error = ex.Message });
            }
        }

        // Detect removed: objects in tracking but not in current source
        var removed = new List<string>();
        foreach (var (key, val) in existing)
        {
            if (currentKeys.Contains(key)) continue;
            // No longer exists in source DB — delete from RAG + tracking
            try
            {
                await ingestion.DeleteSourceAsync(val.Collection, val.Source, ct);
                var parts = key.Split('|');
                await using var delConn = await ds.OpenConnectionAsync(ct);
                await using var delCmd  = delConn.CreateCommand();
                delCmd.CommandText = "DELETE FROM sql_ingested_objects WHERE connection_id=$1 AND object_type=$2 AND schema_name=$3 AND object_name=$4";
                delCmd.Parameters.AddWithValue(id);
                delCmd.Parameters.AddWithValue(parts[0]);
                delCmd.Parameters.AddWithValue(parts[1]);
                delCmd.Parameters.AddWithValue(parts[2]);
                await delCmd.ExecuteNonQueryAsync(ct);
                removed.Add($"{parts[1]}.{parts[2]}");
            }
            catch { /* swallow per-item */ }
        }

        _ = LogActivity(ds, username, "sql.sync.schema", $"conn-{id}",
            $"added={added.Count} updated={updated.Count} unchanged={unchanged} removed={removed.Count}");

        return Results.Ok(new {
            added, updated, removed,
            unchanged,
            chunks   = totalChunks,
            failures,
            collection = defaultCollection,
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Helper — loads connection by ID and returns (dbType, connectionString) or null
static async Task<(DbType dbType, string? connStr)> LoadConnectionAsync(
    int id, ISqlConnectionService svc, NpgsqlDataSource ds, CancellationToken ct)
{
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = "SELECT db_type, host, port, database, username, encrypted_password, COALESCE(query_timeout_sec, 120) FROM sql_connections WHERE id=$1";
    cmd.Parameters.AddWithValue(id);
    await using var r = await cmd.ExecuteReaderAsync(ct);
    if (!await r.ReadAsync(ct)) return (DbType.MsSql, null);

    var dbType   = r.GetString(0).ToLowerInvariant() switch
    {
        "mssql"    => DbType.MsSql,
        "postgres" => DbType.Postgres,
        "mysql"    => DbType.MySql,
        "oracle"   => DbType.Oracle,
        _          => DbType.MsSql,
    };
    var host     = r.GetString(1);
    var port     = r.GetInt32(2);
    var database = r.GetString(3);
    var username = r.GetString(4);
    var password = string.IsNullOrEmpty(r.GetString(5)) ? "" : svc.Decrypt(r.GetString(5));
    var queryTimeoutSec = r.GetInt32(6);
    var connStr  = svc.BuildConnectionString(dbType, host, port, database, username, password, queryTimeoutSec);
    return (dbType, connStr);
}

// POST /api/admin/sql-connections/test-credentials — test ad-hoc (e.g. before saving)
app.MapPost("/api/admin/sql-connections/test-credentials", [Authorize("AdminOnly")] async (
    [FromBody] SqlConnectionUpsertRequest req, ISqlConnectionService svc,
    Microsoft.Extensions.Caching.Memory.IMemoryCache cache,
    System.Security.Claims.ClaimsPrincipal user, IEventLog evt, CancellationToken ct) =>
{
    var who = user.FindFirstValue(ClaimTypes.Name) ?? "anon";
    if (!SqlConnTestRateLimit.TryAcquire(cache, who, out var retryAfter))
    {
        await evt.SecurityAsync("security.rate_limit", "sql-conn-test-cred", new { retryAfter, host = req.Host }, ct);
        return Results.Json(new { ok = false, error = $"Çok fazla istek. {retryAfter}sn sonra tekrar deneyin." }, statusCode: 429);
    }

    DbType dbType;
    try { dbType = ParseDbType(req.DbType); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

    var port = req.Port > 0 ? req.Port : svc.DefaultPort(dbType);
    var qts  = Math.Clamp(req.QueryTimeoutSec, 5, 3600);
    var err  = await svc.TestConnectionAsync(dbType, req.Host, port, req.Database, req.Username ?? "", req.Password ?? "", ct, qts);
    return err == null
        ? Results.Ok(new { ok = true })
        : Results.Ok(new { ok = false, error = err });
});

// =============================================================================
// ─── Background Jobs ──────────────────────────────────────────────────────────

// GET /api/jobs/{id} — single job status
app.MapGet("/api/jobs/{id:long}", [Authorize] async (long id, IJobService jobs, CancellationToken ct) =>
{
    var j = await jobs.GetAsync(id, ct);
    if (j is null) return Results.NotFound();
    return Results.Ok(SerializeJob(j));
});

// ═════════════════════════════════════════════════════════════════════════════
// SQL Table Groups CRUD
// ═════════════════════════════════════════════════════════════════════════════

app.MapGet("/api/admin/sql-connections/{id:int}/table-groups", [Authorize("AdminOnly")] async (
    int id, NpgsqlDataSource ds, CancellationToken ct) =>
{
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = "SELECT id, name, sort_order FROM sql_table_groups WHERE connection_id=$1 ORDER BY sort_order, name";
    cmd.Parameters.AddWithValue(id);
    var items = new List<object>();
    await using var r = await cmd.ExecuteReaderAsync(ct);
    while (await r.ReadAsync(ct))
        items.Add(new { id = r.GetInt32(0), name = r.GetString(1), sortOrder = r.GetInt32(2) });
    return Results.Ok(items);
});

app.MapPost("/api/admin/sql-connections/{id:int}/table-groups", [Authorize("AdminOnly")] async (
    int id, [FromBody] TableGroupUpsert req, NpgsqlDataSource ds, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Name required" });
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = "INSERT INTO sql_table_groups (connection_id, name, sort_order) VALUES ($1,$2,$3) RETURNING id";
    cmd.Parameters.AddWithValue(id); cmd.Parameters.AddWithValue(req.Name.Trim()); cmd.Parameters.AddWithValue(req.SortOrder);
    var gid = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    return Results.Ok(new { id = gid });
});

app.MapPut("/api/admin/sql-connections/{id:int}/table-groups/{gid:int}", [Authorize("AdminOnly")] async (
    int id, int gid, [FromBody] TableGroupUpsert req, NpgsqlDataSource ds, CancellationToken ct) =>
{
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = "UPDATE sql_table_groups SET name=$1, sort_order=$2 WHERE id=$3 AND connection_id=$4";
    cmd.Parameters.AddWithValue(req.Name.Trim()); cmd.Parameters.AddWithValue(req.SortOrder);
    cmd.Parameters.AddWithValue(gid); cmd.Parameters.AddWithValue(id);
    await cmd.ExecuteNonQueryAsync(ct);
    return Results.NoContent();
});

app.MapDelete("/api/admin/sql-connections/{id:int}/table-groups/{gid:int}", [Authorize("AdminOnly")] async (
    int id, int gid, NpgsqlDataSource ds, CancellationToken ct) =>
{
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using (var c1 = conn.CreateCommand())
    {
        c1.CommandText = "UPDATE sql_table_configs SET group_id=NULL WHERE group_id=$1 AND connection_id=$2";
        c1.Parameters.AddWithValue(gid); c1.Parameters.AddWithValue(id);
        await c1.ExecuteNonQueryAsync(ct);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM sql_table_groups WHERE id=$1 AND connection_id=$2";
    cmd.Parameters.AddWithValue(gid); cmd.Parameters.AddWithValue(id);
    await cmd.ExecuteNonQueryAsync(ct);
    return Results.NoContent();
});

// ═════════════════════════════════════════════════════════════════════════════
// SQL Table Configs (PK, CreatedAt, UpdatedAt, group assignment)
// ═════════════════════════════════════════════════════════════════════════════

app.MapGet("/api/admin/sql-connections/{id:int}/table-configs", [Authorize("AdminOnly")] async (
    int id, NpgsqlDataSource ds, CancellationToken ct) =>
{
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = @"SELECT id, schema_name, table_name, pk_col, created_col, updated_col,
                               row_limit, where_clause, included_columns, group_id, collection,
                               last_synced_at, last_max_updated_at,
                               last_sync_status, last_sync_added, last_sync_updated, last_sync_error
                        FROM sql_table_configs WHERE connection_id=$1 ORDER BY schema_name, table_name";
    cmd.Parameters.AddWithValue(id);
    var items = new List<object>();
    await using var r = await cmd.ExecuteReaderAsync(ct);
    while (await r.ReadAsync(ct))
    {
        string[] cols;
        try { cols = System.Text.Json.JsonSerializer.Deserialize<string[]>(r.GetString(8)) ?? Array.Empty<string>(); }
        catch { cols = Array.Empty<string>(); }
        items.Add(new {
            id              = r.GetInt32(0),
            schema          = r.GetString(1),
            table           = r.GetString(2),
            pkCol           = r.GetString(3),
            createdCol      = r.GetString(4),
            updatedCol      = r.GetString(5),
            rowLimit        = r.GetInt32(6),
            whereClause     = r.GetString(7),
            includedColumns = cols,
            groupId         = r.IsDBNull(9)  ? (int?)null     : r.GetInt32(9),
            collection      = r.GetString(10),
            lastSyncedAt    = r.IsDBNull(11) ? (DateTime?)null : r.GetDateTime(11),
            lastMaxUpdatedAt = r.IsDBNull(12) ? (DateTime?)null : r.GetDateTime(12),
            lastSyncStatus  = r.IsDBNull(13) ? null            : r.GetString(13),
            lastSyncAdded   = r.GetInt32(14),
            lastSyncUpdated = r.GetInt32(15),
            lastSyncError   = r.GetString(16),
        });
    }
    return Results.Ok(items);
});

app.MapPost("/api/admin/sql-connections/{id:int}/table-configs", [Authorize("AdminOnly")] async (
    int id, [FromBody] TableConfigUpsert req, NpgsqlDataSource ds, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Schema) || string.IsNullOrWhiteSpace(req.Table))
        return Results.BadRequest(new { error = "Schema, Table required" });
    var colsJson = System.Text.Json.JsonSerializer.Serialize(req.IncludedColumns ?? Array.Empty<string>());

    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = @"INSERT INTO sql_table_configs
        (connection_id, schema_name, table_name, pk_col, created_col, updated_col,
         row_limit, where_clause, included_columns, group_id, collection)
        VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
        ON CONFLICT (connection_id, schema_name, table_name) DO UPDATE
        SET pk_col=$4, created_col=$5, updated_col=$6, row_limit=$7,
            where_clause=$8, included_columns=$9, group_id=$10, collection=$11
        RETURNING id";
    cmd.Parameters.AddWithValue(id);
    cmd.Parameters.AddWithValue(req.Schema); cmd.Parameters.AddWithValue(req.Table);
    cmd.Parameters.AddWithValue(req.PkCol ?? ""); cmd.Parameters.AddWithValue(req.CreatedCol ?? "");
    cmd.Parameters.AddWithValue(req.UpdatedCol ?? ""); cmd.Parameters.AddWithValue(req.RowLimit > 0 ? req.RowLimit : 1000);
    cmd.Parameters.AddWithValue(req.WhereClause ?? ""); cmd.Parameters.AddWithValue(colsJson);
    cmd.Parameters.AddWithValue((object?)req.GroupId ?? DBNull.Value);
    cmd.Parameters.AddWithValue(req.Collection ?? "");
    var tid = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    return Results.Ok(new { id = tid });
});

app.MapDelete("/api/admin/sql-connections/{id:int}/table-configs/{tid:int}", [Authorize("AdminOnly")] async (
    int id, int tid, NpgsqlDataSource ds, CancellationToken ct) =>
{
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using (var c1 = conn.CreateCommand())
    {
        c1.CommandText = "DELETE FROM sql_ingested_rows WHERE table_config_id=$1";
        c1.Parameters.AddWithValue(tid);
        await c1.ExecuteNonQueryAsync(ct);
    }
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM sql_table_configs WHERE id=$1 AND connection_id=$2";
    cmd.Parameters.AddWithValue(tid); cmd.Parameters.AddWithValue(id);
    await cmd.ExecuteNonQueryAsync(ct);
    return Results.NoContent();
});

// POST /api/admin/sql-connections/{id}/table-configs/bulk-assign-group — assign N tables to a group
// body: { tableConfigIds: number[], groupId: number|null }
app.MapPost("/api/admin/sql-connections/{id:int}/table-configs/bulk-assign-group",
    [Authorize("AdminOnly")] async (
        int id, [FromBody] BulkAssignGroupRequest req,
        NpgsqlDataSource ds, CancellationToken ct) =>
{
    if (req.TableConfigIds is null || req.TableConfigIds.Length == 0)
        return Results.BadRequest(new { error = "tableConfigIds boş olamaz" });
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = @"UPDATE sql_table_configs
                        SET group_id=$1
                        WHERE connection_id=$2 AND id = ANY($3)";
    cmd.Parameters.AddWithValue((object?)req.GroupId ?? DBNull.Value);
    cmd.Parameters.AddWithValue(id);
    cmd.Parameters.AddWithValue(req.TableConfigIds);
    var rows = await cmd.ExecuteNonQueryAsync(ct);
    return Results.Ok(new { updated = rows });
});

// POST /api/admin/sql-connections/{id}/sync-data — enqueue delta sync job
app.MapPost("/api/admin/sql-connections/{id:int}/sync-data", [Authorize("AdminOnly")] async (
    int id, [FromBody] SyncDataRequest? req, IJobService jobs, ClaimsPrincipal user, CancellationToken ct) =>
{
    var username = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
    var jobId = await jobs.EnqueueAsync("sql.sync-data", new SqlSyncDataParams(id, req?.TableConfigIds), username, ct);
    return Results.Ok(new { jobId });
});

// GET /api/admin/sql-connections/{id}/latest-job?type=... — latest job for this connection
app.MapGet("/api/admin/sql-connections/{id:int}/latest-job", [Authorize("AdminOnly")] async (
    int id, string? type, IJobService jobs, NpgsqlDataSource ds, CancellationToken ct) =>
{
    // Find latest job whose params JSON includes this connection id
    await using var conn = await ds.OpenConnectionAsync(ct);
    await using var cmd  = conn.CreateCommand();
    cmd.CommandText = string.IsNullOrEmpty(type)
        ? @"SELECT id FROM jobs WHERE params LIKE $1 ORDER BY id DESC LIMIT 1"
        : @"SELECT id FROM jobs WHERE params LIKE $1 AND job_type=$2 ORDER BY id DESC LIMIT 1";
    cmd.Parameters.AddWithValue($"%\"ConnectionId\":{id}%");
    if (!string.IsNullOrEmpty(type)) cmd.Parameters.AddWithValue(type);

    var result = await cmd.ExecuteScalarAsync(ct);
    if (result is null || result == DBNull.Value) return Results.Ok((object?)null);

    var jobId = Convert.ToInt64(result);
    var job   = await jobs.GetAsync(jobId, ct);
    return job is null ? Results.Ok((object?)null) : Results.Ok(SerializeJob(job));
});

// GET /api/jobs?limit=20&status=running
app.MapGet("/api/jobs", [Authorize("AdminOnly")] async (
    int? limit, string? status, IJobService jobs, CancellationToken ct) =>
{
    var list = await jobs.ListRecentAsync(limit ?? 20, status, ct);
    return Results.Ok(list.Select(SerializeJob));
});

// GET /api/admin/jobs?page=1&pageSize=50&type=&status= — paged list for Jobs tab
app.MapGet("/api/admin/jobs", [Authorize("AdminOnly")] async (
    int? page, int? pageSize, string? type, string? status,
    IJobService jobs, CancellationToken ct) =>
{
    var p  = Math.Max(1, page ?? 1);
    var ps = Math.Clamp(pageSize ?? 50, 10, 200);
    var (items, total) = await jobs.ListFilteredAsync(ps, (p - 1) * ps, type, status, ct);
    return Results.Ok(new {
        items = items.Select(SerializeJob),
        total,
        page = p,
        pageSize = ps,
    });
});

// POST /api/admin/jobs/{id}/cancel — only queued jobs can be cancelled
app.MapPost("/api/admin/jobs/{id:long}/cancel", [Authorize("AdminOnly")] async (
    long id, IJobService jobs, NpgsqlDataSource ds, ClaimsPrincipal user, CancellationToken ct) =>
{
    var ok = await jobs.CancelAsync(id, ct);
    if (ok)
    {
        var who = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
        _ = LogActivity(ds, who, "job.cancel", $"jobId={id}");
        return Results.Ok(new { ok = true });
    }
    return Results.BadRequest(new { ok = false, error = "Yalnızca 'queued' durumdaki işler iptal edilebilir." });
});

// POST /api/admin/jobs/{id}/retry — re-enqueue a failed/cancelled job with same params
app.MapPost("/api/admin/jobs/{id:long}/retry", [Authorize("AdminOnly")] async (
    long id, IJobService jobs, NpgsqlDataSource ds, ClaimsPrincipal user, CancellationToken ct) =>
{
    var who = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
    var newId = await jobs.RetryAsync(id, who, ct);
    if (newId is null)
        return Results.BadRequest(new { ok = false, error = "İş bulunamadı veya 'failed/cancelled' durumda değil." });
    _ = LogActivity(ds, who, "job.retry", $"oldId={id} newId={newId}");
    return Results.Ok(new { ok = true, newId });
});

static object SerializeJob(JobInfo j) => new {
    id          = j.Id,
    type        = j.Type,
    status      = j.Status.ToString().ToLowerInvariant(),
    progressCur = j.ProgressCur,
    progressTot = j.ProgressTot,
    message     = j.Message,
    createdBy   = j.CreatedBy,
    createdAt   = j.CreatedAt,
    startedAt   = j.StartedAt,
    completedAt = j.CompletedAt,
    error       = j.Error,
    result      = string.IsNullOrEmpty(j.ResultJson) ? null
                  : System.Text.Json.JsonSerializer.Deserialize<object>(j.ResultJson),
};

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
    LlmMetrics.RatingsTotal.WithLabels(req.Rating == 1 ? "up" : "down").Inc();
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
            LlmMetrics.IngestChunksTotal.WithLabels(collection).Inc(r.ChunksCreated);
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
// ─── Tools — File Generation (docx/xlsx/pdf/pptx via Python subprocess) ──────

// POST /api/tools/generate-file — agent tool entry point
// body: { kind: "docx"|"xlsx"|"pdf"|"pptx", filename: "report.docx", spec: {...} }
app.MapPost("/api/tools/generate-file", [Authorize] async (
    [FromBody] SetYazilim.Llm.Api.Tools.FileGenRequest req,
    SetYazilim.Llm.Api.Tools.IFileGenerator gen,
    ClaimsPrincipal user,
    IEventLog evt,
    CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(req.Kind))
        return Results.BadRequest(new { error = "kind required" });

    var allowed = new[] { "docx", "xlsx", "pdf", "pptx" };
    if (!allowed.Contains(req.Kind.ToLowerInvariant()))
        return Results.BadRequest(new { error = $"kind must be one of: {string.Join(",", allowed)}" });

    var username = user.FindFirstValue(ClaimTypes.Name) ?? "anon";
    var result   = await gen.GenerateAsync(username, req, ct);

    await evt.LogAsync(EventCategory.Data,
        result.Ok ? EventSeverity.Info : EventSeverity.Warn,
        $"file.generate.{req.Kind}",
        result.Ok ? EventResult.Success : EventResult.Failure,
        reason: result.Error,
        action: "generate", resource: $"{req.Kind}:{result.Filename}",
        details: new { result.SizeBytes, result.Token }, ct: ct);

    return Results.Ok(result);
});

// GET /api/tools/generated/{token}/{filename} — download a generated file (user-scoped)
app.MapGet("/api/tools/generated/{token}/{filename}", [Authorize] (
    string token, string filename,
    SetYazilim.Llm.Api.Tools.IFileGenerator gen,
    ClaimsPrincipal user) =>
{
    var username = user.FindFirstValue(ClaimTypes.Name) ?? "anon";
    var path     = gen.Resolve(username, token, filename);
    if (path == null) return Results.NotFound();
    return Results.File(path, SetYazilim.Llm.Api.Tools.ContentTypes.Lookup(filename),
        fileDownloadName: filename, enableRangeProcessing: true);
});

// =============================================================================
// ─── Admin Benchmark (LLM concurrency test) ──────────────────────────────────

// POST /api/admin/benchmark — run N concurrent /api/llm/completions calls
// body: { model, concurrency, prompt, maxTokens, temperature, label? }
app.MapPost("/api/admin/benchmark", [Authorize("AdminOnly")] async (
    [FromBody] SetYazilim.Llm.Api.Tools.BenchmarkRequest req,
    SetYazilim.Llm.Api.Tools.IBenchmarkService bench,
    HttpContext http,
    ClaimsPrincipal user,
    CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(req.Model)) return Results.BadRequest(new { error = "model required" });
    if (req.Concurrency < 1 || req.Concurrency > 200)
        return Results.BadRequest(new { error = "concurrency must be 1..200" });
    if (string.IsNullOrEmpty(req.Prompt)) return Results.BadRequest(new { error = "prompt required" });

    // Reuse the caller's JWT to call our own /api/llm/completions
    var auth = http.Request.Headers.Authorization.ToString();
    var jwt  = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..] : "";
    if (string.IsNullOrEmpty(jwt)) return Results.BadRequest(new { error = "missing bearer token" });

    var createdBy = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
    var result    = await bench.RunAsync(req, jwt, createdBy, ct);
    return Results.Ok(result);
});

// GET /api/admin/benchmarks?model=&limit=20
app.MapGet("/api/admin/benchmarks", [Authorize("AdminOnly")] async (
    string? model, int? limit,
    SetYazilim.Llm.Api.Tools.IBenchmarkService bench,
    CancellationToken ct) =>
{
    var items = await bench.ListAsync(model, limit ?? 20, ct);
    return Results.Ok(items);
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

    // Extract model name for metrics labels
    var metricModel = "unknown";
    try { if (System.Text.Json.JsonDocument.Parse(bodyStr).RootElement
              .TryGetProperty("model", out var mdl)) metricModel = mdl.GetString() ?? "unknown"; }
    catch { /* ignore */ }

    var metricsTimer = LlmMetrics.DurationSeconds.WithLabels(metricModel).NewTimer();

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
            metricsTimer.Dispose();
            LlmMetrics.RequestsTotal.WithLabels(metricModel, "warming").Inc();
            return Results.Json(
                new { error = "warming_up", message = "Model henüz yükleniyor, lütfen birkaç saniye sonra tekrar deneyin." },
                statusCode: 503);
        }

        // Other non-success: proxy as-is
        metricsTimer.Dispose();
        LlmMetrics.RequestsTotal.WithLabels(metricModel, "error").Inc();
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

    try
    {
        await resp.Content.CopyToAsync(http.Response.Body, ct);
        if (debug) app.Logger.LogInformation("[VISION {Rid}] B6. response streamed", rid);
        LlmMetrics.RequestsTotal.WithLabels(metricModel, "success").Inc();
    }
    catch (OperationCanceledException)
    {
        // Client disconnected — not an error
        LlmMetrics.RequestsTotal.WithLabels(metricModel, "cancelled").Inc();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "LLM stream error for model {Model} user {User}", metricModel, username);
        LlmMetrics.RequestsTotal.WithLabels(metricModel, "error").Inc();
    }
    finally
    {
        metricsTimer.Dispose();
    }
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

// ── Rate limit for SQL connection test endpoints ──────────────────────────────
// Max 10 tests per user per minute. Cheap in-memory sliding bucket.
static class SqlConnTestRateLimit
{
    private const int MaxPerMinute = 10;

    public static bool TryAcquire(Microsoft.Extensions.Caching.Memory.IMemoryCache cache, string user, out int retryAfterSec)
    {
        var key = $"sqlConnTest:{user}";
        var now = DateTimeOffset.UtcNow;
        // entry = (windowStart, count)
        (DateTimeOffset start, int count) entry;
        if (cache.TryGetValue(key, out object? raw) && raw is ValueTuple<DateTimeOffset, int> cached && (now - cached.Item1).TotalSeconds < 60)
        {
            entry = (cached.Item1, cached.Item2);
        }
        else
        {
            entry = (now, 0);
        }

        if (entry.count >= MaxPerMinute)
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
