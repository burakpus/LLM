using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SetYazilim.Llm.Retrieval;

/// <summary>
/// LLM-based reranker — prompts the chat model to select top-N most relevant
/// candidates for a query, parses indices from the response.
///
/// Inspired by DB-GPT's `_schema_linking_with_llm` approach: a small fast model
/// (Gemma) does few-shot zero-config schema filtering. No extra infrastructure
/// (uses existing LiteLLM endpoint), ~300-500ms latency overhead per query.
///
/// Failure mode: any parse/timeout error → returns original ordering trimmed to
/// topN, so retrieval never blocks on reranker hiccups.
/// </summary>
public sealed class LlmReranker : IRerankService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<LiteLLMOptions> _litellmOpts;
    private readonly IOptions<RerankOptions>  _rerankOpts;
    private readonly ILogger<LlmReranker>      _log;

    public LlmReranker(
        IHttpClientFactory httpFactory,
        IOptions<LiteLLMOptions> litellmOpts,
        IOptions<RerankOptions>  rerankOpts,
        ILogger<LlmReranker>     log)
    {
        _httpFactory = httpFactory;
        _litellmOpts = litellmOpts;
        _rerankOpts  = rerankOpts;
        _log         = log;
    }

    public async Task<IReadOnlyList<RerankedCandidate>> RerankAsync(
        string query, IReadOnlyList<KbSearchResult> candidates, int topN, CancellationToken ct = default)
    {
        if (candidates.Count <= 1)
            return ToOriginalOrder(candidates, topN);
        if (candidates.Count <= topN)
            return ToOriginalOrder(candidates, topN);   // nothing to rerank

        var opts = _rerankOpts.Value;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(opts.TimeoutMs);

            var indices = await CallLlmRankAsync(query, candidates, topN, opts.Llm, timeoutCts.Token);
            if (indices is null || indices.Count == 0)
            {
                _log.LogWarning("LlmReranker: empty/invalid LLM response, falling back to original order");
                return ToOriginalOrder(candidates, topN);
            }

            // Validate + dedupe indices, drop out-of-range, fill remaining slots from original order.
            var seen = new HashSet<int>();
            var ordered = new List<RerankedCandidate>(topN);
            foreach (var idx in indices)
            {
                if (idx < 0 || idx >= candidates.Count) continue;
                if (!seen.Add(idx)) continue;
                ordered.Add(new RerankedCandidate(candidates[idx], 1.0 - (ordered.Count * 0.05), idx));
                if (ordered.Count >= topN) break;
            }
            // Fallback fill: if LLM returned fewer than topN, append original-order misses.
            for (int i = 0; i < candidates.Count && ordered.Count < topN; i++)
            {
                if (seen.Add(i))
                    ordered.Add(new RerankedCandidate(candidates[i], 0.0, i));
            }
            return ordered;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("LlmReranker: timed out after {Ms}ms, using original order", opts.TimeoutMs);
            return ToOriginalOrder(candidates, topN);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LlmReranker failed, using original order");
            return ToOriginalOrder(candidates, topN);
        }
    }

    private async Task<List<int>?> CallLlmRankAsync(
        string query, IReadOnlyList<KbSearchResult> candidates, int topN,
        LlmRerankConfig cfg, CancellationToken ct)
    {
        var prompt = BuildPrompt(query, candidates, topN, cfg.SnippetChars);
        var litellm = _litellmOpts.Value;
        var http = _httpFactory.CreateClient("proxy");
        http.Timeout = TimeSpan.FromSeconds(15);

        var body = JsonSerializer.Serialize(new
        {
            model = cfg.Model,
            messages = new[]
            {
                new { role = "system", content = "You are a precise database schema search assistant. Respond with ONLY comma-separated zero-based indices, no other text." },
                new { role = "user",   content = prompt },
            },
            temperature = cfg.Temperature,
            max_tokens  = 200,
            stream      = false,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post,
            litellm.BaseUrl.TrimEnd('/') + "/v1/chat/completions");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", litellm.ApiKey);

        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _log.LogWarning("LlmReranker LLM call failed: {Status} {Body}", (int)resp.StatusCode, err[..Math.Min(200, err.Length)]);
            return null;
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";

        return ParseIndices(content);
    }

    /// <summary>
    /// Build the few-shot-style ranking prompt. Each candidate is shown as
    /// [index] title — snippet, where snippet is the first N chars of content
    /// (typically table name + first columns).
    /// </summary>
    private static string BuildPrompt(string query, IReadOnlyList<KbSearchResult> candidates, int topN, int snippetChars)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Aşağıda {candidates.Count} aday veritabanı objesi var. Kullanıcı sorgusu ile en alakalı en fazla {topN} tanesinin index'lerini, alaka sırasına göre (en alakalı önce), virgülle ayrılmış olarak döndür.");
        sb.AppendLine($"YALNIZCA sayıları döndür. Örnek doğru cevap formatı: 3,7,1,12,0,5");
        sb.AppendLine();
        sb.AppendLine($"Sorgu: {query}");
        sb.AppendLine();
        sb.AppendLine("Adaylar:");
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var snippet = c.Content?.Length > snippetChars ? c.Content[..snippetChars].Replace("\n", " ") : c.Content?.Replace("\n", " ") ?? "";
            sb.AppendLine($"[{i}] {c.Title} — {snippet}");
        }
        sb.AppendLine();
        sb.AppendLine($"En alakalı {topN} index (virgülle ayır, sadece sayı):");
        return sb.ToString();
    }

    /// <summary>Extract integer indices from free-form LLM response. Robust: handles
    /// "1, 2, 3", "1,2,3", "Indices: 1,2,3", multi-line, parentheses, etc.</summary>
    internal static List<int> ParseIndices(string content)
    {
        var result = new List<int>();
        var tokens = System.Text.RegularExpressions.Regex.Matches(content, @"\d+");
        foreach (System.Text.RegularExpressions.Match m in tokens)
        {
            if (int.TryParse(m.Value, out var n)) result.Add(n);
        }
        return result;
    }

    private static IReadOnlyList<RerankedCandidate> ToOriginalOrder(IReadOnlyList<KbSearchResult> candidates, int topN)
    {
        var n = Math.Min(topN, candidates.Count);
        var arr = new RerankedCandidate[n];
        for (int i = 0; i < n; i++)
            arr[i] = new RerankedCandidate(candidates[i], 0.0, i);
        return arr;
    }
}

