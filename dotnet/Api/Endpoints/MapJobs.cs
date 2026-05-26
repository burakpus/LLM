using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Npgsql;
using SetYazilim.Llm.Api.Jobs;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// /api/jobs/* + /api/admin/jobs/* — background job inspection and control.
/// </summary>
public static class JobsEndpoints
{
    public static IEndpointRouteBuilder MapJobs(this IEndpointRouteBuilder app)
    {
        // GET /api/jobs/{id} — single job status
        app.MapGet("/api/jobs/{id:long}", [Authorize] async (long id, IJobService jobs, CancellationToken ct) =>
        {
            var j = await jobs.GetAsync(id, ct);
            if (j is null) return Results.NotFound();
            return Results.Ok(ActivityLogger.SerializeJob(j));
        });

        // GET /api/jobs?limit=20&status=running
        app.MapGet("/api/jobs", [Authorize("AdminOnly")] async (
            int? limit, string? status, IJobService jobs, CancellationToken ct) =>
        {
            var list = await jobs.ListRecentAsync(limit ?? 20, status, ct);
            return Results.Ok(list.Select(ActivityLogger.SerializeJob));
        });

        // GET /api/admin/jobs?page=1&pageSize=50&type=&status= — paged list for Jobs tab
        app.MapGet("/api/admin/jobs", [Authorize("AdminOnly")] async (
            int? page, int? pageSize, string? type, string? status,
            IJobService jobs, CancellationToken ct) =>
        {
            var p  = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 50, 10, 200);
            var (items, total) = await jobs.ListFilteredAsync(ps, (p - 1) * ps, type, status, ct);
            return Results.Ok(new {
                items = items.Select(ActivityLogger.SerializeJob),
                total,
                page = p,
                pageSize = ps,
            });
        });

        // POST /api/admin/jobs/{id}/cancel — only queued jobs can be cancelled
        app.MapPost("/api/admin/jobs/{id:long}/cancel", [Authorize("AdminOnly")] async (
            long id, IJobService jobs, NpgsqlDataSource ds, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var ok = await jobs.CancelAsync(id, ct);
            if (ok)
            {
                var who = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
                _ = ActivityLogger.LogAsync(ds, who, "job.cancel", $"jobId={id}");
                return Results.Ok(new { ok = true });
            }
            return Results.BadRequest(new { ok = false, error = "Yalnızca 'queued' durumdaki işler iptal edilebilir." });
        });

        // POST /api/admin/jobs/{id}/retry — re-enqueue a failed/cancelled job with same params
        app.MapPost("/api/admin/jobs/{id:long}/retry", [Authorize("AdminOnly")] async (
            long id, IJobService jobs, NpgsqlDataSource ds, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var who = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
            var newId = await jobs.RetryAsync(id, who, ct);
            if (newId is null)
                return Results.BadRequest(new { ok = false, error = "İş bulunamadı veya 'failed/cancelled' durumda değil." });
            _ = ActivityLogger.LogAsync(ds, who, "job.retry", $"oldId={id} newId={newId}");
            return Results.Ok(new { ok = true, newId });
        });

        return app;
    }
}
