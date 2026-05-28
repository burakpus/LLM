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

        // RAG synonym expansion — DB-backed dictionary with 60s cache.
        // Seeded at startup if rag_synonyms table is empty.
        services.AddSingleton<IRagSynonymService, RagSynonymService>();
        services.AddHostedService<RagSynonymSeeder>();

        // RAG rerank pipeline — HybridSearch top-N → reranker → top-K.
        // Strategy in appsettings: 'llm' (default, uses chat model), 'crossencoder'
        // (needs bge-reranker-v2-m3 deploy), 'auto' (ce→llm fallback), 'off'.
        services.AddOptions<RerankOptions>()
            .BindConfiguration(RerankOptions.SectionName);
        services.AddSingleton<LlmReranker>();
        services.AddSingleton<CrossEncoderRerankService>();
        services.AddSingleton<IRerankService, CompositeRerankService>();

        // Skill registry — load .md files at startup (eager)
        // Without this, the first /api/skills request pays the file I/O cost (86+ files).
        services.AddSingleton(sp =>
        {
            var log = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SkillRegistry>>();
            var registry = new SkillRegistry(log);
            registry.LoadFromDirectory(skillsDirectory, setPath: true);
            return registry;
        });
        services.AddHostedService<SkillRegistryEagerInitializer>();

        // Context builder + agent chat
        services.AddSingleton<IContextBuilder, ContextBuilder>();
        services.AddSingleton<IAgentChat, AgentChat>();

        return services;
    }
}

/// <summary>
/// Forces SkillRegistry to be resolved at host startup (not lazily on first request).
/// Without this, the first /api/skills call has to read 80+ files synchronously,
/// blocking the response for several hundred ms.
/// </summary>
internal sealed class SkillRegistryEagerInitializer : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly SkillRegistry _registry;
    private readonly Microsoft.Extensions.Logging.ILogger _log;

    public SkillRegistryEagerInitializer(SkillRegistry registry,
        Microsoft.Extensions.Logging.ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _log      = loggerFactory.CreateLogger("SkillRegistryEagerInitializer");
    }

    public System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken ct)
    {
        // Force resolution (the registry is created/loaded by the Singleton factory the moment
        // it's first requested — taking it as a constructor parameter is enough).
        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(_log,
            "Skill registry eager-loaded with {Count} skills", _registry.Metadata.Count);
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken ct) =>
        System.Threading.Tasks.Task.CompletedTask;
}
