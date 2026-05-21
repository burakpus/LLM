using System.ComponentModel.DataAnnotations;

namespace SetYazilim.Llm;

public sealed class LiteLLMOptions
{
    public const string SectionName = "LiteLLM";

    /// <summary>Base URL for LiteLLM gateway, e.g. https://llm.internal</summary>
    [Required]
    public string BaseUrl { get; set; } = "http://localhost:4000";

    /// <summary>Virtual API key issued by LiteLLM admin (sk-...).</summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Default request timeout (per-call). Streaming uses StreamTimeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Max time to keep streaming connection open.</summary>
    public TimeSpan StreamTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Number of retry attempts on transient errors (5xx, network).</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>Circuit breaker: failure threshold ratio (0..1).</summary>
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;

    /// <summary>Circuit breaker: minimum throughput before evaluating.</summary>
    public int CircuitBreakerMinimumThroughput { get; set; } = 10;

    /// <summary>Circuit breaker: break duration when opened.</summary>
    public TimeSpan CircuitBreakerBreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>User identifier propagated to LiteLLM for cost tracking.</summary>
    public string? DefaultUser { get; set; }
}
