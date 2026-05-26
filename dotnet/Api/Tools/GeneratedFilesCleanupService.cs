namespace SetYazilim.Llm.Api.Tools;

/// <summary>
/// Periodically removes generated files older than <c>Tools:GeneratedTtlHours</c>
/// (default 24h) from the FileGenerator output directory. Also removes empty
/// per-user token directories to keep the tree tidy.
/// </summary>
public sealed class GeneratedFilesCleanupService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<GeneratedFilesCleanupService> _log;
    private readonly int _ttlHours;
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Interval     = TimeSpan.FromHours(1);

    public GeneratedFilesCleanupService(IServiceProvider services,
        IConfiguration cfg, ILogger<GeneratedFilesCleanupService> log)
    {
        _services = services;
        _log      = log;
        _ttlHours = Math.Clamp(cfg.GetValue<int?>("Tools:GeneratedTtlHours") ?? 24, 1, 720);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        _log.LogInformation("GeneratedFilesCleanupService started — TTL {Hours}h", _ttlHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Run(Tick, stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "Generated files cleanup tick failed"); }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        using var scope = _services.CreateScope();
        var gen = scope.ServiceProvider.GetRequiredService<IFileGenerator>();
        var root = gen.GeneratedRoot;
        if (!Directory.Exists(root)) return;

        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(_ttlHours);
        var deletedFiles = 0;
        var deletedDirs  = 0;

        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) < cutoff)
                    {
                        File.Delete(f);
                        deletedFiles++;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug("Failed to delete file {File}: {Msg}", f, ex.Message);
                }
            }

            // Remove now-empty directories (depth-first)
            foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                                       .OrderByDescending(p => p.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(d).Any())
                    {
                        Directory.Delete(d);
                        deletedDirs++;
                    }
                }
                catch { /* race — ignore */ }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Generated cleanup walk failed: {Msg}", ex.Message);
        }

        if (deletedFiles > 0 || deletedDirs > 0)
            _log.LogInformation("Generated cleanup: removed {F} file(s) and {D} dir(s) older than {H}h",
                deletedFiles, deletedDirs, _ttlHours);
    }
}
