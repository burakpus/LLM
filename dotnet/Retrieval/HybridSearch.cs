using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;

namespace SetYazilim.Llm.Retrieval;

// ── Models ────────────────────────────────────────────────────────────────────

public sealed class KbSearchOptions
{
    public const string SectionName = "KbSearch";

    /// <summary>Weight for vector score vs full-text score. 0.7 = 70% vector.</summary>
    public float VectorWeight     { get; init; } = 0.7f;

    /// <summary>Max cosine distance to include in results. 0 = identical, 2 = opposite.</summary>
    public double MaxVecDistance  { get; init; } = 0.55;

    /// <summary>ef_search for HNSW ANN queries.</summary>
    public int HnswEfSearch       { get; init; } = 40;

    /// <summary>Collections the app has access to (empty = all).</summary>
    public IReadOnlyList<string> AllowedCollections { get; init; } = [];
}

public sealed class KbSearchResult
{
    public Guid   Id           { get; init; }
    public string Collection   { get; init; } = string.Empty;
    public string Source       { get; init; } = string.Empty;
    public string Title        { get; init; } = string.Empty;
    public string Content      { get; init; } = string.Empty;
    public int    ChunkIndex   { get; init; }
    public string Metadata     { get; init; } = "{}";
    public double VecDistance  { get; init; }
    public double FtsRank      { get; init; }
    public double HybridScore  { get; init; }
}

// ── Interface ─────────────────────────────────────────────────────────────────

public interface IHybridSearch
{
    /// <summary>
    /// Searches kb_documents using vector similarity + full-text BM25,
    /// merges scores, returns ranked results.
    /// </summary>
    Task<IReadOnlyList<KbSearchResult>> SearchAsync(
        float[]            queryEmbedding,
        string             queryText,
        string[]?          collections  = null,
        int                topK         = 6,
        string?            metadataFilter = null,   // JSONB @> expression e.g. '{"year":2024}'
        CancellationToken  ct           = default);
}

// ── Implementation ────────────────────────────────────────────────────────────

public sealed class PgHybridSearch : IHybridSearch
{
    private readonly NpgsqlDataSource _ds;
    private readonly KbSearchOptions  _opts;
    private readonly ILogger<PgHybridSearch> _log;

    public PgHybridSearch(
        NpgsqlDataSource ds,
        IOptions<KbSearchOptions> opts,
        ILogger<PgHybridSearch> log)
    {
        _ds   = ds;
        _opts = opts.Value;
        _log  = log;
    }

    public async Task<IReadOnlyList<KbSearchResult>> SearchAsync(
        float[]           queryEmbedding,
        string            queryText,
        string[]?         collections  = null,
        int               topK         = 6,
        string?           metadataFilter = null,
        CancellationToken ct           = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        // Set ef_search
        await using (var setEf = conn.CreateCommand())
        {
            setEf.CommandText = $"SET hnsw.ef_search = {_opts.HnswEfSearch};";
            await setEf.ExecuteNonQueryAsync(ct);
        }

        // Build collection + metadata filters dynamically
        // (avoid passing typed-null parameters — PG can't infer type for $N = NULL)
        var effectiveCollections = (collections?.Length > 0 ? collections : null)
                                ?? (_opts.AllowedCollections.Count > 0
                                    ? [.. _opts.AllowedCollections]
                                    : null);

        // Fixed params: $1 embedding, $2 queryText, $3 maxDist, $4 topK
        int nextParam = 5;

        string collectionClause;
        int    colParam = 0;
        if (effectiveCollections is not null)
        {
            colParam        = nextParam++;
            collectionClause = $"AND collection = ANY(${colParam})";
        }
        else collectionClause = "";

        string metadataClause;
        int    metaParam = 0;
        if (!string.IsNullOrEmpty(metadataFilter))
        {
            metaParam      = nextParam++;
            metadataClause = $"AND metadata @> ${metaParam}::jsonb";
        }
        else metadataClause = "";

        int weightParam = nextParam;

        await using var cmd = conn.CreateCommand();

        cmd.CommandText = $"""
            WITH vec_cand AS (
                SELECT id,
                       embedding <=> $1  AS vec_distance,
                       0.0               AS fts_rank
                FROM   kb_documents
                WHERE  (embedding <=> $1) <= $3
                {collectionClause}
                {metadataClause}
                ORDER  BY embedding <=> $1
                LIMIT  $4 * 3
            ),
            fts_cand AS (
                SELECT id,
                       2.0                                        AS vec_distance,
                       ts_rank_cd(ts_content, websearch_to_tsquery('turkish_unaccent', $2)) AS fts_rank
                FROM   kb_documents
                WHERE  ts_content @@ websearch_to_tsquery('turkish_unaccent', $2)
                {collectionClause}
                {metadataClause}
                ORDER  BY fts_rank DESC
                LIMIT  $4 * 3
            ),
            merged AS (
                SELECT id,
                       min(vec_distance) AS vec_distance,
                       max(fts_rank)     AS fts_rank
                FROM   (SELECT * FROM vec_cand UNION ALL SELECT * FROM fts_cand) u
                GROUP  BY id
            )
            SELECT d.id, d.collection, d.source, d.title, d.content,
                   d.chunk_index, d.metadata::text,
                   m.vec_distance, m.fts_rank,
                   hybrid_score(m.vec_distance, m.fts_rank, ${weightParam}) AS hybrid_score
            FROM   merged m
            JOIN   kb_documents d USING (id)
            ORDER  BY hybrid_score DESC
            LIMIT  $4;
            """;

        cmd.Parameters.AddWithValue(new Vector(queryEmbedding)); // $1
        cmd.Parameters.AddWithValue(queryText);                  // $2
        cmd.Parameters.AddWithValue(_opts.MaxVecDistance);       // $3
        cmd.Parameters.AddWithValue(topK);                       // $4
        if (colParam  > 0) cmd.Parameters.AddWithValue(effectiveCollections!); // $5?
        if (metaParam > 0) cmd.Parameters.AddWithValue(metadataFilter!);       // $5/$6?
        cmd.Parameters.AddWithValue(_opts.VectorWeight);         // $weight

        await using var r = await cmd.ExecuteReaderAsync(ct);
        var results = new List<KbSearchResult>();

        while (await r.ReadAsync(ct))
            results.Add(new KbSearchResult
            {
                Id           = r.GetGuid(0),
                Collection   = r.GetString(1),
                Source       = r.GetString(2),
                Title        = r.GetString(3),
                Content      = r.GetString(4),
                ChunkIndex   = r.GetInt32(5),
                Metadata     = r.GetString(6),
                VecDistance  = r.GetDouble(7),
                FtsRank      = r.GetDouble(8),
                HybridScore  = r.GetDouble(9)
            });

        _log.LogDebug("HybridSearch: query='{Q}', collections={C}, results={N}",
            queryText[..Math.Min(60, queryText.Length)],
            string.Join(",", effectiveCollections ?? ["*"]),
            results.Count);

        return results;
    }
}
