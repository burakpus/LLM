using System.ComponentModel.DataAnnotations;

namespace SetYazilim.Llm.VectorStore;

/// <summary>
/// Configuration for the pgvector store and embedding service.
/// Bind from configuration section "VectorStore".
/// </summary>
public sealed class VectorStoreOptions
{
    public const string SectionName = "VectorStore";

    // ── PostgreSQL ────────────────────────────────────────────────────────────

    /// <summary>
    /// Full Npgsql connection string.
    /// Example: "Host=172.16.0.8;Port=5432;Database=mydb;Username=setadmin;Password=Atlas_71"
    /// </summary>
    [Required]
    public string ConnectionString { get; init; } = string.Empty;

    // ── Embedding model ───────────────────────────────────────────────────────

    /// <summary>
    /// Base URL of the OpenAI-compatible embeddings API.
    /// Example: "http://172.16.1.123:8004/v1" (dedicated vLLM embed container)
    ///       or "http://172.16.1.123:4000/v1"  (LiteLLM proxy)
    /// </summary>
    [Required]
    public string EmbedApiBase { get; init; } = string.Empty;

    /// <summary>API key for the embedding endpoint (use vLLM key or LiteLLM key).</summary>
    [Required]
    public string EmbedApiKey { get; init; } = string.Empty;

    /// <summary>Model name sent in the embeddings request.</summary>
    public string EmbedModel { get; init; } = "embed";

    /// <summary>
    /// Vector dimension produced by the embedding model.
    /// nomic-embed-text-v1.5 → 768, all-MiniLM-L6-v2 → 384.
    /// </summary>
    public int EmbedDimensions { get; init; } = 768;

    // ── HNSW index (applied at EnsureCollection time) ────────────────────────

    /// <summary>HNSW m parameter (edges per node). Larger = better recall, more RAM.</summary>
    public int HnswM { get; init; } = 16;

    /// <summary>HNSW ef_construction. Larger = better index quality, slower build.</summary>
    public int HnswEfConstruction { get; init; } = 64;

    // ── Search ────────────────────────────────────────────────────────────────

    /// <summary>Default ef_search for ANN queries (can be overridden per-query).</summary>
    public int HnswEfSearch { get; init; } = 40;
}
