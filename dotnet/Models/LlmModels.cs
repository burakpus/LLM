using System.Text.Json.Serialization;

namespace SetYazilim.Llm.Models;

/// <summary>
/// Logical model identifier exposed by LiteLLM.
/// Maps to physical model behind the gateway.
/// </summary>
public enum LlmModel
{
    /// <summary>Gemma 4 31B FP8 — general assistant, hot tier.</summary>
    Chat,
    /// <summary>Qwen3.6 35B A3B Coding BF16 — code/SQL, hot tier.</summary>
    Code,
    /// <summary>GPT-OSS 120B MXFP4 — reasoning, cold tier (manual swap).</summary>
    Reason
}

public static class LlmModelExtensions
{
    public static string ToApiName(this LlmModel model) => model switch
    {
        LlmModel.Chat   => "chat",
        LlmModel.Code   => "code",
        LlmModel.Reason => "reason",
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, null)
    };
}

public sealed record ChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public sealed record ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("messages")]
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; init; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }

    [JsonPropertyName("stream")]
    public bool Stream { get; init; }

    [JsonPropertyName("user")]
    public string? User { get; init; }

    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public sealed record ChatCompletionResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice> Choices,
    [property: JsonPropertyName("usage")] TokenUsage? Usage);

public sealed record ChatChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("message")] ChatMessage Message,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

public sealed record StreamingChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] IReadOnlyList<StreamingChoice> Choices);

public sealed record StreamingChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("delta")] StreamingDelta Delta,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

public sealed record StreamingDelta(
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("content")] string? Content);

public sealed record TokenUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);

public class LlmException : Exception
{
    public int? StatusCode { get; }
    public string? ErrorType { get; }

    public LlmException(string message, int? statusCode = null, string? errorType = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ErrorType = errorType;
    }
}

public sealed class LlmModelUnavailableException : LlmException
{
    public TimeSpan? RetryAfter { get; }

    public LlmModelUnavailableException(string message, TimeSpan? retryAfter = null)
        : base(message, statusCode: 503, errorType: "model_unavailable")
    {
        RetryAfter = retryAfter;
    }
}