/// <summary>
/// Cross-encoder reranker (skeleton). Calls a dedicated vLLM-served reranker
/// model (e.g. BAAI/bge-reranker-v2-m3) on `/v1/rerank`. Not deployed yet
/// (VRAM headroom limited until GPT-OSS plan resolved). When the container is
/// added to docker-compose and Rerank:CrossEncoder:Enabled=true, this kicks in.
/// </summary>
public sealed class CrossEncoderRerankService : IRerankService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<RerankOptions> _opts;
    private readonly ILogger<CrossEncoderRerankService> _log;

    public CrossEncoderRerankService(
        IHttpClientFactory httpFactory,
        IOptions<RerankOptions> opts,
        ILogger<CrossEncoderRerankService> log)
    {
        _httpFactory = httpFactory; _opts = opts; _log = log;
    }

    public async Task<IReadOnlyList<RerankedCandidate>> RerankAsync(
        string query, IReadOnlyList<KbSearchResult> candidates, int topN, CancellationToken ct = default)
    {
        var cfg = _opts.Value.CrossEncoder;
        if (!cfg.Enabled || candidates.Count == 0)
            return TrimAndWrap(candidates, topN);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_opts.Value.TimeoutMs);

            var http = _httpFactory.CreateClient("proxy");
            http.Timeout = TimeSpan.FromSeconds(10);

            var body = JsonSerializer.Serialize(new
            {
                model = cfg.Model,
                query = query,
                documents = candidates.Select(c => $"{c.Title}\n{c.Content}").ToArray(),
                top_n = topN,
            });
            using var req = new HttpRequestMessage(HttpMethod.Post,
                cfg.BaseUrl.TrimEnd('/') + "/v1/rerank");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(cfg.ApiKey))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cfg.ApiKey);

            using var resp = await http.SendAsync(req, timeoutCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Cross-encoder rerank failed: {Status}", (int)resp.StatusCode);
                return TrimAndWrap(candidates, topN);
            }

            var json = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            using var doc = JsonDocument.Parse(json);
            // Expected response: { "results": [{ "index": 0, "relevance_score": 0.94 }, ...] }
            var results = doc.RootElement.GetProperty("results");
            var ordered = new List<RerankedCandidate>();
            foreach (var r in results.EnumerateArray())
            {
                var idx = r.GetProperty("index").GetInt32();
                var score = r.GetProperty("relevance_score").GetDouble();
                if (idx >= 0 && idx < candidates.Count)
                    ordered.Add(new RerankedCandidate(candidates[idx], score, idx));
            }
            return ordered.Count > 0 ? ordered.Take(topN).ToList() : TrimAndWrap(candidates, topN);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cross-encoder rerank exception");
            return TrimAndWrap(candidates, topN);
        }
    }

    private static IReadOnlyList<RerankedCandidate> TrimAndWrap(IReadOnlyList<KbSearchResult> candidates, int topN)
    {
        var n = Math.Min(topN, candidates.Count);
        var arr = new RerankedCandidate[n];
        for (int i = 0; i < n; i++) arr[i] = new RerankedCandidate(candidates[i], 0.0, i);
        return arr;
    }
}

