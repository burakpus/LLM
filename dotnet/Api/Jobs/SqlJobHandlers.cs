using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SetYazilim.Llm.Api.Sql;
using SetYazilim.Llm.Retrieval;

namespace SetYazilim.Llm.Api.Jobs;

// ─────────────────────────────────────────────────────────────────────────────
// Shared DTOs (used by handlers + endpoints)
// ─────────────────────────────────────────────────────────────────────────────
public sealed record SqlIngestSchemaParams(int    ConnectionId, string Collection, string[]? IncludeTypes);
public sealed record SqlSyncSchemaParams(int      ConnectionId);
public sealed record SqlIngestDataParams(int      ConnectionId, string Collection, int DefaultLimit, SqlTableSpecDto[] Tables);
public sealed record SqlTableSpecDto(string Schema, string Name, int Limit, string? Where);
public sealed record SqlSyncDataParams(int  ConnectionId, int[]? TableConfigIds);  // null = tüm config'li tablolar
// Kullanıcı tarafından UI'da seçilmiş tabloların "schema.name" listesi.
// Boş/null gönderilirse handler default olarak satır sayısı top-100'ü uygular
// (geriye uyumluluk: API job'u doğrudan tetiklerse seçim olmadan da çalışır).
public sealed record SqlGenerateSkillParams(int ConnectionId, string[]? Tables = null);

