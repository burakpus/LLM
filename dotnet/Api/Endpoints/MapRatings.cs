using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// /api/ratings (user) + /api/admin/ratings/stats (admin) — thumbs up/down on assistant messages.
/// </summary>
public static class RatingsEndpoints
{
    public static IEndpointRouteBuilder MapRatings(this IEndpointRouteBuilder app)
    {
        // POST /api/ratings — submit or update a rating (all authenticated users)
        app.MapPost("/api/ratings", [Authorize] async (
            [FromBody] RatingRequest req,
            ClaimsPrincipal user,
            NpgsqlDataSource ds,
            CancellationToken ct) =>
        {
            if (req.Rating != 1 && req.Rating != -1)
                return Results.BadRequest(new { error = "Rating must be 1 or -1" });

            var username = user.FindFirstValue(ClaimTypes.Name) ?? "anonymous";
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO message_ratings (username, conv_id, message_id, rating, model)
                VALUES ($1, $2, $3, $4, $5)
                ON CONFLICT (username, message_id)
                DO UPDATE SET rating = $4, model = $5";
            cmd.Parameters.AddWithValue(username);
            cmd.Parameters.AddWithValue(req.ConvId);
            cmd.Parameters.AddWithValue(req.MessageId);
            cmd.Parameters.AddWithValue((short)req.Rating);
            cmd.Parameters.AddWithValue(string.IsNullOrEmpty(req.Model) ? (object)DBNull.Value : req.Model);
            await cmd.ExecuteNonQueryAsync(ct);
            LlmMetrics.RatingsTotal.WithLabels(req.Rating == 1 ? "up" : "down").Inc();
            return Results.Ok(new { ok = true });
        });

        // GET /api/admin/ratings/stats — rating statistics (admin only)
        app.MapGet("/api/admin/ratings/stats", [Authorize("AdminOnly")] async (
            NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);

            // Overall totals
            await using var c1 = conn.CreateCommand();
            c1.CommandText = @"SELECT COUNT(*),
                                      COALESCE(SUM(CASE WHEN rating=1  THEN 1 ELSE 0 END),0),
                                      COALESCE(SUM(CASE WHEN rating=-1 THEN 1 ELSE 0 END),0)
                               FROM message_ratings";
            await using var r1 = await c1.ExecuteReaderAsync(ct);
            await r1.ReadAsync(ct);
            var total = r1.GetInt64(0); var ups = r1.GetInt64(1); var downs = r1.GetInt64(2);
            await r1.CloseAsync();

            // By model
            await using var c2 = conn.CreateCommand();
            c2.CommandText = @"SELECT COALESCE(model,'unknown'),
                                      COUNT(*),
                                      COALESCE(SUM(CASE WHEN rating=1  THEN 1 ELSE 0 END),0),
                                      COALESCE(SUM(CASE WHEN rating=-1 THEN 1 ELSE 0 END),0)
                               FROM message_ratings
                               GROUP BY model ORDER BY COUNT(*) DESC";
            await using var r2 = await c2.ExecuteReaderAsync(ct);
            var byModel = new List<object>();
            while (await r2.ReadAsync(ct))
                byModel.Add(new { model = r2.GetString(0), total = r2.GetInt64(1), ups = r2.GetInt64(2), downs = r2.GetInt64(3) });
            await r2.CloseAsync();

            // Recent 20
            await using var c3 = conn.CreateCommand();
            c3.CommandText = @"SELECT username, rating, COALESCE(model,'?'), created_at
                               FROM message_ratings ORDER BY created_at DESC LIMIT 20";
            await using var r3 = await c3.ExecuteReaderAsync(ct);
            var recent = new List<object>();
            while (await r3.ReadAsync(ct))
                recent.Add(new { username = r3.GetString(0), rating = (int)r3.GetInt16(1), model = r3.GetString(2), createdAt = r3.GetDateTime(3) });

            return Results.Ok(new { total, ups, downs, byModel, recent });
        });

        return app;
    }
}
