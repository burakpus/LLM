namespace SetYazilim.Llm.VectorStore;

/// <summary>
/// Abstraction over a pgvector-backed document store.
/// All methods are async-safe and cancellation-aware.
/// </summary>
public interface IVectorStore
{
    /// <summary>
    /// Creates the schema (table + index) for the given collection if it does not exist.
    /// Call once at startup per collection you intend to use.
    /// </summary>
    Task EnsureCollectionAsync(string collection, int dimensions, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates a document.
    /// The document's <see cref="VectorDocument.Embedding"/> must be populated before calling.
    /// </summary>
    Task UpsertAsync(VectorDocument document, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates multiple documents in a single batch.
    /// All documents must have embeddings.
    /// </summary>
    Task UpsertBatchAsync(IEnumerable<VectorDocument> documents, CancellationToken ct = default);

    /// <summary>
    /// Finds the <paramref name="topK"/> most similar documents to the given embedding
    /// within the specified collection.
    /// </summary>
    Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        string    collection,
        float[]   queryEmbedding,
        int       topK             = 5,
        double    maxDistance      = 0.5,
        CancellationToken ct       = default);

    /// <summary>
    /// Deletes a document by ID.
    /// </summary>
    Task DeleteAsync(Guid id, string collection, CancellationToken ct = default);

    /// <summary>
    /// Returns the number of documents in a collection.
    /// </summary>
    Task<long> CountAsync(string collection, CancellationToken ct = default);
}