// ─────────────────────────────────────────────────────────────────────────────
// Helper — sha256 hex
// ─────────────────────────────────────────────────────────────────────────────
static class Sha
{
    public static string Of(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Helper — load DB connection details
// ─────────────────────────────────────────────────────────────────────────────
static class SqlConnLoader
{
    public static async Task<(DbType DbType, string ConnStr)> LoadAsync(
        int id, ISqlConnectionService svc, NpgsqlDataSource ds, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"SELECT db_type, host, port, database, username, encrypted_password,
                                   COALESCE(query_timeout_sec, 120)
                            FROM sql_connections WHERE id=$1";
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException($"Connection #{id} not found");
        var dbType = r.GetString(0).ToLowerInvariant() switch {
            "mssql"=>DbType.MsSql, "postgres"=>DbType.Postgres, "mysql"=>DbType.MySql, "oracle"=>DbType.Oracle,
            _ => DbType.MsSql,
        };
        var host = r.GetString(1); var port = r.GetInt32(2);
        var db   = r.GetString(3); var user = r.GetString(4);
        var pwd  = string.IsNullOrEmpty(r.GetString(5)) ? "" : svc.Decrypt(r.GetString(5));
        var qts  = r.GetInt32(6);
        return (dbType, svc.BuildConnectionString(dbType, host, port, db, user, pwd, qts));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Handler 1: Initial schema ingest
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SqlIngestSchemaJobHandler : IJobHandler
{
    public string Type => "sql.ingest-schema";

    public async Task<object> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var p         = ctx.ParseParams<SqlIngestSchemaParams>();
        var svc       = ctx.Services.GetRequiredService<ISqlConnectionService>();
        var ingestion = ctx.Services.GetRequiredService<IDocumentIngestion>();
        var ds        = ctx.Services.GetRequiredService<NpgsqlDataSource>();

        var (dbType, connStr) = await SqlConnLoader.LoadAsync(p.ConnectionId, svc, ds, ct);
        // baseCollection is the user-provided collection name (or default).
        // Actual per-object collections become: {base}-tables, {base}-views, etc.
        var baseCollection = string.IsNullOrWhiteSpace(p.Collection) ? "sql-schema" : p.Collection.Trim();

        HashSet<DbObjectType>? include = null;
        if (p.IncludeTypes is { Length: > 0 })
        {
            include = new();
            foreach (var s in p.IncludeTypes)
                if (Enum.TryParse<DbObjectType>(s, true, out var t)) include.Add(t);
        }

        var provider = SqlSchemaProviderFactory.Get(dbType);
        await ctx.ReportProgressAsync(0, 1, "Objeler listeleniyor…", ct);
        var objects = await provider.ListObjectsAsync(connStr, include, ct);

        var failures = new List<object>();
        var success = 0; var totalChunks = 0;
        var typedCollections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < objects.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var obj = objects[i];
            await ctx.ReportProgressAsync(i, objects.Count, $"{obj.TypeStr}: {obj.QualifiedName}", ct);

            try
            {
                // ─── Build structured chunk(s) ──────────────────────────────────
                // Tables: structured (columns + FKs + indexes + tags) — one chunk
                // Procedures: section-aware split — possibly multiple chunks
                // Views/Functions/Triggers: DDL wrapped with header — one chunk
                var typedCollection = SqlSchemaChunkBuilder.GetCollectionName(baseCollection, obj.Type);
                typedCollections.Add(typedCollection);
                var source = $"sql://{dbType.ToString().ToLower()}/conn-{p.ConnectionId}/{obj.TypeStr}/{obj.QualifiedName}";
                var title  = $"{obj.TypeStr.ToUpperInvariant()} {obj.QualifiedName}";
                var meta   = $"{{\"db_type\":\"{dbType.ToString().ToLower()}\",\"object_type\":\"{obj.TypeStr}\",\"schema\":\"{obj.Schema}\",\"name\":\"{obj.Name}\",\"connection_id\":{p.ConnectionId}}}";

                List<string> chunks;
                string hashSource;   // text we hash for change-detection (raw DDL or structured form)

                // Side-effect collectors for Faz 4 TASK-4.3 — flushed to sql_object_relations
                // at the end of each iteration (per-object) to keep memory bounded.
                List<(string ts, string tn, string tt, string rel, string detail)>? relations = null;

                if (obj.Type == DbObjectType.Table)
                {
                    var ts = await provider.GetTableSchemaAsync(connStr, obj, ct);
                    if (ts is not null)
                    {
                        chunks = SqlSchemaChunkBuilder.BuildTableChunks(ts).ToList();
                        hashSource = string.Join("\n", chunks);

                        // Faz 4: outgoing FKs → sql_object_relations
                        relations = new();
                        foreach (var fk in ts.ForeignKeys)
                            relations.Add((fk.RefSchema, fk.RefTable, "TABLE", "fk",
                                           $"{fk.Column}->{fk.RefColumn}"));
                    }
                    else
                    {
                        var ddl = await provider.GetCreateScriptAsync(connStr, obj, ct);
                        if (string.IsNullOrWhiteSpace(ddl))
                        { failures.Add(new { name = obj.QualifiedName, error = "empty DDL" }); continue; }
                        chunks = new List<string> { SqlSchemaChunkBuilder.BuildDdlChunk(obj, ddl) };
                        hashSource = ddl;
                    }
                }
                else
                {
                    var ddl = await provider.GetCreateScriptAsync(connStr, obj, ct);
                    if (string.IsNullOrWhiteSpace(ddl))
                    { failures.Add(new { name = obj.QualifiedName, error = "empty DDL" }); continue; }

                    var deps = await provider.GetDependenciesAsync(connStr, obj, ct);

                    chunks = obj.Type == DbObjectType.Procedure
                        ? SqlSchemaChunkBuilder.BuildProcedureChunks(obj, ddl, deps).ToList()
                        : SqlSchemaChunkBuilder.BuildDdlChunkOrSplit(obj, ddl, deps).ToList();
                    hashSource = ddl;

                    // Faz 4: dependencies → sql_object_relations
                    relations = new(deps.Count);
                    var relType = obj.Type switch
                    {
                        DbObjectType.Procedure => "sp_uses",
                        DbObjectType.View      => "view_uses",
                        DbObjectType.Function  => "fn_uses",
                        DbObjectType.Trigger   => "trigger_on",
                        _                      => "uses",
                    };
                    foreach (var d in deps)
                        relations.Add((d.TargetSchema, d.TargetName, d.TargetType, relType, ""));
                }

                // Delete any old chunks for this (collection, source) before re-ingest.
                // Also try to clean up legacy single-collection entries and old-style
                // multi-part sources from previous ingest runs.
                await ingestion.DeleteSourceAsync(typedCollection, source, ct);
                if (typedCollection != baseCollection)
                    await ingestion.DeleteSourceAsync(baseCollection, source, ct);
                // Multi-part chunks (source#partN) — best-effort cleanup of up to 10 parts
                for (int oldPart = 1; oldPart <= 10; oldPart++)
                {
                    await ingestion.DeleteSourceAsync(typedCollection, $"{source}#part{oldPart}", ct);
                }

                // Ingest each chunk. ChunkSize=32000 forces single-pass insert
                // (our pre-built chunks are already sized to ~7000 chars max).
                int producedChunks = 0;
                for (int part = 0; part < chunks.Count; part++)
                {
                    var content = chunks[part];
                    var partSource = chunks.Count > 1 ? $"{source}#part{part + 1}" : source;
                    var partTitle  = chunks.Count > 1 ? $"{title} (part {part + 1}/{chunks.Count})" : title;
                    var r = await ingestion.IngestAsync(new IngestRequest
                    {
                        Collection = typedCollection,
                        Source     = partSource,
                        Title      = partTitle,
                        Content    = content,
                        Metadata   = meta,
                        ChunkSize  = 32000,    // never split — pre-chunked
                        ChunkOverlap = 0,
                    }, ct);
                    producedChunks += r.ChunksCreated;
                }

                var hash = Sha.Of(hashSource);
                await using var conn = await ds.OpenConnectionAsync(ct);
                await using var cmd  = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO sql_ingested_objects (connection_id, collection, object_type, schema_name, object_name, source, ddl_hash, chunks_count, last_ingested_at)
                                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8,NOW())
                                    ON CONFLICT (connection_id, object_type, schema_name, object_name) DO UPDATE
                                    SET collection=$2, source=$6, ddl_hash=$7, chunks_count=$8, last_ingested_at=NOW()";
                cmd.Parameters.AddWithValue(p.ConnectionId); cmd.Parameters.AddWithValue(typedCollection);
                cmd.Parameters.AddWithValue(obj.TypeStr); cmd.Parameters.AddWithValue(obj.Schema);
                cmd.Parameters.AddWithValue(obj.Name); cmd.Parameters.AddWithValue(source);
                cmd.Parameters.AddWithValue(hash); cmd.Parameters.AddWithValue(producedChunks);
                await cmd.ExecuteNonQueryAsync(ct);

                // Faz 4 TASK-4.3: flush per-object relations. Replace-all per source
                // object so re-ingest produces clean state.
                if (relations is not null)
                {
                    await using var delRel = conn.CreateCommand();
                    delRel.CommandText = @"DELETE FROM sql_object_relations
                                           WHERE connection_id=$1 AND source_schema=$2 AND source_object=$3";
                    delRel.Parameters.AddWithValue(p.ConnectionId);
                    delRel.Parameters.AddWithValue(obj.Schema);
                    delRel.Parameters.AddWithValue(obj.Name);
                    await delRel.ExecuteNonQueryAsync(ct);

                    foreach (var rel in relations)
                    {
                        try
                        {
                            await using var insRel = conn.CreateCommand();
                            insRel.CommandText = @"INSERT INTO sql_object_relations
                                (connection_id, source_schema, source_object, source_type,
                                 target_schema, target_object, target_type,
                                 relation_type, relation_detail)
                                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9)
                                ON CONFLICT DO NOTHING";
                            insRel.Parameters.AddWithValue(p.ConnectionId);
                            insRel.Parameters.AddWithValue(obj.Schema);
                            insRel.Parameters.AddWithValue(obj.Name);
                            insRel.Parameters.AddWithValue(obj.Type.ToString().ToUpperInvariant());
                            insRel.Parameters.AddWithValue(rel.ts);
                            insRel.Parameters.AddWithValue(rel.tn);
                            insRel.Parameters.AddWithValue(rel.tt);
                            insRel.Parameters.AddWithValue(rel.rel);
                            insRel.Parameters.AddWithValue(rel.detail);
                            await insRel.ExecuteNonQueryAsync(ct);
                        }
                        catch { /* non-fatal */ }
                    }
                }

                success++; totalChunks += producedChunks;
            }
            catch (Exception ex)
            {
                failures.Add(new { name = obj.QualifiedName, error = ex.Message });
            }
        }

        // ─── Seed collection_settings for each new typed collection ─────────────
        // Tables high-priority, views normal, procedures/functions/triggers low.
        // Existing rows are not overwritten — admin can re-tune via the UI.
        await SeedCollectionSettings(ds, typedCollections, baseCollection, ct);

        await ctx.ReportProgressAsync(objects.Count, objects.Count, "Tamamlandı", ct);
        return new {
            total       = objects.Count,
            success,
            chunks      = totalChunks,
            failures,
            collections = typedCollections.OrderBy(c => c).ToArray(),
        };
    }

