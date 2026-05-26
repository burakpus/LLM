using Npgsql;
using SetYazilim.Llm.Api.Jobs;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// Shared helpers used by multiple endpoint files. Pulled out of Program.cs
/// so MapSql/MapJobs/MapAdminTemplates etc. can call them without keeping the
/// local-function pattern.
/// </summary>
public static class ActivityLogger
{
    /// <summary>
    /// Fire-and-forget activity insert (non-blocking, swallows errors).
    ///
    /// Dual-writes to:
    ///   1. activity_log (eski — Admin → Aktivite sekmesi okur)
    ///   2. event_log    (yeni — OWASP-aligned, Admin → 🛡 Güvenlik sekmesi okur)
    ///
    /// event_log için HTTP context yoktur (IP/UA/request_id NULL);
    /// zenginleştirilmiş kayıt isteniyorsa direkt IEventLog.LogAsync kullanılmalı.
    /// </summary>
    public static async Task LogAsync(NpgsqlDataSource ds, string username, string action, string target, string details = "")
    {
        // (1) activity_log — geriye uyum
        try
        {
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO activity_log (username, action, target, details) VALUES ($1,$2,$3,$4)";
            cmd.Parameters.AddWithValue(username);
            cmd.Parameters.AddWithValue(action);
            cmd.Parameters.AddWithValue(target);
            cmd.Parameters.AddWithValue(details);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* non-critical */ }

        // (2) event_log — yeni OWASP sistemi
        try
        {
            var (category, severity) = MapActionToEvent(action);
            await using var conn = await ds.OpenConnectionAsync();
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO event_log
                (category, severity, event_type, username, result, action, resource, reason, ts)
                VALUES ($1,$2,$3,$4,'Success',$5,$6,$7,NOW())";
            cmd.Parameters.AddWithValue(category);
            cmd.Parameters.AddWithValue(severity);
            cmd.Parameters.AddWithValue(action);
            cmd.Parameters.AddWithValue(username);
            cmd.Parameters.AddWithValue((object?)action ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)target ?? DBNull.Value);
            cmd.Parameters.AddWithValue(string.IsNullOrEmpty(details) ? DBNull.Value : details);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* non-critical */ }
    }

    /// <summary>Map LogActivity action string → (EventCategory, EventSeverity).</summary>
    public static (string Category, string Severity) MapActionToEvent(string action)
    {
        var a = (action ?? "").ToLowerInvariant();
        if (a.StartsWith("auth."))      return ("Auth",    "Info");
        if (a.StartsWith("session."))   return ("Session", "Info");
        if (a.StartsWith("job."))       return ("System",  "Info");
        if (a.StartsWith("config.") ||
            a.Contains(".connection.create") ||
            a.Contains(".connection.update") ||
            a.Contains(".connection.delete"))
                                        return ("Config",  "Info");
        // sql/skill/document/template/etc. — veri operasyonu
        return ("Data", "Info");
    }

    /// <summary>Shared serializer for JobInfo → API DTO (used by MapJobs and MapSql).</summary>
    public static object SerializeJob(JobInfo j) => new {
        id          = j.Id,
        type        = j.Type,
        status      = j.Status.ToString().ToLowerInvariant(),
        progressCur = j.ProgressCur,
        progressTot = j.ProgressTot,
        message     = j.Message,
        createdBy   = j.CreatedBy,
        createdAt   = j.CreatedAt,
        startedAt   = j.StartedAt,
        completedAt = j.CompletedAt,
        error       = j.Error,
        result      = string.IsNullOrEmpty(j.ResultJson) ? null
                      : System.Text.Json.JsonSerializer.Deserialize<object>(j.ResultJson),
    };
}
