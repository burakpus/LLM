using System.Text.Json;
using Npgsql;

namespace SetYazilim.Llm.Api.Jobs;

public enum JobStatus { Queued, Running, Completed, Failed, Cancelled }

public sealed record JobInfo(
    long       Id,
    string     Type,
    JobStatus  Status,
    int        ProgressCur,
    int        ProgressTot,
    string     Message,
    string     CreatedBy,
    DateTime   CreatedAt,
    DateTime?  StartedAt,
    DateTime?  CompletedAt,
    string     Error,
    string     ResultJson);

public interface IJobService
{
    /// <summary>Enqueue a job and return its ID.</summary>
    Task<long> EnqueueAsync(string type, object parameters, string user, CancellationToken ct);

    /// <summary>Atomically claim the next queued job (used by worker).</summary>
    Task<(long Id, string Type, string ParamsJson)?> ClaimNextAsync(CancellationToken ct);

    Task UpdateProgressAsync(long jobId, int cur, int tot, string message, CancellationToken ct);
    Task CompleteAsync(long jobId, object result, CancellationToken ct);
    Task FailAsync(long jobId, string error, CancellationToken ct);

    Task<JobInfo?> GetAsync(long jobId, CancellationToken ct);
    Task<List<JobInfo>> ListRecentAsync(int limit, string? statusFilter, CancellationToken ct);

    /// <summary>Cancel a queued job. Returns false if job is already running/done.</summary>
    Task<bool> CancelAsync(long jobId, CancellationToken ct);

    /// <summary>Re-enqueue a failed/cancelled job with original params. Returns new job ID, or null if source missing/ineligible.</summary>
    Task<long?> RetryAsync(long jobId, string user, CancellationToken ct);

    /// <summary>List with type + status filter and offset (for Jobs admin tab).</summary>
    Task<(List<JobInfo> items, long total)> ListFilteredAsync(int limit, int offset, string? typeFilter, string? statusFilter, CancellationToken ct);
}

public sealed class JobService : IJobService
{
    private readonly NpgsqlDataSource _ds;
    public JobService(NpgsqlDataSource ds) { _ds = ds; }

    static string Status2Str(JobStatus s) => s.ToString().ToLowerInvariant();
    static JobStatus Str2Status(string s) => s.ToLowerInvariant() switch
    {
        "running"    => JobStatus.Running,
        "completed"  => JobStatus.Completed,
        "failed"     => JobStatus.Failed,
        "cancelled"  => JobStatus.Cancelled,
        _            => JobStatus.Queued,
    };

