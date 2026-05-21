using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;

namespace SetYazilim.Llm.VectorStore;

/// <summary>
/// pgvector-backed implementation of <see cref="IVectorStore"/>.
/// Uses Npgsql directly (no EF Core overhead).
/// </summary>
public sealed class PgVectorStore : IVectorStore
{
    private readonly NpgsqlDataSource _ds;
    private readonly VectorStoreOptions _opts;
    private readonly ILogger<PgVectorStore> _logger;

    public PgVectorStore(
        NpgsqlDataSource ds,
        IOptions<VectorStoreOptions> opts,
        ILogger<PgVectorStore> logger)
    {
        _ds     = ds;
        _opts   = opts.Value;
        _logger = logger;
    }

    // ── Schema ─────────────────────────────────────────────────────────────────

    public async Task EnsureCollectionAsync(string collection, int dimensions, CancellationToken ct = default)
    {
        var table = SanitiseName(collection);
        _logger.LogInformation("Ensuring vector collection '{Collection}' ({Dims} dims)", table, dimensions);

        await using var conn = await _ds.OpenConnectionAsync(ct);

        // Enable extension (idempotent)
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS vector;";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Create table
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                CREATE TABLE IF NOT EXISTS {table} (
                    id          UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
                    collection  TEXT         NOT NULL,
                    content     TEXT         NOT NULL,
                    metadata    JSONB        NOT NULL,
                    embedding   vector({dimensions}),
                    updated_at  TIMESTAMPTZ  NOT NULL DEFAULT now()
                );
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // HNSW index (cosine distance, idempotent via DO block)
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_indexes
                        WHERE tablename = '{table}'
                          AND indexname  = '{table}_embedding_hnsw_idx'
                    ) THEN
                        CREATE INDEX {table}_embedding_hnsw_idx
                            ON {table} USING hnsw (embedding vector_cosine_ops)
                            WITH (m = {_opts.HnswM}, ef_construction = {_opts.HnswEfConstruction});
                    END IF;
                END;
                $$;
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // collection filter index
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                CREATE INDEX IF NOT EXISTS {table}_collection_idx ON {table}(collection);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        _logger.LogInformation("Collection '{Collection}' ready", table);
    }

    // ── Upsert ─────────────────────────────────────────────────────────────────

    public async Task UpsertAsync(VectorDocument document, CancellationToken ct = default)
    {
        EnsureEmbedding(document);
        var table = SanitiseName(document.Collection);

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = $"""
            INSERT INTO {table} (id, collection, content, metadata, embedding, updated_at)
            VALUES ($1, $2, $3, $4::jsonb, $5, now())
            ON CONFLICT (id) DO UPDATE
                SET content    = EXCLUDED.content,
                    metadata   = EXCLUDED.metadata,
                    embedding  = EXCLUDED.embedding,
                    updated_at = now();
            """;

        cmd.Parameters.AddWithValue(document.Id);
        cmd.Parameters.AddWithValue(document.Collection);
        cmd.Parameters.AddWithValue(document.Content);
        cmd.Parameters.AddWithValue(document.Metadata);
        cmd.Parameters.AddWithValue(new Vector(document.Embedding!));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertBatchAsync(IEnumerable<VectorDocument> documents, CancellationToken ct = default)
    {
        var docs = documents.ToList();
        if (docs.Count == 0) return;

        // Group by collection so we use the right table name
        foreach (var group in docs.GroupBy(d => d.Collection))
        {
            var table = SanitiseName(group.Key);
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using var txn  = await conn.BeginTransactionAsync(ct);

            foreach (var doc in group)
            {
                EnsureEmbedding(doc);
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = txn;
                cmd.CommandText = $"""
                    INSERT INTO {table} (id, collection, content, metadata, embedding, updated_at)
                    VALUES ($1, $2, $3, $4::jsonb, $5, now())
                    ON CONFLICT (id) DO UPDATE
                        SET content    = EXCLUDED.content,
                            metadata   = EXCLUDED.metadata,
                            embedding  = EXCLUDED.embedding,
                            updated_at = now();
                    """;
                cmd.Parameters.AddWithValue(doc.Id);
                cmd.Parameters.AddWithValue(doc.Collection);
                cmd.Parameters.AddWithValue(doc.Content);
                cmd.Parameters.AddWithValue(doc.Metadata);
                cmd.Parameters.AddWithValue(new Vector(doc.Embedding!));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await txn.CommitAsync(ct);
        }

        _logger.LogDebug("Batch upsert: {Count} documents", docs.Count);
    }

    // ── Search ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string    collection,
        float[]   queryEmbedding,
        int       topK        = 5,
        double    maxDistance = 0.5,
        CancellationToken ct  = default)
    {
        var table = SanitiseName(collection);

        await using var conn = await _ds.OpenConnectionAsync(ct);

        // Set ef_search for this session
        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.CommandText = $"SET hnsw.ef_search = {_opts.HnswEfSearch};";
            await setCmd.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, collection, content, metadata, updated_at,
                   embedding <=> $1 AS distance
            FROM   {table}
            WHERE  collection = $2
              AND  (embedding <=> $1) <= $3
            ORDER  BY embedding <=> $1
            LIMIT  $4;
            """;

        cmd.Parameters.AddWithValue(new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue(collection);
        cmd.Parameters.AddWithValue(maxDistance);
        cmd.Parameters.AddWithValue(topK);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<VectorSearchResult>();

        while (await reader.ReadAsync(ct))
        {
            var doc = new VectorDocument
            {
                Id         = reader.GetGuid(0),
                Collection = reader.GetString(1),
                Content    = reader.GetString(2),
                Metadata   = reader.GetString(3),
                UpdatedAt  = reader.GetDateTime(4)
            };
            results.Add(new VectorSearchResult
            {
                Document = doc,
                Distance = reader.GetDouble(5)
            });
        }

        return results;
    }

    // ── Delete / Count ─────────────────────────────────────────────────────────

    public async Task DeleteAsync(Guid id, string collection, CancellationToken ct = default)
    {
        var table = SanitiseName(collection);
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table} WHERE id = $1 AND collection = $2;";
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(collection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> CountAsync(string collection, CancellationToken ct = default)
    {
        var table = SanitiseName(collection);
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE collection = $1;";
        cmd.Parameters.AddWithValue(collection);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string SanitiseName(string name)
    {
        // Allow only alphanumeric + underscore to prevent SQL injection on table names
        var sanitised = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        if (!char.IsLetter(sanitised[0])) sanitised = "v_" + sanitised;
        return sanitised.ToLowerInvariant();
    }

    private static void EnsureEmbedding(VectorDocument doc)
    {
        if (doc.Embedding is null || doc.Embedding.Length == 0)
            throw new InvalidOperationException(
                $"Document '{doc.Id}' has no embedding. Call EmbeddingService.EmbedAsync first.");
    }
}
