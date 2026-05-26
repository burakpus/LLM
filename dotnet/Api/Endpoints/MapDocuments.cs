using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using SetYazilim.Llm.Api.Admin;
using SetYazilim.Llm.Retrieval;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// /api/ingest (user) + /api/admin/upload + /api/admin/documents + /api/admin/collections.
/// RAG document ingestion, listing and deletion.
/// </summary>
public static class DocumentsEndpoints
{
    public static IEndpointRouteBuilder MapDocuments(this IEndpointRouteBuilder app)
    {
        // POST /api/ingest — programmatic ingest (e.g. from agents)
        app.MapPost("/api/ingest", [Authorize] async (
            [FromBody] ApiIngestRequest req,
            IDocumentIngestion ingestion,
            CancellationToken ct) =>
        {
            var result = await ingestion.IngestAsync(new IngestRequest
            {
                Collection   = req.Collection,
                Source       = req.Source,
                Title        = req.Title,
                Content      = req.Content,
                Metadata     = req.Metadata ?? "{}",
                ChunkSize    = req.ChunkSize   ?? 1600,
                ChunkOverlap = req.ChunkOverlap ?? 200
            }, ct);
            return Results.Ok(result);
        });

        app.MapDelete("/api/ingest/{collection}/{*source}", [Authorize] async (
            string collection, string source,
            IDocumentIngestion ingestion, CancellationToken ct) =>
        {
            var n = await ingestion.DeleteSourceAsync(collection, source, ct);
            return Results.Ok(new { deleted = n });
        });

        // POST /api/admin/upload — multipart file upload, auto-parse and ingest
        app.MapPost("/api/admin/upload", [Authorize("AdminOnly")] async (
            HttpContext http,
            IDocumentIngestion ingestion,
            ClaimsPrincipal user,
            NpgsqlDataSource ds,
            CancellationToken ct) =>
        {
            if (!http.Request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required" });

            var form       = await http.Request.ReadFormAsync(ct);
            var collection = form["collection"].FirstOrDefault() ?? "default";
            var username   = user.FindFirstValue(ClaimTypes.Name) ?? "unknown";
            var results    = new List<object>();

            foreach (var file in form.Files)
            {
                try
                {
                    using var ms = new MemoryStream();
                    await file.CopyToAsync(ms, ct);
                    var text = DocumentParser.ExtractText(ms.ToArray(), file.FileName);

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        results.Add(new { file = file.FileName, ok = false, error = "No text extracted" });
                        continue;
                    }

                    var r = await ingestion.IngestAsync(new IngestRequest
                    {
                        Collection   = collection,
                        Source       = file.FileName,
                        Title        = Path.GetFileNameWithoutExtension(file.FileName),
                        Content      = text,
                        Metadata     = $"{{\"filename\":\"{file.FileName}\",\"size\":{file.Length}}}",
                        ChunkSize    = 1600,
                        ChunkOverlap = 200
                    }, ct);

                    results.Add(new { file = file.FileName, ok = true, chunks = r.ChunksCreated, tokens = r.TokensEstimate });
                    _ = ActivityLogger.LogAsync(ds, username, "document.upload", file.FileName, $"collection={collection} chunks={r.ChunksCreated}");
                    LlmMetrics.IngestChunksTotal.WithLabels(collection).Inc(r.ChunksCreated);
                }
                catch (Exception ex)
                {
                    results.Add(new { file = file.FileName, ok = false, error = ex.Message });
                }
            }
            return Results.Ok(results);
        });

        // GET /api/admin/documents?collection=xxx&page=1&pageSize=20
        app.MapGet("/api/admin/documents", [Authorize("AdminOnly")] async (
            string? collection,
            int page,
            int pageSize,
            NpgsqlDataSource ds,
            CancellationToken ct) =>
        {
            page     = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 5, 100);
            var offset = (page - 1) * pageSize;

            await using var conn = await ds.OpenConnectionAsync(ct);

            // Total count
            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = collection is null
                ? "SELECT COUNT(DISTINCT source) FROM kb_documents"
                : "SELECT COUNT(DISTINCT source) FROM kb_documents WHERE collection = $1";
            if (collection is not null) countCmd.Parameters.AddWithValue(collection);
            var total = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct));

            // Paginated sources
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = collection is null
                ? @"SELECT collection, source, MAX(title) as title,
                           COUNT(*) as chunks, MAX(updated_at) as updated_at
                    FROM kb_documents
                    GROUP BY collection, source
                    ORDER BY MAX(updated_at) DESC
                    LIMIT $1 OFFSET $2"
                : @"SELECT collection, source, MAX(title) as title,
                           COUNT(*) as chunks, MAX(updated_at) as updated_at
                    FROM kb_documents
                    WHERE collection = $3
                    GROUP BY collection, source
                    ORDER BY MAX(updated_at) DESC
                    LIMIT $1 OFFSET $2";
            cmd.Parameters.AddWithValue(pageSize);
            cmd.Parameters.AddWithValue(offset);
            if (collection is not null) cmd.Parameters.AddWithValue(collection);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var docs = new List<object>();
            while (await reader.ReadAsync(ct))
                docs.Add(new {
                    collection = reader.GetString(0),
                    source     = reader.GetString(1),
                    title      = reader.GetString(2),
                    chunks     = reader.GetInt64(3),
                    updatedAt  = reader.GetDateTime(4)
                });

            return Results.Ok(new { total, page, pageSize, items = docs });
        });

        // GET /api/admin/collections
        app.MapGet("/api/admin/collections", [Authorize("AdminOnly")] async (
            NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = @"SELECT collection, COUNT(DISTINCT source) as sources,
                                       COUNT(*) as chunks, MAX(updated_at) as last_updated
                                FROM kb_documents GROUP BY collection ORDER BY collection";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var cols = new List<object>();
            while (await r.ReadAsync(ct))
                cols.Add(new {
                    collection  = r.GetString(0),
                    sources     = r.GetInt64(1),
                    chunks      = r.GetInt64(2),
                    lastUpdated = r.GetDateTime(3)
                });
            return Results.Ok(cols);
        });

        // DELETE /api/admin/documents/{collection}/{*source}
        app.MapDelete("/api/admin/documents/{collection}/{*source}", [Authorize("AdminOnly")] async (
            string collection, string source,
            IDocumentIngestion ingestion,
            ClaimsPrincipal user,
            NpgsqlDataSource ds,
            CancellationToken ct) =>
        {
            var n        = await ingestion.DeleteSourceAsync(collection, source, ct);
            var username = user.FindFirstValue(ClaimTypes.Name) ?? "unknown";
            _ = ActivityLogger.LogAsync(ds, username, "document.delete", source, $"collection={collection} chunks={n}");
            return Results.Ok(new { deleted = n });
        });

        // DELETE /api/admin/collections/{collection} — bulk delete ALL sources in a collection
        // Used to re-ingest after schema changes (e.g. data dictionary view restructure).
        app.MapDelete("/api/admin/collections/{collection}", [Authorize("AdminOnly")] async (
            string collection,
            IDocumentIngestion ingestion,
            ClaimsPrincipal user,
            NpgsqlDataSource ds,
            CancellationToken ct) =>
        {
            var n        = await ingestion.DeleteCollectionAsync(collection, ct);
            var username = user.FindFirstValue(ClaimTypes.Name) ?? "unknown";
            _ = ActivityLogger.LogAsync(ds, username, "collection.delete", collection, $"chunks={n}");
            return Results.Ok(new { collection, deleted = n });
        });

        return app;
    }
}
