using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;
using SetYazilim.Llm.VectorStore;

namespace SetYazilim.Llm.Memory;

// ── Models ────────────────────────────────────────────────────────────────────

public sealed class AgentMemoryEntry
{
    public Guid     Id         { get; init; } = Guid.NewGuid();
    public string   AgentId    { get; init; } = string.Empty;
    public string   SkillName  { get; init; } = string.Empty;
    public string   UserId     { get; init; } = string.Empty;
    public string   Content    { get; init; } = string.Empty;
    public float[]? Embedding  { get; set; }
    public int      Importance { get; init; } = 5;  // 1-10
    public string   Metadata   { get; init; } = "{}";
    public DateTime CreatedAt  { get; init; } = DateTime.UtcNow;
}

public sealed class AgentMemorySearchResult
{
    public AgentMemoryEntry Entry    { get; init; } = null!;
    public double           Distance { get; init; }
    public double           Similarity => 1.0 - Distance;
}

// ── Interface ─────────────────────────────────────────────────────────────────

public interface IAgentMemory
{
    Task StoreAsync(AgentMemoryEntry entry, CancellationToken ct = default);

    Task<IReadOnlyList<AgentMemorySearchResult>> SearchAsync(
        string   agentId,
        string   skillName,
        string   userId,
        float[]  queryEmbedding,
        int      topK        = 5,
        double   maxDistance = 0.5,
        CancellationToken ct = default);

    Task<IReadOnlyList<AgentMemoryEntry>> GetRecentAsync(
        string agentId, string skillName, string userId,
        int limit = 10, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}

// ── Implementation ────────────────────────────────────────────────────────────

public sealed class PgAgentMemory : IAgentMemory
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<PgAgentMemory> _log;

    public PgAgentMemory(NpgsqlDataSource ds, ILogger<PgAgentMemory> log)
    {
        _ds  = ds;
        _log = log;
    }

    public async Task StoreAsync(AgentMemoryEntry entry, CancellationToken ct = default)
    {
        if (entry.Embedding is null || entry.Embedding.Length == 0)
            throw new InvalidOperationException("AgentMemoryEntry must have an embedding before storing.");

        await using var conn = await _ds.OpenConnectionAsync(ct);
        await SetRlsUserAsync(conn, entry.UserId, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agent_memories
                (id, agent_id, skill_name, user_id, content, embedding, importance, metadata)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb)
            ON CONFLICT (id) DO UPDATE
                SET content    = EXCLUDED.content,
                    embedding  = EXCLUDED.embedding,
                    importance = EXCLUDED.importance,
                    metadata   = EXCLUDED.metadata,
                    updated_at = now();
            """;
        cmd.Parameters.AddWithValue(entry.Id);
        cmd.Parameters.AddWithValue(entry.AgentId);
        cmd.Parameters.AddWithValue(entry.SkillName);
        cmd.Parameters.AddWithValue(entry.UserId);
        cmd.Parameters.AddWithValue(entry.Content);
        cmd.Parameters.AddWithValue(new Vector(entry.Embedding));
        cmd.Parameters.AddWithValue(entry.Importance);
        cmd.Parameters.AddWithValue(entry.Metadata);

        await cmd.ExecuteNonQueryAsync(ct);
        _log.LogDebug("Stored agent memory {Id} for {Agent}/{Skill}", entry.Id, entry.AgentId, entry.SkillName);
    }

    public async Task<IReadOnlyList<AgentMemorySearchResult>> SearchAsync(
        string  agentId, string skillName, string userId,
        float[] queryEmbedding,
        int     topK = 5, double maxDistance = 0.5,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await SetRlsUserAsync(conn, userId, ct);

        await using var setEf = conn.CreateCommand();
        setEf.CommandText = "SET hnsw.ef_search = 40;";
        await setEf.ExecuteNonQueryAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, agent_id, skill_name, user_id, content, importance, metadata, created_at,
                   embedding <=> $1 AS distance
            FROM   agent_memories
            WHERE  agent_id   = $2
              AND  skill_name = $3
              AND  (embedding <=> $1) <= $4
            ORDER  BY (importance::float / 10.0) * (1.0 - (embedding <=> $1) / 2.0) DESC
            LIMIT  $5;
            """;
        cmd.Parameters.AddWithValue(new Vector(queryEmbedding));
        cmd.Parameters.AddWithValue(agentId);
        cmd.Parameters.AddWithValue(skillName);
        cmd.Parameters.AddWithValue(maxDistance);
        cmd.Parameters.AddWithValue(topK);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        var results = new List<AgentMemorySearchResult>();

        while (await r.ReadAsync(ct))
        {
            results.Add(new AgentMemorySearchResult
            {
                Entry = new AgentMemoryEntry
                {
                    Id         = r.GetGuid(0),
                    AgentId    = r.GetString(1),
                    SkillName  = r.GetString(2),
                    UserId     = r.GetString(3),
                    Content    = r.GetString(4),
                    Importance = r.GetInt32(5),
                    Metadata   = r.GetString(6),
                    CreatedAt  = r.GetDateTime(7)
                },
                Distance = r.GetDouble(8)
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<AgentMemoryEntry>> GetRecentAsync(
        string agentId, string skillName, string userId,
        int limit = 10, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await SetRlsUserAsync(conn, userId, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, agent_id, skill_name, user_id, content, importance, metadata, created_at
            FROM   agent_memories
            WHERE  agent_id   = $1
              AND  skill_name = $2
            ORDER  BY importance DESC, created_at DESC
            LIMIT  $3;
            """;
        cmd.Parameters.AddWithValue(agentId);
        cmd.Parameters.AddWithValue(skillName);
        cmd.Parameters.AddWithValue(limit);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        var results = new List<AgentMemoryEntry>();

        while (await r.ReadAsync(ct))
            results.Add(new AgentMemoryEntry
            {
                Id         = r.GetGuid(0),
                AgentId    = r.GetString(1),
                SkillName  = r.GetString(2),
                UserId     = r.GetString(3),
                Content    = r.GetString(4),
                Importance = r.GetInt32(5),
                Metadata   = r.GetString(6),
                CreatedAt  = r.GetDateTime(7)
            });

        return results;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agent_memories WHERE id = $1;";
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task SetRlsUserAsync(NpgsqlConnection conn, string userId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_user', $1, true);";
        cmd.Parameters.AddWithValue(userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
