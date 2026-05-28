using SetYazilim.Llm.Memory;
using SetYazilim.Llm.Models;
using SetYazilim.Llm.Retrieval;

namespace SetYazilim.Llm.Context;

// ── Models ────────────────────────────────────────────────────────────────────

public sealed class AgentContext
{
    public required string AgentId      { get; init; }
    public required string SkillName    { get; init; }
    public required string UserId       { get; init; }
    public required string SessionId    { get; init; }
    public required string UserQuery    { get; init; }

    /// <summary>Max tokens the final prompt may consume (leave headroom for answer).</summary>
    public int TokenBudget { get; init; } = 4000;

    /// <summary>Optional collections to restrict KB retrieval.</summary>
    public string[]? Collections { get; init; }

    /// <summary>Optional JSONB filter e.g. '{"year":2024}'.</summary>
    public string? MetadataFilter { get; init; }
}

public sealed class BuiltContext
{
    public required string              SystemPrompt   { get; init; }
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public int                          EstimatedTokens { get; init; }
    public int                          KbHits          { get; init; }
    public int                          MemoryHits      { get; init; }
}

// ── Interface ─────────────────────────────────────────────────────────────────

public interface IContextBuilder
{
    Task<BuiltContext> BuildAsync(AgentContext ctx, CancellationToken ct = default);
}

// ── Implementation ────────────────────────────────────────────────────────────

/// <summary>
/// Assembles the full LLM prompt from:
///   1. Skill system prompt (loaded by agent dispatcher)
///   2. KB retrieval (hybrid search)
///   3. Agent memory (persistent facts for this user+skill)
///   4. Session history (rolling window)
///   5. Current user query
///
/// Respects <see cref="AgentContext.TokenBudget"/> — trims greedily if over budget.
/// Token counting is approximate (4 chars ≈ 1 token).
/// </summary>
public sealed class ContextBuilder : IContextBuilder
{
    private readonly IHybridSearch   _kb;
    private readonly IAgentMemory    _agentMem;
    private readonly ISessionMemory  _sessionMem;
    private readonly VectorStore.EmbeddingService _embed;
    private readonly SkillRegistry   _skills;
    private readonly IRagSynonymService _synonyms;

    public ContextBuilder(
        IHybridSearch   kb,
        IAgentMemory    agentMem,
        ISessionMemory  sessionMem,
        VectorStore.EmbeddingService embed,
        SkillRegistry   skills,
        IRagSynonymService synonyms)
    {
        _kb         = kb;
        _agentMem   = agentMem;
        _sessionMem = sessionMem;
        _embed      = embed;
        _skills     = skills;
        _synonyms   = synonyms;
    }

    public async Task<BuiltContext> BuildAsync(AgentContext ctx, CancellationToken ct = default)
    {
        int budget = ctx.TokenBudget;

        // 1. System prompt from skill registry — cap at 3000 tokens to preserve headroom
        var systemPrompt = _skills.GetSystemPrompt(ctx.AgentId, ctx.SkillName);
        const int MaxSystemPromptTokens = 3000;
        if (Estimate(systemPrompt) > MaxSystemPromptTokens)
        {
            var maxChars = MaxSystemPromptTokens * 4;
            systemPrompt = systemPrompt[..Math.Min(systemPrompt.Length, maxChars)]
                         + "\n\n[...skill truncated to fit context window...]";
        }
        budget -= Estimate(systemPrompt);

        // 2. Embed query (used for both KB and agent memory retrieval).
        //    Apply Turkish synonym expansion before embedding/FTS so that
        //    "vergi" matches columns named "VAT*", "müşteri" matches "Customer*", etc.
        //    The user's original query is unchanged in the LLM messages — only
        //    the retrieval channel sees the expanded form. Dictionary lives in
        //    `rag_synonyms` DB table; admin panel UI manages it (60s cache).
        var expandedQuery = _synonyms.Expand(ctx.UserQuery);
        var queryVec      = await _embed.EmbedAsync(expandedQuery, ct);

        // 3. KB retrieval — expanded query goes to both vector + FTS channels
        var kbHits = await _kb.SearchAsync(
            queryVec, expandedQuery,
            collections:    ctx.Collections,
            topK:           6,
            metadataFilter: ctx.MetadataFilter,
            ct:             ct);

        var kbBlock = BuildKbBlock(kbHits, ref budget);

        // 4. Agent memory
        var agentMems = await _agentMem.SearchAsync(
            ctx.AgentId, ctx.SkillName, ctx.UserId,
            queryVec, topK: 4, maxDistance: 0.5, ct: ct);

        var agentMemBlock = BuildAgentMemBlock(agentMems, ref budget);

        // 5. Session history
        var history = await _sessionMem.GetWindowAsync(
            ctx.SessionId, ctx.UserId,
            maxTokens: Math.Max(budget - Estimate(ctx.UserQuery) - 200, 0),
            ct: ct);

        // 6. Assemble messages
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrEmpty(kbBlock) || !string.IsNullOrEmpty(agentMemBlock))
        {
            var contextBlock = "";
            if (!string.IsNullOrEmpty(kbBlock))
                contextBlock += $"## İlgili Belgeler\n{kbBlock}\n\n";
            if (!string.IsNullOrEmpty(agentMemBlock))
                contextBlock += $"## Hatırlanan Bilgiler\n{agentMemBlock}\n";

            messages.Add(new ChatMessage("system",
                $"Aşağıdaki bağlamı kullanarak soruyu yanıtla:\n\n{contextBlock.Trim()}"));
        }

        foreach (var h in history)
            messages.Add(new ChatMessage(h.Role, h.Content));

        messages.Add(new ChatMessage("user", ctx.UserQuery));

        return new BuiltContext
        {
            SystemPrompt    = systemPrompt,
            Messages        = messages,
            EstimatedTokens = ctx.TokenBudget - budget + Estimate(ctx.UserQuery),
            KbHits          = kbHits.Count,
            MemoryHits      = agentMems.Count
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildKbBlock(
        IReadOnlyList<KbSearchResult> hits, ref int budget)
    {
        if (hits.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder();

        foreach (var h in hits)
        {
            var chunk = $"[{h.Title}] {h.Content}";
            int t = Estimate(chunk);
            if (budget - t < 500) break;
            sb.AppendLine(chunk);
            sb.AppendLine("---");
            budget -= t;
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildAgentMemBlock(
        IReadOnlyList<AgentMemorySearchResult> mems, ref int budget)
    {
        if (mems.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder();

        foreach (var m in mems)
        {
            int t = Estimate(m.Entry.Content);
            if (budget - t < 300) break;
            sb.AppendLine($"- {m.Entry.Content}");
            budget -= t;
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Rough token estimate: 4 chars ≈ 1 token (good enough for budget control).</summary>
    private static int Estimate(string text) => text.Length / 4 + 1;
}
