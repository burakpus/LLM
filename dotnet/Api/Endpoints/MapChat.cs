using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SetYazilim.Llm;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// /api/chat (one-shot) + /api/chat/stream (SSE) — agentic chat over IAgentChat.
/// </summary>
public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChat(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/chat", [Authorize] async (
            [FromBody] ApiChatRequest req,
            IAgentChat agentChat,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.Name) ?? "anonymous";

            var result = await agentChat.ChatAsync(new ChatRequest
            {
                SessionId      = req.SessionId,
                UserId         = userId,
                AgentId        = req.AgentId,
                SkillName      = req.SkillName,
                Message        = req.Message,
                Collections    = req.Collections,
                MetadataFilter = req.MetadataFilter,
                TokenBudget    = req.TokenBudget ?? 4000
            }, ct);

            return Results.Ok(new ApiChatResponse(
                result.Content,
                result.SessionId,
                result.KbHits,
                result.MemoryHits,
                result.EstTokens));
        });

        // POST /api/chat/stream — SSE streaming (token query string for EventSource)
        app.MapPost("/api/chat/stream", [Authorize] async (
            [FromBody] ApiChatRequest req,
            IAgentChat agentChat,
            ClaimsPrincipal user,
            HttpContext http,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.Name) ?? "anonymous";

            http.Response.Headers.ContentType  = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.Connection   = "keep-alive";

            await foreach (var token in agentChat.StreamAsync(new ChatRequest
            {
                SessionId      = req.SessionId,
                UserId         = userId,
                AgentId        = req.AgentId,
                SkillName      = req.SkillName,
                Message        = req.Message,
                Collections    = req.Collections,
                MetadataFilter = req.MetadataFilter,
                TokenBudget    = req.TokenBudget ?? 4000,
                Stream         = true
            }, ct))
            {
                var line = Encoding.UTF8.GetBytes($"data: {JsonSerializer.Serialize(new { token })}\n\n");
                await http.Response.Body.WriteAsync(line, ct);
                await http.Response.Body.FlushAsync(ct);
            }

            await http.Response.Body.WriteAsync(
                Encoding.UTF8.GetBytes("data: [DONE]\n\n"), ct);
        });

        return app;
    }
}
