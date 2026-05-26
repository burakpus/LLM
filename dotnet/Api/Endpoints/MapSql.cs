using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using SetYazilim.Llm;
using SetYazilim.Llm.Api.Auth;
using SetYazilim.Llm.Api.Jobs;
using SetYazilim.Llm.Api.Sql;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// /api/admin/sql-connections/* — connection CRUD, schema ingest/sync, data sampling,
/// table groups, table configs. The biggest endpoint file.
/// </summary>
public static class SqlEndpoints
{
    public static IEndpointRouteBuilder MapSql(this IEndpointRouteBuilder app)
    {
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
    _ = ActivityLogger.LogAsync(ds, username, "sql.connection.create", req.Name.Trim(), $"db={DbTypeToStr(dbType)} host={req.Host}");
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
    _ = ActivityLogger.LogAsync(ds, username, "sql.connection.update", req.Name.Trim());
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
    _ = ActivityLogger.LogAsync(ds, username, "sql.connection.delete", $"id={id}");
    return Results.NoContent();
});

// POST /api/admin/sql-connections/{id}/test — test connection using stored password
app.MapPost("/api/admin/sql-connections/{id:int}/test", [Authorize("AdminOnly")] async (
    int id, ISqlConnectionService svc, NpgsqlDataSource ds,
    Microsoft.Extensions.Caching.Memory.IMemoryCache cache, IConfiguration appCfg,
    System.Security.Claims.ClaimsPrincipal user, IEventLog evt, CancellationToken ct) =>
{
    var who = user.FindFirstValue(ClaimTypes.Name) ?? "anon";
    var sqlMax = appCfg.GetValue<int?>("Limits:SqlTestRateLimitPerMinute") ?? 10;
    if (!SqlConnTestRateLimit.TryAcquire(cache, who, sqlMax, out var retryAfter))
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

// POST /api/admin/sql-connections/{id}/sync-schema — enqueue background sync job
app.MapPost("/api/admin/sql-connections/{id:int}/sync-schema", [Authorize("AdminOnly")] async (
    int id, IJobService jobs, ClaimsPrincipal user, CancellationToken ct) =>
{
    var username = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
    var jobId = await jobs.EnqueueAsync("sql.sync-schema", new SqlSyncSchemaParams(id), username, ct);
    return Results.Ok(new { jobId });
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
    Microsoft.Extensions.Caching.Memory.IMemoryCache cache, IConfiguration appCfg,
    System.Security.Claims.ClaimsPrincipal user, IEventLog evt, CancellationToken ct) =>
{
    var who = user.FindFirstValue(ClaimTypes.Name) ?? "anon";
    var sqlMax = appCfg.GetValue<int?>("Limits:SqlTestRateLimitPerMinute") ?? 10;
    if (!SqlConnTestRateLimit.TryAcquire(cache, who, sqlMax, out var retryAfter))
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
    return job is null ? Results.Ok((object?)null) : Results.Ok(ActivityLogger.SerializeJob(job));
});

        return app;
    }
}
