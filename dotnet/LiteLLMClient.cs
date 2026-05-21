using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SetYazilim.Llm.Models;

namespace SetYazilim.Llm;

/// <summary>
/// Default LiteLLM client implementation.
/// Talks OpenAI-compatible /v1/chat/completions endpoint.
/// Resilience (retry/circuit breaker) is layered via Polly in DI registration.
/// </summary>
public sealed class LiteLLMClient : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly LiteLLMOptions _options;
    private readonly ILogger<LiteLLMClient> _logger;

    public LiteLLMClient(
        HttpClient http,
        IOptions<LiteLLMOptions> options,
        ILogger<LiteLLMClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ChatCompletionResponse> CompleteAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var msg = BuildRequest("/v1/chat/completions", request, stream: false);
        using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        await EnsureSuccessAsync(resp, request.Model, cancellationToken).ConfigureAwait(false);

        var result = await resp.Content.ReadFromJsonAsync<ChatCompletionResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        return result ?? throw new LlmException("LiteLLM returned empty response body");
    }

    public async IAsyncEnumerable<StreamingChunk> StreamAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var streamRequest = request with { Stream = true };
        using var msg = BuildRequest("/v1/chat/completions", streamRequest, stream: true);

        using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        await EnsureSuccessAsync(resp, request.Model, cancellationToken).ConfigureAwait(false);

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line[5..].Trim();
            if (payload == "[DONE]") yield break;

            StreamingChunk? chunk = null;
            try
            {
                chunk = JsonSerializer.Deserialize<StreamingChunk>(payload, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse streaming chunk: {Payload}", payload);
            }

            if (chunk is not null)
                yield return chunk;
        }
    }

    public async Task<string> AskAsync(
        LlmModel model,
        string prompt,
        string? systemPrompt = null,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>(2);
        if (!string.IsNullOrEmpty(systemPrompt))
            messages.Add(new ChatMessage("system", systemPrompt));
        messages.Add(new ChatMessage("user", prompt));

        var request = new ChatCompletionRequest
        {
            Model = model.ToApiName(),
            Messages = messages,
            Temperature = model == LlmModel.Code ? 0.2 : 0.7,
            MaxTokens = 2048,
            User = _options.DefaultUser
        };

        var response = await CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Choices.FirstOrDefault()?.Message.Content
            ?? throw new LlmException("LiteLLM returned no choices");
    }

    private HttpRequestMessage BuildRequest<T>(string path, T body, bool stream)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        if (stream)
            msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return msg;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage resp, string model, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            var retryAfter = resp.Headers.RetryAfter?.Delta;
            _logger.LogWarning(
                "Model '{Model}' unavailable (503). Retry-After: {RetryAfter}. This usually indicates a swap is in progress.",
                model, retryAfter);
            throw new LlmModelUnavailableException(
                $"Model '{model}' is currently unavailable (likely swapped out).",
                retryAfter);
        }

        _logger.LogError(
            "LiteLLM returned {StatusCode} for model '{Model}': {Body}",
            resp.StatusCode, model, body);

        throw new LlmException(
            $"LiteLLM error {(int)resp.StatusCode}: {body}",
            statusCode: (int)resp.StatusCode);
    }
}
