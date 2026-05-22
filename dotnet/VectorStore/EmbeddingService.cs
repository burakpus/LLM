using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SetYazilim.Llm.VectorStore;

/// <summary>
/// Generates text embeddings via an OpenAI-compatible /v1/embeddings endpoint.
/// Results are cached in-process (IMemoryCache) to avoid re-embedding identical
/// query strings — typical RAG pattern where the same questions recur frequently.
/// </summary>
public sealed class EmbeddingService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly VectorStoreOptions _opts;
    private readonly ILogger<EmbeddingService> _logger;
    private readonly IMemoryCache _cache;

    // Cache settings: up to 2000 embeddings, 1 hour TTL, 30 min sliding
    private static readonly MemoryCacheEntryOptions CacheOpts = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1),
        SlidingExpiration               = TimeSpan.FromMinutes(30),
        Size                            = 1,
    };

    public EmbeddingService(
        HttpClient http,
        IOptions<VectorStoreOptions> opts,
        ILogger<EmbeddingService> logger,
        IMemoryCache cache)
    {
        _http   = http;
        _opts   = opts.Value;
        _logger = logger;
        _cache  = cache;
    }

    private static string CacheKey(string text, string model) =>
        $"emb:{model}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16]}";

    /// <summary>Embeds a single text string (cache-first).</summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var key = CacheKey(text, _opts.EmbedModel);
        if (_cache.TryGetValue(key, out float[]? cached))
        {
            _logger.LogDebug("Embedding cache hit for {Chars} chars", text.Length);
            return cached!;
        }

        var results = await EmbedBatchAsync([text], ct);
        _cache.Set(key, results[0], CacheOpts);
        return results[0];
    }

    /// <summary>
    /// Embeds multiple texts, using cache for already-known inputs.
    /// Returns embeddings in the same order as the input.
    /// </summary>
    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];

        // Resolve from cache where possible
        var result = new float[texts.Count][];
        var missing = new List<(int idx, string text)>();
        for (int i = 0; i < texts.Count; i++)
        {
            var key = CacheKey(texts[i], _opts.EmbedModel);
            if (_cache.TryGetValue(key, out float[]? cached))
                result[i] = cached!;
            else
                missing.Add((i, texts[i]));
        }
        if (missing.Count == 0) return result;

        var request = new EmbeddingRequest
        {
            Model = _opts.EmbedModel,
            Input = missing.Select(m => m.text).ToList()
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings")
        {
            Content = JsonContent.Create(request, options: JsonOpts)
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.EmbedApiKey);

        using var resp = await _http.SendAsync(msg, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("Embedding API error {Status}: {Body}", resp.StatusCode, body);
            throw new InvalidOperationException($"Embedding API returned {(int)resp.StatusCode}: {body}");
        }

        var apiResult = await resp.Content.ReadFromJsonAsync<EmbeddingResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Embedding API returned empty response");

        var embeddings = apiResult.Data.OrderBy(d => d.Index).Select(d => d.Embedding).ToArray();
        for (int i = 0; i < missing.Count; i++)
        {
            result[missing[i].idx] = embeddings[i];
            var key = CacheKey(missing[i].text, _opts.EmbedModel);
            _cache.Set(key, embeddings[i], CacheOpts);
        }
        return result;
    }

    /// <summary>
    /// Convenience: embed documents that are missing embeddings and return them.
    /// Documents that already have embeddings are skipped.
    /// </summary>
    public async Task<IReadOnlyList<VectorDocument>> EmbedDocumentsAsync(
        IReadOnlyList<VectorDocument> documents,
        CancellationToken ct = default)
    {
        var toEmbed = documents.Where(d => d.Embedding is null).ToList();
        if (toEmbed.Count == 0) return documents;

        _logger.LogDebug("Embedding {Count} documents", toEmbed.Count);

        var embeddings = await EmbedBatchAsync(
            toEmbed.Select(d => d.Content).ToList(), ct);

        for (int i = 0; i < toEmbed.Count; i++)
            toEmbed[i].Embedding = embeddings[i];

        return documents;
    }
}
