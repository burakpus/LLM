using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;
using SetYazilim.Llm.VectorStore;

namespace SetYazilim.Llm.Retrieval;

// ── Models ────────────────────────────────────────────────────────────────────

public sealed class IngestRequest
{
    public required string Collection { get; init; }
    public required string Source     { get; init; }   // file path, URL, system name
    public required string Title      { get; init; }
    public required string Content    { get; init; }
    public string          Metadata   { get; init; } = "{}";

    /// <summary>Max characters per chunk (≈ 400 tokens).</summary>
    public int ChunkSize    { get; init; } = 1600;

    /// <summary>Overlap between consecutive chunks in characters.</summary>
    public int ChunkOverlap { get; init; } = 200;
}

public sealed class IngestResult
{
    public int    ChunksCreated  { get; init; }
    public int    TokensEstimate { get; init; }
    public string Collection     { get; init; } = string.Empty;
    public string Source         { get; init; } = string.Empty;
}

// ── Interface ─────────────────────────────────────────────────────────────────

public interface IDocumentIngestion
{
    Task<IngestResult> IngestAsync(IngestRequest request, CancellationToken ct = default);
    Task<int> DeleteSourceAsync(string collection, string source, CancellationToken ct = default);
}

// ── Implementation ────────────────────────────────────────────────────────────

public sealed class DocumentIngestion : IDocumentIngestion
{
    private readonly NpgsqlDataSource  _ds;
    private readonly EmbeddingService  _embed;
    private readonly ILogger<DocumentIngestion> _log;

    public DocumentIngestion(
        NpgsqlDataSource ds,
        EmbeddingService embed,
        ILogger<DocumentIngestion> log)
    {
        _ds    = ds;
        _embed = embed;
        _log   = log;
    }

    public async Task<IngestResult> IngestAsync(IngestRequest req, CancellationToken ct = default)
    {
        // 1. Chunk
        var chunks = Chunk(req.Content, req.ChunkSize, req.ChunkOverlap);
        _log.LogInformation("Ingesting '{Source}' → {Count} chunks into '{Col}'",
            req.Source, chunks.Count, req.Collection);

        // 2. Embed (batch, single round-trip)
        var texts      = chunks.Select(c => $"{req.Title}\n{c}").ToList();
        var embeddings = await _embed.EmbedBatchAsync(texts, ct);

        // 3. Delete existing chunks for this source (re-index pattern)
        await DeleteSourceAsync(req.Collection, req.Source, ct);

        // 4. Upsert
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var txn  = await conn.BeginTransactionAsync(ct);

        for (int i = 0; i < chunks.Count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = txn;
            cmd.CommandText = """
                INSERT INTO kb_documents
                    (collection, source, title, content, chunk_index, chunk_total, embedding, metadata)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb);
                """;
            cmd.Parameters.AddWithValue(req.Collection);
            cmd.Parameters.AddWithValue(req.Source);
            cmd.Parameters.AddWithValue(req.Title);
            cmd.Parameters.AddWithValue(chunks[i]);
            cmd.Parameters.AddWithValue(i);
            cmd.Parameters.AddWithValue(chunks.Count);
            cmd.Parameters.AddWithValue(new Vector(embeddings[i]));
            cmd.Parameters.AddWithValue(req.Metadata);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await txn.CommitAsync(ct);

        return new IngestResult
        {
            ChunksCreated  = chunks.Count,
            TokensEstimate = req.Content.Length / 4,
            Collection     = req.Collection,
            Source         = req.Source
        };
    }

    public async Task<int> DeleteSourceAsync(string collection, string source, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM kb_documents WHERE collection = $1 AND source = $2;";
        cmd.Parameters.AddWithValue(collection);
        cmd.Parameters.AddWithValue(source);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Chunking ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Recursive character text splitter.
    /// Splits on paragraphs → sentences → words → characters.
    /// </summary>
    private static IReadOnlyList<string> Chunk(string text, int chunkSize, int overlap)
    {
        var separators = new[] { "\n\n", "\n", ". ", " ", "" };
        return RecursiveSplit(text.Trim(), chunkSize, overlap, separators);
    }

    private static List<string> RecursiveSplit(
        string text, int size, int overlap, string[] separators)
    {
        if (text.Length <= size) return [text];

        var sep = separators.FirstOrDefault(s => !string.IsNullOrEmpty(s) && text.Contains(s))
                  ?? separators[^1];

        var parts  = string.IsNullOrEmpty(sep)
            ? SplitByChar(text, size)
            : text.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries)
                  .Where(p => !string.IsNullOrWhiteSpace(p))
                  .ToArray();

        var chunks = new List<string>();
        var buffer = new System.Text.StringBuilder();

        foreach (var part in parts)
        {
            if (buffer.Length + part.Length + sep.Length > size)
            {
                if (buffer.Length > 0)
                {
                    chunks.Add(buffer.ToString().Trim());
                    // Keep overlap
                    var bufStr = buffer.ToString();
                    buffer.Clear();
                    if (overlap > 0 && bufStr.Length > overlap)
                        buffer.Append(bufStr[^overlap..]);
                }
            }
            if (buffer.Length > 0) buffer.Append(sep);
            buffer.Append(part);
        }

        if (buffer.Length > 0)
            chunks.Add(buffer.ToString().Trim());

        // Recursively split any chunks still over size
        var result = new List<string>();
        foreach (var c in chunks)
        {
            if (c.Length > size && separators.Length > 1)
                result.AddRange(RecursiveSplit(c, size, overlap, separators[1..]));
            else
                result.Add(c);
        }

        return result;
    }

    private static string[] SplitByChar(string text, int size)
    {
        var parts = new List<string>();
        for (int i = 0; i < text.Length; i += size)
            parts.Add(text.Substring(i, Math.Min(size, text.Length - i)));
        return [.. parts];
    }
}
