namespace SetYazilim.Llm.Retrieval;

/// <summary>
/// appsettings.json "Rerank" block. All fields optional — sensible defaults
/// apply when omitted, so existing deployments don't need to touch config.
/// </summary>
public sealed class RerankOptions
{
    public const string SectionName = "Rerank";

    /// <summary>Master switch. When false, hybrid search results are used directly.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Strategy: 'llm' (default — uses chat model), 'crossencoder' (vLLM service required),
    /// 'auto' (try crossencoder, fall back to llm), 'off' (no-op).</summary>
    public string Strategy { get; set; } = "llm";

    /// <summary>Initial candidate count fetched from HybridSearch when rerank is on.
    /// Larger = more recall but slower rerank. Typical 15-25.</summary>
    public int CandidateCount { get; set; } = 20;

    /// <summary>Final top-K passed to the LLM after reranking. Should match the
    /// ContextBuilder's pre-existing topK (default 6).</summary>
    public int TopK { get; set; } = 6;

    /// <summary>Hard timeout for reranker call (ms). On timeout, fall back to no-op
    /// (return candidates in original order, trimmed to TopK).</summary>
    public int TimeoutMs { get; set; } = 5000;

    public LlmRerankConfig Llm { get; set; } = new();
    public CrossEncoderRerankConfig CrossEncoder { get; set; } = new();
}

public sealed class LlmRerankConfig
{
    /// <summary>LiteLLM model name to use for reranking. 'chat' = Gemma (faster, cheaper).</summary>
    public string Model { get; set; } = "chat";

    /// <summary>How many chars of each candidate to show the LLM. Keep small to avoid
    /// prompt bloat — first ~200 chars usually carry table name + first few columns.</summary>
    public int SnippetChars { get; set; } = 250;

    /// <summary>Temperature for the rerank call. Low = consistent ordering.</summary>
    public double Temperature { get; set; } = 0.0;
}

public sealed class CrossEncoderRerankConfig
{
    public bool   Enabled { get; set; } = false;
    public string BaseUrl { get; set; } = "http://localhost:8005";
    public string Model   { get; set; } = "BAAI/bge-reranker-v2-m3";
    public string? ApiKey { get; set; }
}
