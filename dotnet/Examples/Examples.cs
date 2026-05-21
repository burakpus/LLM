using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SetYazilim.Llm;
using SetYazilim.Llm.Models;
using SetYazilim.Llm.VectorStore;

namespace SetYazilim.Llm.Examples;

/// <summary>
/// Reference usage patterns. Not part of the published library — copy into your app.
/// </summary>
public static class Examples
{
    // ────────────────────────────────────────────────────────────────────────
    // 1) Program.cs — DI registration
    // ────────────────────────────────────────────────────────────────────────
    public static IHost BuildHost(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration(b =>
            {
                b.AddJsonFile("appsettings.json", optional: false)
                 .AddEnvironmentVariables(prefix: "LLM_");
            })
            .ConfigureServices((ctx, services) =>
            {
                services.AddLiteLLMClient();
                services.AddVectorStore();   // pgvector + EmbeddingService
                services.AddHostedService<DemoWorker>();
            })
            .Build();

    // ────────────────────────────────────────────────────────────────────────
    // 2) Simple ask (Gemma chat)
    // ────────────────────────────────────────────────────────────────────────
    public static async Task SimpleAsk(ILlmClient llm, CancellationToken ct)
    {
        var answer = await llm.AskAsync(
            LlmModel.Chat,
            prompt: "Bu evrakta neler eksik olabilir? Tek paragraf, Türkçe.",
            systemPrompt: "Sen finans operasyon ekibinin AI asistanısın. Detaylı ve teknik konuş.",
            cancellationToken: ct);

        Console.WriteLine(answer);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 3) Code generation (Qwen Coder)
    // ────────────────────────────────────────────────────────────────────────
    public static async Task<string> GenerateRefactoring(ILlmClient llm, string code, CancellationToken ct)
    {
        var resp = await llm.CompleteAsync(new ChatCompletionRequest
        {
            Model = LlmModel.Code.ToApiName(),
            Temperature = 0.1,
            MaxTokens = 4096,
            Messages =
            [
                new("system", "You are a senior C# code reviewer. Output ONLY refactored code, no commentary."),
                new("user", $"Refactor for SOLID + readability:\n\n```csharp\n{code}\n```")
            ]
        }, ct);

        return resp.Choices[0].Message.Content;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 4) Streaming (token-by-token UX)
    // ────────────────────────────────────────────────────────────────────────
    public static async Task StreamToConsole(ILlmClient llm, CancellationToken ct)
    {
        var request = new ChatCompletionRequest
        {
            Model = LlmModel.Chat.ToApiName(),
            Messages = [new("user", "Açıkla: SOLID prensipleri kısa madde madde.")],
            Stream = true,
            Temperature = 0.7
        };

        await foreach (var chunk in llm.StreamAsync(request, ct))
        {
            var delta = chunk.Choices.FirstOrDefault()?.Delta.Content;
            if (!string.IsNullOrEmpty(delta))
                Console.Write(delta);
        }
        Console.WriteLine();
    }

    // ────────────────────────────────────────────────────────────────────────
    // 5) Reasoning with graceful 503 handling (cold tier swap)
    // ────────────────────────────────────────────────────────────────────────
    public static async Task<string?> ReasoningWithFallback(
        ILlmClient llm,
        IReasoningJobQueue queue,
        string prompt,
        string userId,
        CancellationToken ct)
    {
        try
        {
            var resp = await llm.CompleteAsync(new ChatCompletionRequest
            {
                Model = LlmModel.Reason.ToApiName(),
                Messages = [new("user", prompt)],
                MaxTokens = 8192,
                User = userId
            }, ct);

            return resp.Choices[0].Message.Content;
        }
        catch (LlmModelUnavailableException ex)
        {
            // Cold tier not loaded → enqueue async, notify via webhook later
            await queue.EnqueueAsync(new ReasoningJob
            {
                UserId = userId,
                Prompt = prompt,
                EstimatedReadyAt = DateTimeOffset.UtcNow.Add(ex.RetryAfter ?? TimeSpan.FromMinutes(8))
            }, ct);
            return null; // UI shows "Processing... we'll notify you"
        }
    }
}

