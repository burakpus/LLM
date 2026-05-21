using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SetYazilim.Llm.VectorStore;

/// <summary>
/// Generates text embeddings via an OpenAI-compatible /v1/embeddings endpoint
/// (vLLM embed container or LiteLLM proxy).
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

    public EmbeddingService(
        HttpClient http,
        IOptions<VectorStoreOptions> opts,
        ILogger<EmbeddingService> logger)
    {
        _http   = http;
        _opts   = opts.Value;
        _logger = logger;
    }

    /// <summary>Embeds a single text string.</summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var results = await EmbedBatchAsync([text], ct);
        return results[0];
    }

    /// <summary>
    /// Embeds multiple texts in one request.
    /// Returns embeddings in the same order as the input.
    /// </summary>
    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];

        var request = new EmbeddingRequest
        {
            Model = _opts.EmbedModel,
            Input = texts
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

        var result = await resp.Content.ReadFromJsonAsync<EmbeddingResponse>(JsonOpts, ct)
            ?? throw new InvalidOperationException("Embedding API returned empty response");

        return result.Data
            .OrderBy(d => d.Index)
            .Select(d => d.Embedding)
            .ToList();
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
