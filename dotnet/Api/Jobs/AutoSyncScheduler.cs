using Npgsql;

namespace SetYazilim.Llm.Api.Jobs;

/// <summary>
/// Periodically scans sql_connections and enqueues sql.sync-data jobs
/// for connections that have auto_sync_interval_min > 0 and are due.
/// </summary>
public sealed class AutoSyncScheduler : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AutoSyncScheduler> _log;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    public AutoSyncScheduler(IServiceProvider services, ILogger<AutoSyncScheduler> log)
    {
        _services = services;
        _log      = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        _log.LogInformation("AutoSyncScheduler started — polling every {Min} min", PollInterval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Tick(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "AutoSyncScheduler tick failed"); }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task Tick(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var ds    = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var queue = scope.ServiceProvider.GetRequiredService<IJobService>();

        await using var conn = await ds.OpenConnectionAsync(ct);
        // Find all connections due for sync.
        // Due if: interval > 0 AND (last_auto_sync_at IS NULL OR NOW - last_auto_sync_at >= interval)
        await using var find = conn.CreateCommand();
        find.CommandText = @"SELECT id, name, COALESCE(auto_sync_interval_min, 0), last_auto_sync_at
                             FROM sql_connections
                             WHERE COALESCE(auto_sync_interval_min, 0) > 0
                               AND (last_auto_sync_at IS NULL
                                    OR (NOW() - last_auto_sync_at) >= make_interval(mins => auto_sync_interval_min))";
        var due = new List<(int id, string name, int intervalMin)>();
        await using (var r = await find.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                due.Add((r.GetInt32(0), r.GetString(1), r.GetInt32(2)));
        }

        if (due.Count == 0) return;

        foreach (var (id, name, interval) in due)
        {
            try
            {
                // Skip if there's already an active sync-data job for this connection
                await using var check = conn.CreateCommand();
                check.CommandText = @"SELECT COUNT(*) FROM jobs
                                      WHERE job_type='sql.sync-data'
                                        AND status IN ('queued','running')
                                        AND params LIKE $1";
                check.Parameters.AddWithValue($"%\"ConnectionId\":{id}%");
                var active = Convert.ToInt64(await check.ExecuteScalarAsync(ct));
                if (active > 0)
                {
                    _log.LogDebug("AutoSync skipped {Name} (id={Id}) — active job exists", name, id);
                    continue;
                }

                var jobId = await queue.EnqueueAsync(
                    "sql.sync-data",
                    new { ConnectionId = id, TableConfigIds = (List<int>?)null },
                    "auto-sync",
                    ct);

                await using var upd = conn.CreateCommand();
                upd.CommandText = "UPDATE sql_connections SET last_auto_sync_at=NOW() WHERE id=$1";
                upd.Parameters.AddWithValue(id);
                await upd.ExecuteNonQueryAsync(ct);

                _log.LogInformation("AutoSync enqueued job #{JobId} for {Name} (id={Id}, every {Min}min)",
                    jobId, name, id, interval);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "AutoSync failed to enqueue for connection {Name} (id={Id})", name, id);
            }
        }
    }
}
