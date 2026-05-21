using SetYazilim.Llm.Models;

namespace SetYazilim.Llm;

/// <summary>
/// Abstraction for LLM completions via LiteLLM gateway.
/// Implementations should be thread-safe and support cancellation.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Send a chat completion request and await the full response.
    /// </summary>
    /// <exception cref="LlmModelUnavailableException">Thrown when the target model is not currently active (cold tier swapped out).</exception>
    /// <exception cref="LlmException">Thrown for any other API or transport error.</exception>
    Task<ChatCompletionResponse> CompleteAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stream a chat completion as Server-Sent Events.
    /// Yields chunks as they arrive. Caller is responsible for accumulating content.
    /// </summary>
    IAsyncEnumerable<StreamingChunk> StreamAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Convenience: single user prompt → string response (non-streaming).
    /// </summary>
    Task<string> AskAsync(
        LlmModel model,
        string prompt,
        string? systemPrompt = null,
        CancellationToken cancellationToken = default);
}
