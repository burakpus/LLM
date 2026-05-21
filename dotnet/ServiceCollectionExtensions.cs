using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;
using Polly;
using Polly.Retry;
using Polly.CircuitBreaker;
using SetYazilim.Llm.Context;
using SetYazilim.Llm.Memory;
using SetYazilim.Llm.Retrieval;
using SetYazilim.Llm.VectorStore;

namespace SetYazilim.Llm;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ILlmClient"/> with HttpClient, Polly resilience pipeline,
    /// and options bound from configuration section "LiteLLM".
    /// </summary>
    public static IServiceCollection AddLiteLLMClient(
        this IServiceCollection services,
        Action<LiteLLMOptions>? configure = null)
    {
        services.AddOptions<LiteLLMOptions>()
            .BindConfiguration(LiteLLMOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        if (configure is not null)
            services.PostConfigure(configure);

        services.AddHttpClient<ILlmClient, LiteLLMClient>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<LiteLLMOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/'));
            client.Timeout = opts.StreamTimeout;   // streaming requires long-lived connections
            client.DefaultRequestHeaders.Add("X-Client", "SetYazilim.Llm/1.0");
        })
        .AddResilienceHandler("llm-pipeline", (builder, ctx) =>
        {
            var opts = ctx.ServiceProvider.GetRequiredService<IOptions<LiteLLMOptions>>().Value;

            // Retry transient errors. Honor Retry-After (LiteLLM emits it during swap).
            builder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = opts.RetryCount,
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TaskCanceledException>()
                    .HandleResult(r =>
                        r.StatusCode == HttpStatusCode.RequestTimeout ||
                        r.StatusCode == HttpStatusCode.ServiceUnavailable ||
                        r.StatusCode == HttpStatusCode.BadGateway ||
                        r.StatusCode == HttpStatusCode.GatewayTimeout ||
                        r.StatusCode == HttpStatusCode.TooManyRequests),
                DelayGenerator = args =>
                {
                    if (args.Outcome.Result?.Headers.RetryAfter?.Delta is { } retryAfter)
                        return new ValueTask<TimeSpan?>(retryAfter);
                    return new ValueTask<TimeSpan?>((TimeSpan?)null);
                }
            });

            // Circuit breaker per host
            builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                FailureRatio = opts.CircuitBreakerFailureRatio,
                MinimumThroughput = opts.CircuitBreakerMinimumThroughput,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = opts.CircuitBreakerBreakDuration,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .HandleResult(r => (int)r.StatusCode >= 500)
            });

            // Per-attempt timeout (overrides HttpClient.Timeout for non-stream requests)
            builder.AddTimeout(opts.Timeout);
        });

        return services;
    }

    /// <summary>
    /// Registers <see cref="IVectorStore"/> (pgvector) and <see cref="EmbeddingService"/>.
    /// Binds options from configuration section "VectorStore".
    /// </summary>
    public static IServiceCollection AddVectorStore(
        this IServiceCollection services,
        Action<VectorStoreOptions>? configure = null)
    {
        services.AddOptions<VectorStoreOptions>()
            .BindConfiguration(VectorStoreOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        if (configure is not null)
            services.PostConfigure(configure);

        // NpgsqlDataSource — registered as singleton, connection-pooled
        services.AddSingleton<NpgsqlDataSource>(sp =>
        {
            var opts  = sp.GetRequiredService<IOptions<VectorStoreOptions>>().Value;
            var builder = new NpgsqlDataSourceBuilder(opts.ConnectionString);
            builder.UseVector();      // enable pgvector type mapping
            return builder.Build();
        });

        services.AddSingleton<IVectorStore, PgVectorStore>();

        // EmbeddingService gets its own HttpClient pointing at the embed endpoint
        services.AddHttpClient<EmbeddingService>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<VectorStoreOptions>>().Value;
            client.BaseAddress = new Uri(opts.EmbedApiBase.TrimEnd('/'));
            client.Timeout     = TimeSpan.FromSeconds(60);
        });

        return services;
    }

    /// <summary>
    /// Registers the full agent stack:
    ///   Session memory, Agent memory, Hybrid KB search,
    ///   Context builder, Skill registry, AgentChat.
    ///
    /// Prerequisite: <see cref="AddVectorStore"/> and <see cref="AddLiteLLMClient"/> must be called first.
    /// </summary>
    public static IServiceCollection AddAgentStack(
        this IServiceCollection services,
        string skillsDirectory,
        Action<SessionMemoryOptions>? sessionOpts = null,
        Action<KbSearchOptions>? kbOpts = null)
    {
        // Session memory
        services.AddOptions<SessionMemoryOptions>()
            .BindConfiguration(SessionMemoryOptions.SectionName)
            .ValidateDataAnnotations();
        if (sessionOpts is not null)
            services.PostConfigure(sessionOpts);
        services.AddSingleton<ISessionMemory, PgSessionMemory>();

        // Agent memory
        services.AddSingleton<IAgentMemory, PgAgentMemory>();

        // KB hybrid search
        services.AddOptions<KbSearchOptions>()
            .BindConfiguration(KbSearchOptions.SectionName);
        if (kbOpts is not null)
            services.PostConfigure(kbOpts);
        services.AddSingleton<IHybridSearch, PgHybridSearch>();

        // Skill registry — load .md files at startup
        services.AddSingleton(sp =>
        {
            var log = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SkillRegistry>>();
            var registry = new SkillRegistry(log);
            registry.LoadFromDirectory(skillsDirectory);
            return registry;
        });

        // Context builder + agent chat
        services.AddSingleton<IContextBuilder, ContextBuilder>();
        services.AddSingleton<IAgentChat, AgentChat>();

        return services;
    }
}