    public async Task<long> EnqueueAsync(string type, object parameters, string user, CancellationToken ct)
    {
        var paramsJson = JsonSerializer.Serialize(parameters);
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO jobs (job_type, status, params, created_by)
                            VALUES ($1, 'queued', $2, $3) RETURNING id";
        cmd.Parameters.AddWithValue(type);
        cmd.Parameters.AddWithValue(paramsJson);
        cmd.Parameters.AddWithValue(user);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<(long Id, string Type, string ParamsJson)?> ClaimNextAsync(CancellationToken ct)
    {
        // Use SKIP LOCKED to safely claim next queued job in case of multiple workers
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE jobs SET status='running', started_at=NOW()
            WHERE id = (
                SELECT id FROM jobs
                WHERE status='queued'
                ORDER BY id
                LIMIT 1 FOR UPDATE SKIP LOCKED
            )
            RETURNING id, job_type, params";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetInt64(0), r.GetString(1), r.GetString(2));
    }

    public async Task UpdateProgressAsync(long jobId, int cur, int tot, string message, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE jobs SET progress_cur=$1, progress_tot=$2, message=$3 WHERE id=$4";
        cmd.Parameters.AddWithValue(cur);
        cmd.Parameters.AddWithValue(tot);
        cmd.Parameters.AddWithValue(message ?? "");
        cmd.Parameters.AddWithValue(jobId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteAsync(long jobId, object result, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(result);
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"UPDATE jobs
                            SET status='completed', result=$1, completed_at=NOW(),
                                progress_cur=progress_tot
                            WHERE id=$2";
        cmd.Parameters.AddWithValue(json);
        cmd.Parameters.AddWithValue(jobId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task FailAsync(long jobId, string error, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "UPDATE jobs SET status='failed', error=$1, completed_at=NOW() WHERE id=$2";
        cmd.Parameters.AddWithValue(error.Length > 4000 ? error[..4000] : error);
        cmd.Parameters.AddWithValue(jobId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<JobInfo?> GetAsync(long jobId, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, job_type, status, progress_cur, progress_tot, message,
                                   created_by, created_at, started_at, completed_at, error, result
                            FROM jobs WHERE id=$1";
        cmd.Parameters.AddWithValue(jobId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return Read(r);
    }

    public async Task<List<JobInfo>> ListRecentAsync(int limit, string? statusFilter, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 200);
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = string.IsNullOrEmpty(statusFilter)
            ? @"SELECT id, job_type, status, progress_cur, progress_tot, message,
                       created_by, created_at, started_at, completed_at, error, result
                FROM jobs ORDER BY id DESC LIMIT $1"
            : @"SELECT id, job_type, status, progress_cur, progress_tot, message,
                       created_by, created_at, started_at, completed_at, error, result
                FROM jobs WHERE status=$2 ORDER BY id DESC LIMIT $1";
        cmd.Parameters.AddWithValue(limit);
        if (!string.IsNullOrEmpty(statusFilter)) cmd.Parameters.AddWithValue(statusFilter);

        var result = new List<JobInfo>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(Read(r));
        return result;
    }

    public async Task<bool> CancelAsync(long jobId, CancellationToken ct)
    {
        // Only safely cancel jobs that haven't started yet — running jobs would need cooperative cancellation
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"UPDATE jobs
                            SET status='cancelled', completed_at=NOW()
                            WHERE id=$1 AND status='queued'";
        cmd.Parameters.AddWithValue(jobId);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    public async Task<long?> RetryAsync(long jobId, string user, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);

        // Read original job
        await using var get = conn.CreateCommand();
        get.CommandText = "SELECT job_type, params, status FROM jobs WHERE id=$1";
        get.Parameters.AddWithValue(jobId);
        string? jobType = null, paramsJson = null, status = null;
        await using (var r = await get.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            jobType    = r.GetString(0);
            paramsJson = r.GetString(1);
            status     = r.GetString(2);
        }
        if (status is not ("failed" or "cancelled")) return null;

        // Enqueue fresh
        await using var ins = conn.CreateCommand();
        ins.CommandText = @"INSERT INTO jobs (job_type, status, params, created_by)
                            VALUES ($1, 'queued', $2, $3) RETURNING id";
        ins.Parameters.AddWithValue(jobType!);
        ins.Parameters.AddWithValue(paramsJson!);
        ins.Parameters.AddWithValue(user);
        return Convert.ToInt64(await ins.ExecuteScalarAsync(ct));
    }

    public async Task<(List<JobInfo> items, long total)> ListFilteredAsync(int limit, int offset, string? typeFilter, string? statusFilter, CancellationToken ct)
    {
        limit  = Math.Clamp(limit, 1, 500);
        offset = Math.Max(0, offset);

        var clauses = new List<string>();
        var args    = new List<object>();
        if (!string.IsNullOrEmpty(typeFilter))   { args.Add(typeFilter);   clauses.Add($"job_type=${args.Count}"); }
        if (!string.IsNullOrEmpty(statusFilter)) { args.Add(statusFilter); clauses.Add($"status=${args.Count}"); }
        var where = clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses);

        await using var conn = await _ds.OpenConnectionAsync(ct);

        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM jobs" + where;
        foreach (var a in args) countCmd.Parameters.AddWithValue(a);
        var total = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, job_type, status, progress_cur, progress_tot, message,
                                   created_by, created_at, started_at, completed_at, error, result
                            FROM jobs" + where +
                          $" ORDER BY id DESC LIMIT ${args.Count + 1} OFFSET ${args.Count + 2}";
        foreach (var a in args) cmd.Parameters.AddWithValue(a);
        cmd.Parameters.AddWithValue(limit);
        cmd.Parameters.AddWithValue(offset);

        var items = new List<JobInfo>();
        await using var rr = await cmd.ExecuteReaderAsync(ct);
        while (await rr.ReadAsync(ct)) items.Add(Read(rr));
        return (items, total);
    }

    static JobInfo Read(System.Data.Common.DbDataReader r) => new(
        Id:          r.GetInt64(0),
        Type:        r.GetString(1),
        Status:      Str2Status(r.GetString(2)),
        ProgressCur: r.GetInt32(3),
        ProgressTot: r.GetInt32(4),
        Message:     r.GetString(5),
        CreatedBy:   r.GetString(6),
        CreatedAt:   r.GetDateTime(7),
        StartedAt:   r.IsDBNull(8)  ? null : r.GetDateTime(8),
        CompletedAt: r.IsDBNull(9)  ? null : r.GetDateTime(9),
        Error:       r.GetString(10),
        ResultJson:  r.GetString(11));
}