/// <summary>
/// Composite strategy: applies the configured strategy (Strategy='crossencoder' |
/// 'llm' | 'auto' | 'off'). 'auto' tries cross-encoder first and falls back to
/// LLM on any failure.
/// </summary>
public sealed class CompositeRerankService : IRerankService
{
    private readonly CrossEncoderRerankService _ce;
    private readonly LlmReranker               _llm;
    private readonly IOptions<RerankOptions>   _opts;
    private readonly ILogger<CompositeRerankService> _log;

    public CompositeRerankService(
        CrossEncoderRerankService ce, LlmReranker llm,
        IOptions<RerankOptions> opts, ILogger<CompositeRerankService> log)
    {
        _ce = ce; _llm = llm; _opts = opts; _log = log;
    }

    public async Task<IReadOnlyList<RerankedCandidate>> RerankAsync(
        string query, IReadOnlyList<KbSearchResult> candidates, int topN, CancellationToken ct = default)
    {
        var opts = _opts.Value;
        if (!opts.Enabled || string.Equals(opts.Strategy, "off", StringComparison.OrdinalIgnoreCase))
            return Trim(candidates, topN);

        switch (opts.Strategy.ToLowerInvariant())
        {
            case "crossencoder":
                return await _ce.RerankAsync(query, candidates, topN, ct);
            case "llm":
                return await _llm.RerankAsync(query, candidates, topN, ct);
            case "auto":
            {
                // Try cross-encoder only if enabled in config; otherwise straight to LLM.
                if (opts.CrossEncoder.Enabled)
                {
                    var ceResult = await _ce.RerankAsync(query, candidates, topN, ct);
                    // Heuristic: if cross-encoder returned without re-scoring (all scores=0),
                    // treat as failure and fall through to LLM.
                    if (ceResult.Any(r => r.Score > 0)) return ceResult;
                    _log.LogDebug("Cross-encoder produced no scores, falling back to LLM rerank");
                }
                return await _llm.RerankAsync(query, candidates, topN, ct);
            }
            default:
                _log.LogWarning("Unknown rerank strategy '{S}', falling back to no-op", opts.Strategy);
                return Trim(candidates, topN);
        }
    }

    private static IReadOnlyList<RerankedCandidate> Trim(IReadOnlyList<KbSearchResult> candidates, int topN)
    {
        var n = Math.Min(topN, candidates.Count);
        var arr = new RerankedCandidate[n];
        for (int i = 0; i < n; i++) arr[i] = new RerankedCandidate(candidates[i], 0.0, i);
        return arr;
    }
}
