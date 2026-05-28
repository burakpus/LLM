using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SetYazilim.Llm.Retrieval;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// /api/admin/rag/synonyms — CRUD endpoints for Turkish ↔ English synonym
/// dictionary used by RAG query expansion. Dictionary lives in `rag_synonyms`
/// table; <see cref="IRagSynonymService"/> caches it 60s.
/// </summary>
public static class RagEndpoints
{
    public static IEndpointRouteBuilder MapRag(this IEndpointRouteBuilder app)
    {
        // GET /api/admin/rag/synonyms — list all (admin only)
        app.MapGet("/api/admin/rag/synonyms", [Authorize("AdminOnly")] async (
            IRagSynonymService svc, CancellationToken ct) =>
        {
            var dict = await svc.GetAllAsync(ct);
            var rows = dict
                .OrderBy(kv => kv.Key)
                .Select(kv => new { term = kv.Key, synonyms = kv.Value })
                .ToList();
            return Results.Ok(rows);
        });

        // POST /api/admin/rag/synonyms — upsert (admin only)
        // body: { term: "vergi", synonyms: ["vat","kdv","tax"], notes?: "..." }
        app.MapPost("/api/admin/rag/synonyms", [Authorize("AdminOnly")] async (
            [FromBody] RagSynonymUpsertRequest req,
            ClaimsPrincipal user,
            IRagSynonymService svc,
            NpgsqlDataSource ds,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Term))
                return Results.BadRequest(new { error = "term required" });
            if (req.Synonyms is null || req.Synonyms.Length == 0)
                return Results.BadRequest(new { error = "at least one synonym required" });

            // Sanity: drop empty/duplicate synonyms (case-insensitive)
            var cleaned = req.Synonyms
                .Select(s => s?.Trim() ?? "")
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (cleaned.Length == 0)
                return Results.BadRequest(new { error = "all synonyms were empty" });

            var username = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
            await svc.UpsertAsync(req.Term, cleaned, req.Notes, username, ct);
            _ = ActivityLogger.LogAsync(ds, username, "rag.synonym.upsert", req.Term, $"synonyms={cleaned.Length}");
            return Results.Ok(new { term = req.Term.Trim().ToLowerInvariant(), synonyms = cleaned });
        });

        // DELETE /api/admin/rag/synonyms/{term}
        app.MapDelete("/api/admin/rag/synonyms/{term}", [Authorize("AdminOnly")] async (
            string term,
            ClaimsPrincipal user,
            IRagSynonymService svc,
            NpgsqlDataSource ds,
            CancellationToken ct) =>
        {
            var ok = await svc.DeleteAsync(term, ct);
            if (!ok) return Results.NotFound();
            var username = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
            _ = ActivityLogger.LogAsync(ds, username, "rag.synonym.delete", term);
            return Results.Ok(new { deleted = term.Trim().ToLowerInvariant() });
        });

        // POST /api/admin/rag/synonyms/test?q=... — debug helper: expand a query
        // and return the resulting string (read-only, useful for verifying changes).
        app.MapPost("/api/admin/rag/synonyms/test", [Authorize("AdminOnly")] (
            [FromBody] RagSynonymTestRequest req,
            IRagSynonymService svc) =>
        {
            if (string.IsNullOrWhiteSpace(req.Query))
                return Results.BadRequest(new { error = "query required" });
            var expanded = svc.Expand(req.Query);
            return Results.Ok(new
            {
                original = req.Query,
                expanded,
                added    = expanded.Length > req.Query.Length
                    ? expanded[req.Query.Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    : Array.Empty<string>()
            });
        });

        return app;
    }
}

public sealed record RagSynonymUpsertRequest(string Term, string[] Synonyms, string? Notes);
public sealed record RagSynonymTestRequest(string Query);