    /// <summary>
    /// UPSERT collection_settings with sensible priority defaults:
    ///   tables=high, views=normal, procedures/functions/triggers=low.
    /// Only inserts if the row doesn't exist (ON CONFLICT DO NOTHING) so admin overrides survive.
    /// Also marks any legacy base-collection (single-bucket ingest) as 'hidden'
    /// so the typed collections naturally take over without retrieval pollution.
    /// </summary>
    private static async Task SeedCollectionSettings(
        NpgsqlDataSource ds, HashSet<string> typedCollections, string baseCollection, CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);

        foreach (var col in typedCollections)
        {
            var (priority, dataType, description) = ClassifyCollection(col);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO collection_settings (collection, priority, data_type, description, updated_at)
                                VALUES ($1, $2, $3, $4, NOW())
                                ON CONFLICT (collection) DO NOTHING";
            cmd.Parameters.AddWithValue(col);
            cmd.Parameters.AddWithValue(priority);
            cmd.Parameters.AddWithValue(dataType);
            cmd.Parameters.AddWithValue(description);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Demote the legacy base collection (raw single-bucket ingest) to hidden
        // so RAG retrieval prefers the new typed collections.
        if (!typedCollections.Contains(baseCollection))
        {
            await using var demote = conn.CreateCommand();
            demote.CommandText = @"INSERT INTO collection_settings (collection, priority, data_type, description, updated_at)
                                   VALUES ($1, 'hidden', 'sql-schema-legacy', 'Eski tek-bucket schema ingest — yeni typed collection''lar tarafından değiştirildi', NOW())
                                   ON CONFLICT (collection) DO UPDATE
                                   SET priority='hidden',
                                       data_type='sql-schema-legacy',
                                       updated_at=NOW()
                                   WHERE collection_settings.priority <> 'hidden'";
            demote.Parameters.AddWithValue(baseCollection);
            await demote.ExecuteNonQueryAsync(ct);
        }
    }

