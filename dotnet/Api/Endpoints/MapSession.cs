using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using SetYazilim.Llm.Memory;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// /api/session/{sessionId} — read or clear chat memory window.
/// </summary>
public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSession(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/session/{sessionId}", [Authorize] async (
            string sessionId,
            ISessionMemory session,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.Name) ?? "anonymous";
            var msgs = await session.GetWindowAsync(sessionId, userId, maxTokens: 8000, ct: ct);
            return Results.Ok(msgs);
        });

        app.MapDelete("/api/session/{sessionId}", [Authorize] async (
            string sessionId,
            ISessionMemory session,
            CancellationToken ct) =>
        {
            await session.ClearAsync(sessionId, ct);
            return Results.NoContent();
        });

        return app;
    }
}
