using System.Text.Json;

namespace SetYazilim.Llm.Api.Jobs;

public interface IJobHandler
{
    /// <summary>Job type identifier — e.g. "sql.ingest-schema"</summary>
    string Type { get; }

    /// <summary>Executes the job. Use ctx.ReportProgress() to update UI.</summary>
    Task<object> RunAsync(JobContext ctx, CancellationToken ct);
}

public sealed class JobContext
{
    public long              JobId       { get; init; }
    public required string   ParamsJson  { get; init; }
    public required IServiceProvider Services { get; init; }
    public required IJobService Queue { get; init; }

    public T ParseParams<T>() => JsonSerializer.Deserialize<T>(ParamsJson)
        ?? throw new InvalidOperationException("Invalid job params JSON");

    public Task ReportProgressAsync(int cur, int tot, string message = "", CancellationToken ct = default)
        => Queue.UpdateProgressAsync(JobId, cur, tot, message, ct);
}

public sealed class JobWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<JobWorker> _log;
    private readonly Dictionary<string, IJobHandler> _handlers;

    public JobWorker(IServiceProvider services, IEnumerable<IJobHandler> handlers, ILogger<JobWorker> log)
    {
        _services = services;
        _log      = log;
        _handlers = handlers.ToDictionary(h => h.Type, h => h);
        _log.LogInformation("JobWorker initialized with {Count} handlers: {Types}",
            _handlers.Count, string.Join(", ", _handlers.Keys));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(2000, stoppingToken);  // wait for DB ready

        // Recovery: any job left in 'running' state from a previous crash → re-queue
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var ds = scope.ServiceProvider.GetRequiredService<Npgsql.NpgsqlDataSource>();
            await using var conn = await ds.OpenConnectionAsync(stoppingToken);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = @"UPDATE jobs SET status='queued', started_at=NULL,
                                                 message='Sunucu yeniden başladı — kuyruğa geri alındı'
                                WHERE status='running' RETURNING id";
            await using var r = await cmd.ExecuteReaderAsync(stoppingToken);
            var recovered = new List<long>();
            while (await r.ReadAsync(stoppingToken)) recovered.Add(r.GetInt64(0));
            if (recovered.Count > 0)
                _log.LogWarning("Recovered {N} stranded running jobs: {Ids}",
                    recovered.Count, string.Join(", ", recovered));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to recover stranded jobs on startup");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _services.CreateAsyncScope();
                var queue = scope.ServiceProvider.GetRequiredService<IJobService>();
                var claimed = await queue.ClaimNextAsync(stoppingToken);

                if (claimed is null)
                {
                    await Task.Delay(2000, stoppingToken);
                    continue;
                }

                var (jobId, type, paramsJson) = claimed.Value;
                _log.LogInformation("Running job #{Id} type={Type}", jobId, type);

                if (!_handlers.TryGetValue(type, out var handler))
                {
                    await queue.FailAsync(jobId, $"No handler registered for type: {type}", stoppingToken);
                    continue;
                }

                try
                {
                    var ctx = new JobContext
                    {
                        JobId      = jobId,
                        ParamsJson = paramsJson,
                        Services   = scope.ServiceProvider,
                        Queue      = queue,
                    };
                    var result = await handler.RunAsync(ctx, stoppingToken);
                    await queue.CompleteAsync(jobId, result, stoppingToken);
                    _log.LogInformation("Job #{Id} completed", jobId);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Job #{Id} failed", jobId);
                    await queue.FailAsync(jobId, ex.Message, CancellationToken.None);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "JobWorker loop error — sleeping 5s");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }
}
