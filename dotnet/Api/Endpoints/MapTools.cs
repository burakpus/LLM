using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SetYazilim.Llm.Api.Auth;
using SetYazilim.Llm.Api.Tools;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// /api/tools/* (file generation) and /api/admin/benchmark[s] (LLM concurrency test).
/// </summary>
public static class ToolsEndpoints
{
    public static IEndpointRouteBuilder MapTools(this IEndpointRouteBuilder app)
    {
        // POST /api/tools/generate-file — agent tool entry point
        // body: { kind: "docx"|"xlsx"|"pdf"|"pptx", filename: "report.docx", spec: {...} }
        app.MapPost("/api/tools/generate-file", [Authorize] async (
            [FromBody] FileGenRequest req,
            IFileGenerator gen,
            ClaimsPrincipal user,
            IEventLog evt,
            CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(req.Kind))
                return Results.BadRequest(new { error = "kind required" });

            var allowed = new[] { "docx", "xlsx", "pdf", "pptx" };
            if (!allowed.Contains(req.Kind.ToLowerInvariant()))
                return Results.BadRequest(new { error = $"kind must be one of: {string.Join(",", allowed)}" });

            var username = user.FindFirstValue(ClaimTypes.Name) ?? "anon";
            var result   = await gen.GenerateAsync(username, req, ct);

            await evt.LogAsync(EventCategory.Data,
                result.Ok ? EventSeverity.Info : EventSeverity.Warn,
                $"file.generate.{req.Kind}",
                result.Ok ? EventResult.Success : EventResult.Failure,
                reason: result.Error,
                action: "generate", resource: $"{req.Kind}:{result.Filename}",
                details: new { result.SizeBytes, result.Token }, ct: ct);

            return Results.Ok(result);
        });

        // GET /api/tools/generated/{token}/{filename} — download a generated file (user-scoped)
        app.MapGet("/api/tools/generated/{token}/{filename}", [Authorize] (
            string token, string filename,
            IFileGenerator gen,
            ClaimsPrincipal user) =>
        {
            var username = user.FindFirstValue(ClaimTypes.Name) ?? "anon";
            var path     = gen.Resolve(username, token, filename);
            if (path == null) return Results.NotFound();
            return Results.File(path, ContentTypes.Lookup(filename),
                fileDownloadName: filename, enableRangeProcessing: true);
        });

        // POST /api/admin/benchmark — run N concurrent /api/llm/completions calls
        // body: { model, concurrency, prompt, maxTokens, temperature, label? }
        app.MapPost("/api/admin/benchmark", [Authorize("AdminOnly")] async (
            [FromBody] BenchmarkRequest req,
            IBenchmarkService bench,
            HttpContext http,
            ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(req.Model)) return Results.BadRequest(new { error = "model required" });
            if (req.Concurrency < 1 || req.Concurrency > 200)
                return Results.BadRequest(new { error = "concurrency must be 1..200" });
            if (string.IsNullOrEmpty(req.Prompt)) return Results.BadRequest(new { error = "prompt required" });

            // Reuse the caller's JWT to call our own /api/llm/completions
            var auth = http.Request.Headers.Authorization.ToString();
            var jwt  = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..] : "";
            if (string.IsNullOrEmpty(jwt)) return Results.BadRequest(new { error = "missing bearer token" });

            var createdBy = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
            var result    = await bench.RunAsync(req, jwt, createdBy, ct);
            return Results.Ok(result);
        });

        // GET /api/admin/benchmarks?model=&limit=20
        app.MapGet("/api/admin/benchmarks", [Authorize("AdminOnly")] async (
            string? model, int? limit,
            IBenchmarkService bench,
            CancellationToken ct) =>
        {
            var items = await bench.ListAsync(model, limit ?? 20, ct);
            return Results.Ok(items);
        });

        return app;
    }
}
