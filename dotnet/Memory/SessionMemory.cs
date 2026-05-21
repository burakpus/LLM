using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace SetYazilim.Llm.Memory;

// ── Models ────────────────────────────────────────────────────────────────────

public sealed record MemoryMessage(
    string Role,
    string Content,
    int    TokenCount = 0,
    string Metadata   = "{}");

public sealed class SessionMemoryOptions
{
    public const string SectionName = "SessionMemory";

    /// <summary>Hours a session lives without activity (default 24). Config: SessionTtlHours.</summary>
    public int SessionTtlHours { get; init; } = 24;

    public TimeSpan SessionTtl => TimeSpan.FromHours(SessionTtlHours);

    /// <summary>Max tokens to keep in rolling window before summarising.</summary>
    public int MaxTokenWindow        { get; init; } = 6000;

    /// <summary>How many recent messages to always keep (never summarised).</summary>
    public int RecentMessageCount    { get; init; } = 8;
}

// ── Interface ─────────────────────────────────────────────────────────────────

public interface ISessionMemory
{
    Task AppendAsync(string sessionId, string userId, string agentId,
                     MemoryMessage message, CancellationToken ct = default);

    Task<IReadOnlyList<MemoryMessage>> GetWindowAsync(
        string sessionId, string userId,
        int maxTokens = 4000, CancellationToken ct = default);

    Task<int> CountAsync(string sessionId, CancellationToken ct = default);

    Task ClearAsync(string sessionId, CancellationToken ct = default);

    Task<int> CleanupExpiredAsync(CancellationToken ct = default);
}

// ── Implementation ────────────────────────────────────────────────────────────

public sealed class PgSessionMemory : ISessionMemory
{
    private readonly NpgsqlDataSource _ds;
    private readonly SessionMemoryOptions _opts;
    private readonly ILogger<PgSessionMemory> _log;

    public PgSessionMemory(
        NpgsqlDataSource ds,
        IOptions<SessionMemoryOptions> opts,
        ILogger<PgSessionMemory> log)
    {
        _ds   = ds;
        _opts = opts.Value;
        _log  = log;
    }

    public async Task AppendAsync(
        string sessionId, string userId, string agentId,
        MemoryMessage message, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();

        cmd.CommandText = """
            INSERT INTO session_memories
                (session_id, user_id, agent_id, role, content, token_count, metadata, expires_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, now() + $8);
            """;

        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(agentId);
        cmd.Parameters.AddWithValue(message.Role);
        cmd.Parameters.AddWithValue(message.Content);
        cmd.Parameters.AddWithValue(message.TokenCount);
        cmd.Parameters.AddWithValue(message.Metadata);
        cmd.Parameters.AddWithValue(_opts.SessionTtl);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryMessage>> GetWindowAsync(
        string sessionId, string userId,
        int maxTokens = 4000, CancellationToken ct = default)
    {
        // Set RLS context
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await SetRlsUserAsync(conn, userId, ct);

        await using var cmd = conn.CreateCommand();
        // Grab last N messages, newest first, then reverse for chronological order
        cmd.CommandText = """
            SELECT role, content, token_count, metadata
            FROM   session_memories
            WHERE  session_id = $1
              AND  expires_at > now()
            ORDER  BY created_at DESC
            LIMIT  100;
            """;
        cmd.Parameters.AddWithValue(sessionId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<(string role, string content, int tokens, string meta)>();

        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetString(0), reader.GetString(1),
                      reader.GetInt32(2), reader.GetString(3)));

        reader.Close();

        // Sliding window: always keep RecentMessageCount, then fill back with tokens
        var recent  = rows.Take(_opts.RecentMessageCount).ToList();
        var older   = rows.Skip(_opts.RecentMessageCount).ToList();
        int used    = recent.Sum(r => r.tokens);
        var window  = new List<(string, string, int, string)>(recent);

        foreach (var row in older)
        {
            if (used + row.tokens > maxTokens) break;
            window.Add(row);
            used += row.tokens;
        }

        // Restore chronological order
        window.Reverse();

        return window
            .Select(r => new MemoryMessage(r.Item1, r.Item2, r.Item3, r.Item4))
            .ToList();
    }

    public async Task<int> CountAsync(string sessionId, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM session_memories WHERE session_id = $1 AND expires_at > now();";
        cmd.Parameters.AddWithValue(sessionId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task ClearAsync(string sessionId, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM session_memories WHERE session_id = $1;";
        cmd.Parameters.AddWithValue(sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CleanupExpiredAsync(CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT cleanup_expired_sessions();";
        var result = await cmd.ExecuteScalarAsync(ct);
        var deleted = Convert.ToInt32(result);
        _log.LogInformation("Cleaned up {Count} expired session rows", deleted);
        return deleted;
    }

    private static async Task SetRlsUserAsync(NpgsqlConnection conn, string userId, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT set_config('app.current_user', $1, true);";
        cmd.Parameters.AddWithValue(userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
