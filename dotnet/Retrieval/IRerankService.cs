namespace SetYazilim.Llm.Retrieval;

/// <summary>
/// Second-pass retrieval refinement. After <see cref="IHybridSearch"/> returns
/// N candidates by combined vector + FTS score, a reranker re-orders them using
/// a more powerful signal (cross-encoder embedding model, or an LLM filter).
///
/// The hybrid pipeline is:
///   HybridSearch (top 20 by score) → IRerankService.RerankAsync → top K (6) → LLM
///
/// Two production strategies exist:
///  - <see cref="LlmReranker"/>: prompt the existing chat model to score relevance.
///    Zero extra infrastructure, ~300-500ms overhead. Default.
///  - <see cref="CrossEncoderRerankService"/>: call a dedicated cross-encoder vLLM
///    instance (e.g. BAAI/bge-reranker-v2-m3). Faster (~100ms) and higher quality,
///    but requires ~2 GB VRAM and a separate container.
///
/// <see cref="CompositeRerankService"/> chains them: tries cross-encoder first,
/// falls back to LLM if unavailable, falls back to no-op pass-through on errors.
/// </summary>
public interface IRerankService
{
    /// <summary>
    /// Re-order <paramref name="candidates"/> by relevance to <paramref name="query"/>,
    /// returning the top <paramref name="topN"/> (best first). Implementations should
    /// be resilient: on timeout / failure, return the input candidates trimmed to
    /// <paramref name="topN"/> in original order (no-op fallback) rather than throwing.
    /// </summary>
    Task<IReadOnlyList<RerankedCandidate>> RerankAsync(
        string query,
        IReadOnlyList<KbSearchResult> candidates,
        int topN,
        CancellationToken ct = default);
}

public sealed record RerankedCandidate(
    KbSearchResult Source,
    double         Score,             // 0..1 confidence; higher = more relevant
    int            OriginalIndex);    // position in input list (for debugging)
