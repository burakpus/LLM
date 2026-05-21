using System.Text.Json.Serialization;

namespace SetYazilim.Llm.VectorStore;

/// <summary>
/// A document stored in the vector store with its embedding and metadata.
/// </summary>
public sealed class VectorDocument
{
    /// <summary>Unique identifier (UUID).</summary>
    public Guid   Id         { get; init; } = Guid.NewGuid();

    /// <summary>Collection / table name this document belongs to.</summary>
    public string Collection { get; init; } = "default";

    /// <summary>Raw text content used to generate the embedding.</summary>
    public string Content    { get; init; } = string.Empty;

    /// <summary>Serialised metadata (JSON object).</summary>
    public string Metadata   { get; init; } = "{}";

    /// <summary>Embedding vector. Null until populated by EmbeddingService.</summary>
    public float[]? Embedding { get; set; }

    /// <summary>UTC timestamp of the last upsert.</summary>
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>A search result returned by <see cref="IVectorStore.SearchAsync"/>.</summary>
public sealed class VectorSearchResult
{
    public VectorDocument Document    { get; init; } = null!;

    /// <summary>Cosine distance — lower is more similar. Range [0, 2].</summary>
    public double Distance { get; init; }

    /// <summary>Cosine similarity derived from distance. Range [-1, 1].</summary>
    public double Similarity => 1.0 - Distance;
}

/// <summary>Parameters for an embedding request sent to the model server.</summary>
public sealed class EmbeddingRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("input")]
    public required IReadOnlyList<string> Input { get; init; }

    [JsonPropertyName("encoding_format")]
    public string EncodingFormat { get; init; } = "float";
}

/// <summary>OpenAI-compatible embedding response.</summary>
public sealed class EmbeddingResponse
{
    [JsonPropertyName("data")]
    public required IReadOnlyList<EmbeddingData> Data { get; init; }

    [JsonPropertyName("usage")]
    public EmbeddingUsage? Usage { get; init; }
}

public sealed class EmbeddingData
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("embedding")]
    public required float[] Embedding { get; init; }
}

public sealed class EmbeddingUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; init; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; init; }
}
