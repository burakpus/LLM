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

        // GET /api/admin/documents?collection=xxx&page=1&pageSize=50&q=keyword
        // q: optional ILIKE substring match on source OR title (case-insensitive)
        app.MapGet("/api/admin/documents", [Authorize("AdminOnly")] async (
            string? collection,
            int page,
            int pageSize,
            string? q,
            NpgsqlDataSource ds,
            CancellationToken ct) =>
        {
            page     = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 5, 1000);   // allow up to 1000 per page (was 100)
            var offset = (page - 1) * pageSize;
            var hasQ   = !string.IsNullOrWhiteSpace(q);
            var qLike  = hasQ ? "%" + q!.Trim() + "%" : null;

            // Build WHERE dynamically — positional $N parameters in order:
            //   $1 = limit, $2 = offset, then optional [$3 = collection], [$N = qLike]
            var whereParts = new List<string>();
            int next = 3;
            int colP = 0, qP = 0;
            if (collection is not null) { colP = next++; whereParts.Add($"collection = ${colP}"); }
            if (hasQ)                   { qP   = next++; whereParts.Add($"(source ILIKE ${qP} OR title ILIKE ${qP})"); }
            var whereSql = whereParts.Count > 0 ? "WHERE " + string.Join(" AND ", whereParts) : "";

            await using var conn = await ds.OpenConnectionAsync(ct);

            // Total count
            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(DISTINCT source) FROM kb_documents {whereSql}";
            if (colP > 0) countCmd.Parameters.AddWithValue(collection!);
            if (qP   > 0) countCmd.Parameters.AddWithValue(qLike!);
            var total = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct));

            // Paginated sources
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT collection, source, MAX(title) as title,
                       COUNT(*) as chunks, MAX(updated_at) as updated_at
                FROM   kb_documents
                {whereSql}
                GROUP  BY collection, source
                ORDER  BY MAX(updated_at) DESC
                LIMIT  $1 OFFSET $2";
            cmd.Parameters.AddWithValue(pageSize);
            cmd.Parameters.AddWithValue(offset);
            if (colP > 0) cmd.Parameters.AddWithValue(collection!);
            if (qP   > 0) cmd.Parameters.AddWithValue(qLike!);

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

        // GET /api/admin/collections — includes priority/dataType/description settings (left-joined)
        app.MapGet("/api/admin/collections", [Authorize("AdminOnly")] async (
            NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT k.collection,
                       COUNT(DISTINCT k.source) AS sources,
                       COUNT(*)                 AS chunks,
                       MAX(k.updated_at)        AS last_updated,
                       COALESCE(cs.priority, 'normal') AS priority,
                       COALESCE(cs.data_type, '')      AS data_type,
                       COALESCE(cs.description, '')    AS description
                FROM   kb_documents k
                LEFT   JOIN collection_settings cs ON cs.collection = k.collection
                GROUP  BY k.collection, cs.priority, cs.data_type, cs.description
                ORDER  BY k.collection";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var cols = new List<object>();
            while (await r.ReadAsync(ct))
                cols.Add(new {
                    collection  = r.GetString(0),
                    sources     = r.GetInt64(1),
                    chunks      = r.GetInt64(2),
                    lastUpdated = r.GetDateTime(3),
                    priority    = r.GetString(4),
                    dataType    = r.GetString(5),
                    description = r.GetString(6),
                });
            return Results.Ok(cols);
        });

        // PUT /api/admin/collections/{collection}/settings — update priority/dataType/description
        // body: { priority?: 'high'|'normal'|'low'|'hidden', dataType?: string, description?: string }
        app.MapPut("/api/admin/collections/{collection}/settings", [Authorize("AdminOnly")] async (
            string collection,
            [FromBody] CollectionSettingsRequest req,
            ClaimsPrincipal user,
            NpgsqlDataSource ds,
            CancellationToken ct) =>
        {
            var allowed = new[] { "high", "normal", "low", "hidden" };
            var pri = (req.Priority ?? "normal").ToLowerInvariant();
            if (!allowed.Contains(pri))
                return Results.BadRequest(new { error = $"priority must be one of {string.Join(",", allowed)}" });

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO collection_settings (collection, priority, data_type, description, updated_at)
                                VALUES ($1, $2, $3, $4, NOW())
                                ON CONFLICT (collection) DO UPDATE
                                SET priority   = $2,
                                    data_type  = $3,
                                    description = $4,
                                    updated_at = NOW()";
            cmd.Parameters.AddWithValue(collection);
            cmd.Parameters.AddWithValue(pri);
            cmd.Parameters.AddWithValue(req.DataType ?? "");
            cmd.Parameters.AddWithValue(req.Description ?? "");
            await cmd.ExecuteNonQueryAsync(ct);

            var username = user.FindFirstValue(ClaimTypes.Name) ?? "admin";
            _ = ActivityLogger.LogAsync(ds, username, "collection.settings.update", collection,
                $"priority={pri} dataType={req.DataType ?? ""}");
            return Results.Ok(new { collection, priority = pri, dataType = req.DataType ?? "", description = req.Description ?? "" });
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
