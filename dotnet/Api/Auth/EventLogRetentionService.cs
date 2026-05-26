using Npgsql;

namespace SetYazilim.Llm.Api.Auth;

/// <summary>
/// Removes event_log rows older than <c>EventLog:RetentionDays</c> (default 90).
/// Runs at startup (after a short delay) and every 24 hours afterward.
/// </summary>
public sealed class EventLogRetentionService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<EventLogRetentionService> _log;
    private readonly int _retentionDays;
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval     = TimeSpan.FromHours(24);

    public EventLogRetentionService(IServiceProvider services,
        IConfiguration cfg, ILogger<EventLogRetentionService> log)
    {
        _services = services;
        _log      = log;
        _retentionDays = Math.Clamp(cfg.GetValue<int?>("EventLog:RetentionDays") ?? 90, 7, 3650);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _log.LogInformation("EventLogRetentionService started — keeping {Days} days", _retentionDays);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Tick(stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "EventLog retention tick failed"); }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task Tick(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var ds = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM event_log WHERE ts < NOW() - INTERVAL '{_retentionDays} days'";
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows > 0)
            _log.LogInformation("EventLog retention: deleted {N} rows older than {Days} days",
                rows, _retentionDays);
        else
            _log.LogDebug("EventLog retention: no rows to delete");
    }
}