    private static (string priority, string dataType, string description) ClassifyCollection(string collection)
    {
        if (collection.EndsWith("-tables",     StringComparison.OrdinalIgnoreCase)) return ("high",   "sql-schema", "SQL Tabloları");
        if (collection.EndsWith("-views",      StringComparison.OrdinalIgnoreCase)) return ("normal", "sql-schema", "SQL Viewler");
        if (collection.EndsWith("-procedures", StringComparison.OrdinalIgnoreCase)) return ("low",    "sql-schema", "Stored Procedures");
        if (collection.EndsWith("-functions",  StringComparison.OrdinalIgnoreCase)) return ("low",    "sql-schema", "Fonksiyonlar");
        if (collection.EndsWith("-triggers",   StringComparison.OrdinalIgnoreCase)) return ("low",    "sql-schema", "Triggerlar");
        return ("normal", "sql-schema", "SQL Schema");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Handler 2: Sync (incremental) schema ingest
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SqlSyncSchemaJobHandler : IJobHandler
{
    public string Type => "sql.sync-schema";

    public async Task<object> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var p         = ctx.ParseParams<SqlSyncSchemaParams>();
        var svc       = ctx.Services.GetRequiredService<ISqlConnectionService>();
        var ingestion = ctx.Services.GetRequiredService<IDocumentIngestion>();
        var ds        = ctx.Services.GetRequiredService<NpgsqlDataSource>();

        var (dbType, connStr) = await SqlConnLoader.LoadAsync(p.ConnectionId, svc, ds, ct);

        // Load existing tracking
        var existing = new Dictionary<string, (string Source, string Hash, string Collection)>();
        string defaultColl = "sql-schema";
        await using (var c = await ds.OpenConnectionAsync(ct))
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT object_type, schema_name, object_name, source, ddl_hash, collection FROM sql_ingested_objects WHERE connection_id=$1";
            cmd.Parameters.AddWithValue(p.ConnectionId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var key = $"{r.GetString(0)}|{r.GetString(1)}|{r.GetString(2)}";
                existing[key] = (r.GetString(3), r.GetString(4), r.GetString(5));
                defaultColl = r.GetString(5);
            }
        }
        if (existing.Count == 0) throw new InvalidOperationException("İlk şema çıkarımı yapılmamış. Önce 'Şema' butonunu kullanın.");

        await ctx.ReportProgressAsync(0, 1, "Karşılaştırma yapılıyor…", ct);
        var provider = SqlSchemaProviderFactory.Get(dbType);
        var current  = await provider.ListObjectsAsync(connStr, null, ct);

        var added = new List<string>(); var updated = new List<string>();
        var unchanged = 0; var failures = new List<object>();
        var currentKeys = new HashSet<string>(); var totalChunks = 0;

        for (int i = 0; i < current.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var obj = current[i];
            var key = $"{obj.TypeStr}|{obj.Schema}|{obj.Name}";
            currentKeys.Add(key);
            await ctx.ReportProgressAsync(i, current.Count, $"{obj.TypeStr}: {obj.QualifiedName}", ct);

            try
            {
                // Build the same structured chunks as the initial ingest handler
                List<string> chunks;
                string hashSource;
                if (obj.Type == DbObjectType.Table)
                {
                    var ts = await provider.GetTableSchemaAsync(connStr, obj, ct);
                    if (ts is not null)
                    {
                        chunks = SqlSchemaChunkBuilder.BuildTableChunks(ts).ToList();
                        hashSource = string.Join("\n", chunks);
                    }
                    else
                    {
                        var ddl = await provider.GetCreateScriptAsync(connStr, obj, ct);
                        if (string.IsNullOrWhiteSpace(ddl))
                        { failures.Add(new { name = obj.QualifiedName, error = "empty DDL" }); continue; }
                        chunks = SqlSchemaChunkBuilder.BuildDdlChunkOrSplit(obj, ddl).ToList();
                        hashSource = ddl;
                    }
                }
                else
                {
                    var ddl = await provider.GetCreateScriptAsync(connStr, obj, ct);
                    if (string.IsNullOrWhiteSpace(ddl))
                    { failures.Add(new { name = obj.QualifiedName, error = "empty DDL" }); continue; }
                    var deps = await provider.GetDependenciesAsync(connStr, obj, ct);
                    chunks = obj.Type == DbObjectType.Procedure
                        ? SqlSchemaChunkBuilder.BuildProcedureChunks(obj, ddl, deps).ToList()
                        : SqlSchemaChunkBuilder.BuildDdlChunkOrSplit(obj, ddl, deps).ToList();
                    hashSource = ddl;
                }

                var hash       = Sha.Of(hashSource);
                var source     = $"sql://{dbType.ToString().ToLower()}/conn-{p.ConnectionId}/{obj.TypeStr}/{obj.QualifiedName}";
                // For NEW objects, derive collection from the (now expected typed) base.
                // For EXISTING objects, reuse the tracked collection — which should already be typed
                // after the user re-ran ingest-schema.
                var baseColl   = defaultColl;
                // If defaultColl looks like a legacy single-bucket name (no "-tables/-views/..." suffix),
                // derive the typed collection from it. Otherwise keep what's tracked.
                var collection = existing.TryGetValue(key, out var prev)
                    ? (LooksTyped(prev.Collection) ? prev.Collection : SqlSchemaChunkBuilder.GetCollectionName(StripTyped(prev.Collection), obj.Type))
                    : SqlSchemaChunkBuilder.GetCollectionName(StripTyped(baseColl), obj.Type);

                if (prev != default && prev.Hash == hash) { unchanged++; continue; }
                if (prev != default) { await ingestion.DeleteSourceAsync(prev.Collection, prev.Source, ct); updated.Add(obj.QualifiedName); }
                else added.Add(obj.QualifiedName);

                var title = $"{obj.TypeStr.ToUpperInvariant()} {obj.QualifiedName}";
                var meta  = $"{{\"db_type\":\"{dbType.ToString().ToLower()}\",\"object_type\":\"{obj.TypeStr}\",\"schema\":\"{obj.Schema}\",\"name\":\"{obj.Name}\",\"connection_id\":{p.ConnectionId}}}";

                int producedChunks = 0;
                for (int part = 0; part < chunks.Count; part++)
                {
                    var content = chunks[part];
                    var partSource = chunks.Count > 1 ? $"{source}#part{part + 1}" : source;
                    var partTitle  = chunks.Count > 1 ? $"{title} (part {part + 1}/{chunks.Count})" : title;
                    var r = await ingestion.IngestAsync(new IngestRequest {
                        Collection = collection, Source = partSource, Title = partTitle, Content = content,
                        Metadata = meta, ChunkSize = 32000, ChunkOverlap = 0,
                    }, ct);
                    producedChunks += r.ChunksCreated;
                }
                totalChunks += producedChunks;

                await using var conn = await ds.OpenConnectionAsync(ct);
                await using var cmd  = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO sql_ingested_objects (connection_id, collection, object_type, schema_name, object_name, source, ddl_hash, chunks_count, last_ingested_at)
                                    VALUES ($1,$2,$3,$4,$5,$6,$7,$8,NOW())
                                    ON CONFLICT (connection_id, object_type, schema_name, object_name) DO UPDATE
                                    SET collection=$2, source=$6, ddl_hash=$7, chunks_count=$8, last_ingested_at=NOW()";
                cmd.Parameters.AddWithValue(p.ConnectionId); cmd.Parameters.AddWithValue(collection);
                cmd.Parameters.AddWithValue(obj.TypeStr); cmd.Parameters.AddWithValue(obj.Schema);
                cmd.Parameters.AddWithValue(obj.Name); cmd.Parameters.AddWithValue(source);
                cmd.Parameters.AddWithValue(hash); cmd.Parameters.AddWithValue(producedChunks);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                failures.Add(new { name = obj.QualifiedName, error = ex.Message });
            }
        }

        // Helpers (local) — typed-collection naming heuristics
        static bool LooksTyped(string col) =>
            col.EndsWith("-tables",     StringComparison.OrdinalIgnoreCase) ||
            col.EndsWith("-views",      StringComparison.OrdinalIgnoreCase) ||
            col.EndsWith("-procedures", StringComparison.OrdinalIgnoreCase) ||
            col.EndsWith("-functions",  StringComparison.OrdinalIgnoreCase) ||
            col.EndsWith("-triggers",   StringComparison.OrdinalIgnoreCase);
        static string StripTyped(string col)
        {
            foreach (var s in new[] { "-tables", "-views", "-procedures", "-functions", "-triggers" })
                if (col.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return col[..^s.Length];
            return col;
        }

        // Removed objects
        var removed = new List<string>();
        foreach (var (key, val) in existing)
        {
            if (currentKeys.Contains(key)) continue;
            try
            {
                await ingestion.DeleteSourceAsync(val.Collection, val.Source, ct);
                var parts = key.Split('|');
                await using var conn = await ds.OpenConnectionAsync(ct);
                await using var cmd  = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM sql_ingested_objects WHERE connection_id=$1 AND object_type=$2 AND schema_name=$3 AND object_name=$4";
                cmd.Parameters.AddWithValue(p.ConnectionId);
                cmd.Parameters.AddWithValue(parts[0]); cmd.Parameters.AddWithValue(parts[1]); cmd.Parameters.AddWithValue(parts[2]);
                await cmd.ExecuteNonQueryAsync(ct);
                removed.Add($"{parts[1]}.{parts[2]}");
            }
            catch { }
        }

        await ctx.ReportProgressAsync(current.Count, current.Count, "Tamamlandı", ct);
        return new { added, updated, removed, unchanged, chunks = totalChunks, failures, collection = defaultColl };
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Handler 4: Delta data sync (incremental row-level)
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SqlSyncDataJobHandler : IJobHandler
{
    public string Type => "sql.sync-data";

    public async Task<object> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var p         = ctx.ParseParams<SqlSyncDataParams>();
        var svc       = ctx.Services.GetRequiredService<ISqlConnectionService>();
        var ingestion = ctx.Services.GetRequiredService<IDocumentIngestion>();
        var ds        = ctx.Services.GetRequiredService<NpgsqlDataSource>();

        var (dbType, connStr) = await SqlConnLoader.LoadAsync(p.ConnectionId, svc, ds, ct);

        // Load table configs
        var configs = new List<TableConfig>();
        await using (var c = await ds.OpenConnectionAsync(ct))
        await using (var cmd = c.CreateCommand())
        {
            var filterSql = (p.TableConfigIds is { Length: > 0 })
                ? " AND id = ANY($2)"
                : "";
            cmd.CommandText = @"SELECT id, connection_id, schema_name, table_name, pk_col, created_col, updated_col,
                                       row_limit, where_clause, included_columns, group_id, collection,
                                       last_synced_at, last_max_updated_at
                                FROM sql_table_configs
                                WHERE connection_id=$1" + filterSql;
            cmd.Parameters.AddWithValue(p.ConnectionId);
            if (p.TableConfigIds is { Length: > 0 }) cmd.Parameters.AddWithValue(p.TableConfigIds);

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                string[] cols;
                try { cols = System.Text.Json.JsonSerializer.Deserialize<string[]>(r.GetString(9)) ?? Array.Empty<string>(); }
                catch { cols = Array.Empty<string>(); }
                configs.Add(new TableConfig(
                    Id:               r.GetInt32(0),
                    ConnectionId:     r.GetInt32(1),
                    Schema:           r.GetString(2),
                    Table:            r.GetString(3),
                    PkCol:            r.GetString(4),
                    CreatedCol:       r.GetString(5),
                    UpdatedCol:       r.GetString(6),
                    RowLimit:         r.GetInt32(7),
                    WhereClause:      r.GetString(8),
                    IncludedColumns:  cols,
                    GroupId:          r.IsDBNull(10) ? null : r.GetInt32(10),
                    Collection:       r.GetString(11),
                    LastSyncedAt:     r.IsDBNull(12) ? null : r.GetDateTime(12),
                    LastMaxUpdatedAt: r.IsDBNull(13) ? null : r.GetDateTime(13)));
            }
        }

        if (configs.Count == 0)
            throw new InvalidOperationException("Bu bağlantı için yapılandırılmış tablo yok. Önce tablo ayarlarını yapın.");

        var totalAdded = 0; var totalUpdated = 0; var totalUnchanged = 0;
        var totalChunks = 0; var totalRowsProcessed = 0;
        var failures = new List<object>();
        var perTable = new List<object>();

        for (int idx = 0; idx < configs.Count; idx++)
        {
            ct.ThrowIfCancellationRequested();
            var cfg = configs[idx];
            await ctx.ReportProgressAsync(idx, configs.Count, $"Senkronize: {cfg.QualifiedName}", ct);

            int added = 0, updated = 0, unchanged = 0, chunks = 0;
            DateTime? newMaxUpdated = cfg.LastMaxUpdatedAt;
            try
            {
                if (cfg.PkCols.Length == 0)
                {
                    failures.Add(new { table = cfg.QualifiedName, error = "PK tanımlı değil" });
                    continue;
                }
                var collection = string.IsNullOrWhiteSpace(cfg.Collection) ? "sql-data" : cfg.Collection;

                var rows = await SqlDataDelta.FetchDeltaAsync(dbType, connStr, cfg, ct);

                // Load existing hashes for this table_config_id
                var existing = new Dictionary<string, (string Hash, string Source)>();
                await using (var c2 = await ds.OpenConnectionAsync(ct))
                await using (var c2cmd = c2.CreateCommand())
                {
                    c2cmd.CommandText = "SELECT pk_value, content_hash, source FROM sql_ingested_rows WHERE table_config_id=$1";
                    c2cmd.Parameters.AddWithValue(cfg.Id);
                    await using var rr = await c2cmd.ExecuteReaderAsync(ct);
                    while (await rr.ReadAsync(ct))
                        existing[rr.GetString(0)] = (rr.GetString(1), rr.GetString(2));
                }

                foreach (var row in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    var md   = SqlDataDelta.FormatRowAsMarkdown(cfg, row);
                    var hash = Sha.Of(md);
                    var safePk = row.PkValue.Replace('/', '_').Replace('#','-');
                    var source = $"sql://{dbType.ToString().ToLower()}/conn-{p.ConnectionId}/row/{cfg.Schema}.{cfg.Table}#{safePk}";

                    if (existing.TryGetValue(row.PkValue, out var prev))
                    {
                        if (prev.Hash == hash) { unchanged++; continue; }
                        await ingestion.DeleteSourceAsync(collection, prev.Source, ct);
                        updated++;
                    }
                    else added++;

                    var title = $"ROW {cfg.QualifiedName} {cfg.PkCol}={row.PkValue}";
                    var meta  = $"{{\"db_type\":\"{dbType.ToString().ToLower()}\",\"object_type\":\"row\",\"schema\":\"{cfg.Schema}\",\"table\":\"{cfg.Table}\",\"pk\":\"{row.PkValue.Replace("\"","\\\"")}\",\"connection_id\":{p.ConnectionId}}}";

                    var ingestResult = await ingestion.IngestAsync(new IngestRequest
                    {
                        Collection = collection, Source = source, Title = title,
                        Content = md, Metadata = meta,
                        // Large chunk size: each SQL row is logically one document.
                        // Data dictionary / table-summary rows can be 5-10K chars
                        // (aggregated column lists). Splitting fragments the answer
                        // and forces multiple chunks into top-K. 12000 ≈ 3K tokens —
                        // fits even very wide tables without splitting.
                        ChunkSize = 12000, ChunkOverlap = 0,
                    }, ct);
                    chunks += ingestResult.ChunksCreated;

                    // Upsert tracking
                    await using var trackConn = await ds.OpenConnectionAsync(ct);
                    await using var trackCmd  = trackConn.CreateCommand();
                    trackCmd.CommandText = @"INSERT INTO sql_ingested_rows (table_config_id, pk_value, content_hash, source, chunks_count, last_ingested_at)
                                             VALUES ($1,$2,$3,$4,$5,NOW())
                                             ON CONFLICT (table_config_id, pk_value) DO UPDATE
                                             SET content_hash=$3, source=$4, chunks_count=$5, last_ingested_at=NOW()";
                    trackCmd.Parameters.AddWithValue(cfg.Id);
                    trackCmd.Parameters.AddWithValue(row.PkValue);
                    trackCmd.Parameters.AddWithValue(hash);
                    trackCmd.Parameters.AddWithValue(source);
                    trackCmd.Parameters.AddWithValue(ingestResult.ChunksCreated);
                    await trackCmd.ExecuteNonQueryAsync(ct);

                    if (row.UpdatedAt.HasValue && (!newMaxUpdated.HasValue || row.UpdatedAt > newMaxUpdated))
                        newMaxUpdated = row.UpdatedAt;
                }

                // Update table config: last_synced_at + last_max_updated_at + per-table status
                await using (var upConn = await ds.OpenConnectionAsync(ct))
                await using (var upCmd  = upConn.CreateCommand())
                {
                    upCmd.CommandText = @"UPDATE sql_table_configs
                                          SET last_synced_at=NOW(), last_max_updated_at=$1,
                                              last_sync_status='ok', last_sync_added=$2, last_sync_updated=$3,
                                              last_sync_error=''
                                          WHERE id=$4";
                    upCmd.Parameters.AddWithValue((object?)newMaxUpdated ?? DBNull.Value);
                    upCmd.Parameters.AddWithValue(added);
                    upCmd.Parameters.AddWithValue(updated);
                    upCmd.Parameters.AddWithValue(cfg.Id);
                    await upCmd.ExecuteNonQueryAsync(ct);
                }

                totalAdded += added; totalUpdated += updated; totalUnchanged += unchanged;
                totalChunks += chunks; totalRowsProcessed += rows.Count;
                perTable.Add(new {
                    table = cfg.QualifiedName, added, updated, unchanged, chunks, rows = rows.Count,
                });
            }
            catch (Exception ex)
            {
                failures.Add(new { table = cfg.QualifiedName, error = ex.Message });
                // Record failure on the table config too
                try
                {
                    await using var upConn = await ds.OpenConnectionAsync(ct);
                    await using var upCmd  = upConn.CreateCommand();
                    upCmd.CommandText = @"UPDATE sql_table_configs
                                          SET last_synced_at=NOW(),
                                              last_sync_status='failed', last_sync_error=$1
                                          WHERE id=$2";
                    upCmd.Parameters.AddWithValue(ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message);
                    upCmd.Parameters.AddWithValue(cfg.Id);
                    await upCmd.ExecuteNonQueryAsync(ct);
                } catch { /* swallow — primary error already recorded */ }
            }
        }

