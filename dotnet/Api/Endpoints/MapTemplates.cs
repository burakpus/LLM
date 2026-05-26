using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// /api/templates (read, all users) + /api/admin/templates/* (CRUD, admin) — prompt templates
/// with {{variable}} extraction.
/// </summary>
public static class TemplatesEndpoints
{
    /// <summary>Extract {{variable}} names from template content.</summary>
    private static string[] ExtractTemplateVars(string content) =>
        System.Text.RegularExpressions.Regex.Matches(content, @"\{\{(\w+)\}\}")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToArray();

    public static IEndpointRouteBuilder MapTemplates(this IEndpointRouteBuilder app)
    {
        // GET /api/templates — list all (all authenticated users, for chat slash picker)
        app.MapGet("/api/templates", [Authorize] async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = @"SELECT id, name, content, variables, collection, created_by, created_at
                                FROM prompt_templates ORDER BY collection, name";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var rows = new List<object>();
            while (await r.ReadAsync(ct))
                rows.Add(new {
                    id         = r.GetInt32(0),
                    name       = r.GetString(1),
                    content    = r.GetString(2),
                    variables  = JsonSerializer.Deserialize<string[]>(r.GetString(3)) ?? Array.Empty<string>(),
                    collection = r.GetString(4),
                    createdBy  = r.GetString(5),
                    createdAt  = r.GetDateTime(6),
                });
            return Results.Ok(rows);
        });

        // POST /api/admin/templates — create (admin only)
        app.MapPost("/api/admin/templates", [Authorize("AdminOnly")] async (
            [FromBody] TemplateUpsertRequest req,
            ClaimsPrincipal user,
            NpgsqlDataSource ds,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Content))
                return Results.BadRequest(new { error = "Name and Content are required" });

            var vars     = ExtractTemplateVars(req.Content);
            var varsJson = JsonSerializer.Serialize(vars);
            var username = user.FindFirstValue(ClaimTypes.Name) ?? "admin";

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO prompt_templates (name, content, variables, collection, created_by)
                                VALUES ($1, $2, $3, $4, $5) RETURNING id";
            cmd.Parameters.AddWithValue(req.Name.Trim());
            cmd.Parameters.AddWithValue(req.Content);
            cmd.Parameters.AddWithValue(varsJson);
            cmd.Parameters.AddWithValue(req.Collection?.Trim() ?? "");
            cmd.Parameters.AddWithValue(username);
            var id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            _ = ActivityLogger.LogAsync(ds, username, "template.create", req.Name.Trim(), $"collection={req.Collection ?? ""}");
            return Results.Ok(new { id, name = req.Name.Trim(), variables = vars });
        });

        // PUT /api/admin/templates/{id} — update (admin only)
        app.MapPut("/api/admin/templates/{id:int}", [Authorize("AdminOnly")] async (
            int id,
            [FromBody] TemplateUpsertRequest req,
            ClaimsPrincipal user,
            NpgsqlDataSource ds,
            CancellationToken ct) =>
        {
            var vars     = ExtractTemplateVars(req.Content);
            var varsJson = JsonSerializer.Serialize(vars);
            var username = user.FindFirstValue(ClaimTypes.Name) ?? "admin";

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = @"UPDATE prompt_templates
                                SET name=$1, content=$2, variables=$3, collection=$4, updated_at=NOW()
                                WHERE id=$5";
            cmd.Parameters.AddWithValue(req.Name.Trim());
            cmd.Parameters.AddWithValue(req.Content);
            cmd.Parameters.AddWithValue(varsJson);
            cmd.Parameters.AddWithValue(req.Collection?.Trim() ?? "");
            cmd.Parameters.AddWithValue(id);
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            if (rows > 0) _ = ActivityLogger.LogAsync(ds, username, "template.update", req.Name.Trim());
            return rows == 0 ? Results.NotFound() : Results.Ok(new { id, variables = vars });
        });

        // DELETE /api/admin/templates/{id} — delete (admin only)
        app.MapDelete("/api/admin/templates/{id:int}", [Authorize("AdminOnly")] async (
            int id, ClaimsPrincipal user, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var username = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM prompt_templates WHERE id=$1";
            cmd.Parameters.AddWithValue(id);
            await cmd.ExecuteNonQueryAsync(ct);
            _ = ActivityLogger.LogAsync(ds, username, "template.delete", $"id={id}");
            return Results.NoContent();
        });

        return app;
    }
}
