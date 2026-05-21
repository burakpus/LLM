using SetYazilim.Llm.Context;
using SetYazilim.Llm.Memory;
using SetYazilim.Llm.Models;

namespace SetYazilim.Llm;

// ── Models ────────────────────────────────────────────────────────────────────

public sealed class ChatRequest
{
    public required string SessionId  { get; init; }
    public required string UserId     { get; init; }
    public required string AgentId    { get; init; }
    public required string SkillName  { get; init; }
    public required string Message    { get; init; }
    public string[]?       Collections { get; init; }
    public string?         MetadataFilter { get; init; }
    public int             TokenBudget { get; init; } = 4000;
    public bool            Stream      { get; init; } = false;
}

public sealed class ChatResponse
{
    public required string Content      { get; init; }
    public required string SessionId    { get; init; }
    public int             KbHits       { get; init; }
    public int             MemoryHits   { get; init; }
    public int             EstTokens    { get; init; }
}

// ── Interface ─────────────────────────────────────────────────────────────────

public interface IAgentChat
{
    Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken ct = default);
    IAsyncEnumerable<string> StreamAsync(ChatRequest request, CancellationToken ct = default);
}

// ── Implementation ────────────────────────────────────────────────────────────

/// <summary>
/// Top-level agent chat orchestrator.
/// Flow: build context → call LLM → persist session → return response.
/// </summary>
public sealed class AgentChat : IAgentChat
{
    private readonly IContextBuilder _ctx;
    private readonly ILlmClient      _llm;
    private readonly ISessionMemory  _session;

    public AgentChat(
        IContextBuilder ctx,
        ILlmClient      llm,
        ISessionMemory  session)
    {
        _ctx     = ctx;
        _llm     = llm;
        _session = session;
    }

    public async Task<ChatResponse> ChatAsync(ChatRequest req, CancellationToken ct = default)
    {
        var built = await _ctx.BuildAsync(new AgentContext
        {
            AgentId        = req.AgentId,
            SkillName      = req.SkillName,
            UserId         = req.UserId,
            SessionId      = req.SessionId,
            UserQuery      = req.Message,
            TokenBudget    = req.TokenBudget,
            Collections    = req.Collections,
            MetadataFilter = req.MetadataFilter
        }, ct);

        // Prepend system prompt as first message
        var allMessages = new List<ChatMessage>
        {
            new("system", built.SystemPrompt)
        };
        allMessages.AddRange(built.Messages);

        var llmResp = await _llm.CompleteAsync(new ChatCompletionRequest
        {
            Model       = LlmModel.Chat.ToApiName(),
            Messages    = allMessages,
            Temperature = 0.3,
            MaxTokens   = 8192,
            User        = req.UserId
        }, ct);

        var answer = llmResp.Choices.FirstOrDefault()?.Message.Content
                     ?? "(boş yanıt)";

        // Persist to session
        await _session.AppendAsync(req.SessionId, req.UserId, req.AgentId,
            new MemoryMessage("user", req.Message,
                TokenCount: req.Message.Length / 4), ct);

        await _session.AppendAsync(req.SessionId, req.UserId, req.AgentId,
            new MemoryMessage("assistant", answer,
                TokenCount: answer.Length / 4), ct);

        return new ChatResponse
        {
            Content     = answer,
            SessionId   = req.SessionId,
            KbHits      = built.KbHits,
            MemoryHits  = built.MemoryHits,
            EstTokens   = built.EstimatedTokens
        };
    }

    public async IAsyncEnumerable<string> StreamAsync(
        ChatRequest req,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var built = await _ctx.BuildAsync(new AgentContext
        {
            AgentId        = req.AgentId,
            SkillName      = req.SkillName,
            UserId         = req.UserId,
            SessionId      = req.SessionId,
            UserQuery      = req.Message,
            TokenBudget    = req.TokenBudget,
            Collections    = req.Collections,
            MetadataFilter = req.MetadataFilter
        }, ct);

        var allMessages = new List<ChatMessage> { new("system", built.SystemPrompt) };
        allMessages.AddRange(built.Messages);

        var fullAnswer = new System.Text.StringBuilder();

        await foreach (var chunk in _llm.StreamAsync(new ChatCompletionRequest
        {
            Model       = LlmModel.Chat.ToApiName(),
            Messages    = allMessages,
            Temperature = 0.3,
            MaxTokens   = 8192,
            Stream      = true,
            User        = req.UserId
        }, ct))
        {
            var delta = chunk.Choices.FirstOrDefault()?.Delta.Content;
            if (!string.IsNullOrEmpty(delta))
            {
                fullAnswer.Append(delta);
                yield return delta;
            }
        }

        // Persist after stream completes
        var answer = fullAnswer.ToString();
        if (!string.IsNullOrEmpty(answer))
        {
            await _session.AppendAsync(req.SessionId, req.UserId, req.AgentId,
                new MemoryMessage("user", req.Message, TokenCount: req.Message.Length / 4), ct);
            await _session.AppendAsync(req.SessionId, req.UserId, req.AgentId,
                new MemoryMessage("assistant", answer, TokenCount: answer.Length / 4), ct);
        }
    }
}