        await ctx.ReportProgressAsync(configs.Count, configs.Count, "Tamamlandı", ct);
        return new {
            tables   = configs.Count,
            added    = totalAdded,
            updated  = totalUpdated,
            unchanged = totalUnchanged,
            chunks   = totalChunks,
            rows     = totalRowsProcessed,
            failures, perTable,
        };
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Handler 3: Data sampling
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SqlIngestDataJobHandler : IJobHandler
{
    public string Type => "sql.ingest-data";

    public async Task<object> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var p         = ctx.ParseParams<SqlIngestDataParams>();
        var svc       = ctx.Services.GetRequiredService<ISqlConnectionService>();
        var ingestion = ctx.Services.GetRequiredService<IDocumentIngestion>();
        var ds        = ctx.Services.GetRequiredService<NpgsqlDataSource>();

        var (dbType, connStr) = await SqlConnLoader.LoadAsync(p.ConnectionId, svc, ds, ct);
        var collection = string.IsNullOrWhiteSpace(p.Collection) ? "sql-data" : p.Collection.Trim();
        var defaultLimit = p.DefaultLimit > 0 ? p.DefaultLimit : 1000;

        var failures = new List<object>();
        var success = 0; var totalRows = 0; var totalChunks = 0;

        for (int i = 0; i < p.Tables.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = p.Tables[i];
            await ctx.ReportProgressAsync(i, p.Tables.Length, $"{t.Schema}.{t.Name}", ct);

            try
            {
                var req = new TableSampleRequest(t.Schema, t.Name, t.Limit > 0 ? t.Limit : defaultLimit, t.Where);
                var (cols, rows) = await SqlDataSampler.SampleAsync(dbType, connStr, req, ct);
                if (rows.Count == 0) { failures.Add(new { name = $"{t.Schema}.{t.Name}", error = "no rows" }); continue; }

                var md     = SqlDataSampler.FormatAsMarkdown(t.Schema, t.Name, cols, rows);
                var source = $"sql://{dbType.ToString().ToLower()}/conn-{p.ConnectionId}/data/{t.Schema}.{t.Name}";
                var title  = $"DATA {t.Schema}.{t.Name} ({rows.Count} rows)";
                var meta   = $"{{\"db_type\":\"{dbType.ToString().ToLower()}\",\"object_type\":\"data\",\"schema\":\"{t.Schema}\",\"name\":\"{t.Name}\",\"rows\":{rows.Count},\"connection_id\":{p.ConnectionId}}}";

                await ingestion.DeleteSourceAsync(collection, source, ct);
                var r = await ingestion.IngestAsync(new IngestRequest {
                    Collection = collection, Source = source, Title = title, Content = md,
                    // See above for rationale. Per-table sample DATA chunks can be
                    // larger than CREATE TABLE statements (especially with markdown
                    // tables of rows). 12000 chars ≈ 3K tokens.
                    Metadata = meta, ChunkSize = 12000, ChunkOverlap = 100,
                }, ct);
                success++; totalRows += rows.Count; totalChunks += r.ChunksCreated;
            }
            catch (Exception ex)
            {
                failures.Add(new { name = $"{t.Schema}.{t.Name}", error = ex.Message });
            }
        }

        await ctx.ReportProgressAsync(p.Tables.Length, p.Tables.Length, "Tamamlandı", ct);
        return new { total = p.Tables.Length, success, rows = totalRows, chunks = totalChunks, failures, collection };
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Handler 5: Generate SQL skill .md from connection schema (background)
// İzole: RAG yoluna dokunmaz. Top N tabloyu satır sayısına göre seçer, LiteLLM
// ile 6 bölümlü skill yazdırır, Skills/ klasörüne kaydeder, registry'yi reload eder.
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SqlGenerateSkillJobHandler : IJobHandler
{
    public string Type => "sql.generate-skill";

    public async Task<object> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var p   = ctx.ParseParams<SqlGenerateSkillParams>();
        var svc = ctx.Services.GetRequiredService<ISqlConnectionService>();
        var ds  = ctx.Services.GetRequiredService<NpgsqlDataSource>();
        var cfg = ctx.Services.GetRequiredService<IConfiguration>();
        var httpFactory = ctx.Services.GetRequiredService<IHttpClientFactory>();
        var env  = ctx.Services.GetRequiredService<IWebHostEnvironment>();
        var skills = ctx.Services.GetRequiredService<SetYazilim.Llm.Context.SkillRegistry>();

        // Connection adı (slug + doküman başlığı için)
        string? connName;
        await using (var conn = await ds.OpenConnectionAsync(ct))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sql_connections WHERE id=$1";
            cmd.Parameters.AddWithValue(p.ConnectionId);
            var v = await cmd.ExecuteScalarAsync(ct);
            connName = v as string;
        }
        if (connName is null) throw new InvalidOperationException($"Connection #{p.ConnectionId} not found");

        await ctx.ReportProgressAsync(0, 8, "Şema okunuyor…", ct);
        var (dbType, connStr) = await SqlConnLoader.LoadAsync(p.ConnectionId, svc, ds, ct);
        var allTables = await SqlDataSampler.ListTablesAsync(dbType, connStr, ct);
        if (allTables.Count == 0)
            throw new InvalidOperationException("Bu bağlantıda tablo bulunamadı");

        // Seçim mantığı: kullanıcı liste verdiyse onu kullan (UI'da onaylanmış);
        // vermediyse satır sayısına göre top-100 default uygulanır. View'lerin
        // EstimatedRows=0 olduğu için top-N default seçimi tabloları öne çeker —
        // bu kasıtlı (kullanım yoğun tabloları önceliklendir).
        List<TableInfo> picked;
        string pickedDesc;
        if (p.Tables is { Length: > 0 })
        {
            var requested = new HashSet<string>(p.Tables, StringComparer.OrdinalIgnoreCase);
            picked = allTables
                .Where(t => requested.Contains(t.QualifiedName))
                .ToList();
            pickedDesc = $"{picked.Count}/{p.Tables.Length} tablo kullanıcı tarafından seçildi";
            if (picked.Count == 0)
                throw new InvalidOperationException("Seçilen tablolar bağlantıda bulunamadı");
        }
        else
        {
            const int defaultTopN = 100;
            picked = allTables
                .OrderByDescending(t => t.EstimatedRows)
                .ThenBy(t => t.QualifiedName, StringComparer.OrdinalIgnoreCase)
                .Take(defaultTopN)
                .ToList();
            pickedDesc = $"{picked.Count}/{allTables.Count} tablo (satır sayısı top {defaultTopN})";
        }

        await ctx.ReportProgressAsync(1, 8, pickedDesc, ct);

        var schemaText = SqlSkillGenerator.BuildSchemaText(picked);

        // LiteLLM ayarları
        var liteLlmBase = cfg["LiteLLM:BaseUrl"] ?? "http://localhost:4000";
        var liteLlmKey  = cfg["LiteLLM:ApiKey"];
        var model       = cfg["SkillGen:Model"] ?? "chat";
        var http        = httpFactory.CreateClient();
        http.Timeout    = TimeSpan.FromMinutes(5);

        // 6 bölümü üretirken progress'e yansıt
        var skillMd = await SqlSkillGenerator.GenerateAsync(
            http, liteLlmBase, liteLlmKey, model, connName, schemaText,
            onSectionDone: async (idx, total, title) =>
            {
                // idx: 1-based, total: 6 (sabit). Progress 2..7 arası mapping.
                var cur = 1 + idx;
                await ctx.ReportProgressAsync(cur, 8, $"Bölüm {idx}/{total}: {title}", ct);
            },
            ct);

        // Skills/ yaz + registry hot-reload
        await ctx.ReportProgressAsync(7, 8, "Dosya yazılıyor…", ct);
        var skillsDir = skills.SkillsPath ?? Path.Combine(env.ContentRootPath, "Skills");
        Directory.CreateDirectory(skillsDir);
        var slug = System.Text.RegularExpressions.Regex.Replace(
            connName.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrEmpty(slug)) slug = "sql";
        var fileName  = $"{slug}-db-model.md";
        var skillPath = Path.Combine(skillsDir, fileName);

        await File.WriteAllTextAsync(skillPath, skillMd, ct);
        skills.LoadFromDirectory(skillsDir);

        await ctx.ReportProgressAsync(8, 8, "Tamamlandı", ct);
        return new
        {
            skillFile     = fileName,
            skillId       = Path.GetFileNameWithoutExtension(fileName),
            tablesPicked  = picked.Count,
            tablesTotal   = allTables.Count,
            chars         = skillMd.Length,
            selectionMode = p.Tables is { Length: > 0 } ? "user-selected" : "default-top100",
        };
    }
}