    // ────────────────────────────────────────────────────────────────────────
    // 6) Vector store: index a document, then semantic search
    // ────────────────────────────────────────────────────────────────────────
    public static async Task VectorStoreDemo(
        IVectorStore vectorStore,
        EmbeddingService embedSvc,
        CancellationToken ct)
    {
        const string COLLECTION = "finance_docs";

        // Ensure the collection table + HNSW index exist (idempotent)
        await vectorStore.EnsureCollectionAsync(COLLECTION, dimensions: 768, ct);

        // Build documents
        var documents = new List<VectorDocument>
        {
            new() { Collection = COLLECTION, Content = "Q1 2024 net revenue artışı %18 olarak gerçekleşti.", Metadata = """{"source":"q1-report","year":2024}""" },
            new() { Collection = COLLECTION, Content = "Stok devir hızı bu çeyrekte 4.2x seviyesine ulaştı.", Metadata = """{"source":"ops-report","year":2024}""" },
            new() { Collection = COLLECTION, Content = "EBITDA marjı baskı altında: hammadde maliyetleri %23 yükseldi.", Metadata = """{"source":"cfo-brief","year":2024}""" },
        };

        // Embed & upsert
        await embedSvc.EmbedDocumentsAsync(documents, ct);
        await vectorStore.UpsertBatchAsync(documents, ct);
        Console.WriteLine($"Indexed {documents.Count} documents.");

        // Semantic search
        var queryEmbedding = await embedSvc.EmbedAsync("Bu çeyrekte karlılık nasıl?", ct);
        var results = await vectorStore.SearchAsync(
            collection: COLLECTION,
            queryEmbedding: queryEmbedding,
            topK: 3,
            maxDistance: 0.6,
            ct: ct);

        Console.WriteLine("\nSearch results:");
        foreach (var r in results)
            Console.WriteLine($"  [{r.Similarity:P0}] {r.Document.Content}");
    }

    // ────────────────────────────────────────────────────────────────────────
    // 7) RAG pattern: retrieve context, then answer with LLM
    // ────────────────────────────────────────────────────────────────────────
    public static async Task<string> RagAnswer(
        ILlmClient llm,
        IVectorStore vectorStore,
        EmbeddingService embedSvc,
        string userQuestion,
        CancellationToken ct)
    {
        // 1. Embed the question
        var queryEmbedding = await embedSvc.EmbedAsync(userQuestion, ct);

        // 2. Retrieve top-3 relevant chunks
        var hits = await vectorStore.SearchAsync(
            collection: "finance_docs",
            queryEmbedding: queryEmbedding,
            topK: 3,
            maxDistance: 0.55,
            ct: ct);

        if (hits.Count == 0)
            return "Bu konuda belgelerimde ilgili bir bilgi bulamadım.";

        var context = string.Join("\n---\n", hits.Select(h => h.Document.Content));

        // 3. Generate answer with retrieved context
        var answer = await llm.AskAsync(
            LlmModel.Chat,
            prompt: $"""
                Kullanıcı sorusu: {userQuestion}

                İlgili belgeler:
                {context}

                Sadece yukarıdaki belgelere dayanarak kısa ve net bir Türkçe cevap ver.
                """,
            systemPrompt: "Sen bir finans analistinin AI asistanısın. Yalnızca verilen bağlamı kullan.",
            cancellationToken: ct);

        return answer;
    }

// Demonstration types
public sealed record ReasoningJob
{
    public required string UserId { get; init; }
    public required string Prompt { get; init; }
    public DateTimeOffset EstimatedReadyAt { get; init; }
}

public interface IReasoningJobQueue
{
    Task<string> EnqueueAsync(ReasoningJob job, CancellationToken ct);
}

internal sealed class DemoWorker : BackgroundService
{
    private readonly ILlmClient _llm;
    private readonly ILogger<DemoWorker> _logger;

    public DemoWorker(ILlmClient llm, ILogger<DemoWorker> logger)
    {
        _llm = llm;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = await _llm.AskAsync(
                LlmModel.Chat,
                prompt: "Test prompt — bir kelimelik cevap ver.",
                cancellationToken: stoppingToken);
            _logger.LogInformation("LLM responded: {Response}", result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Demo failed");
        }
    }
}
