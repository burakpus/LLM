using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace SetYazilim.Llm.Api.Tools;

public sealed record BenchmarkRequest(
    string Model,
    int    Concurrency,
    string Prompt,
    int    MaxTokens = 150,
    double Temperature = 0.4,
    string? Label = null);

public sealed record BenchmarkResult(
    long    Id,
    DateTime Ts,
    string  Model,
    int     Concurrency,
    int     MaxTokens,
    int     Success,
    int     Failed,
    double  WallSeconds,
    double  TtftP50Ms,
    double  TtftP95Ms,
    double  TpsPerStreamP50,
    double  TpsPerStreamP95,
    double  TpsAggregate,
    int     TotalTokens,
    string? Label,
    string  CreatedBy);

public interface IBenchmarkService
{
    Task<BenchmarkResult> RunAsync(BenchmarkRequest req, string callerJwt, string createdBy, CancellationToken ct);
    Task<List<BenchmarkResult>> ListAsync(string? model, int limit, CancellationToken ct);
}

public sealed class BenchmarkService : IBenchmarkService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration     _cfg;
    private readonly NpgsqlDataSource   _ds;
    private readonly ILogger<BenchmarkService> _log;

    public BenchmarkService(IHttpClientFactory http, IConfiguration cfg,
        NpgsqlDataSource ds, ILogger<BenchmarkService> log)
    {
        _httpFactory = http; _cfg = cfg; _ds = ds; _log = log;
    }

    public async Task<BenchmarkResult> RunAsync(BenchmarkRequest req, string callerJwt,
        string createdBy, CancellationToken ct)
    {
        var n = Math.Clamp(req.Concurrency, 1, 200);
        var swWall = Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, n)
            .Select(i => RunOneAsync(i, req, callerJwt, ct))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        swWall.Stop();
        var wall = swWall.Elapsed.TotalSeconds;

        var ok      = results.Where(r => r.Ok).ToArray();
        var failed  = results.Length - ok.Length;
        var ttfts   = ok.Where(r => r.TtftMs.HasValue).Select(r => r.TtftMs!.Value)
                        .OrderBy(x => x).ToArray();
        var tpss    = ok.Where(r => r.TokensPerSec > 0).Select(r => r.TokensPerSec)
                        .OrderBy(x => x).ToArray();
        var totalTokens = ok.Sum(r => r.Tokens);
        var tpsAgg  = wall > 0 ? totalTokens / wall : 0;

        var saved = await SaveAsync(req, results.Length, ok.Length, failed, wall,
            P50(ttfts), P95(ttfts), P50(tpss), P95(tpss), tpsAgg, totalTokens, createdBy, ct);

        return saved;
    }

    public async Task<List<BenchmarkResult>> ListAsync(string? model, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 200);
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        if (string.IsNullOrEmpty(model))
        {
            cmd.CommandText = @"SELECT id, ts, model, concurrency, max_tokens, success_count, fail_count,
                                       wall_seconds, ttft_p50_ms, ttft_p95_ms,
                                       tps_per_stream_p50, tps_per_stream_p95, tps_aggregate,
                                       total_tokens, label, created_by
                                FROM benchmark_results ORDER BY ts DESC LIMIT $1";
            cmd.Parameters.AddWithValue(limit);
        }
        else
        {
            cmd.CommandText = @"SELECT id, ts, model, concurrency, max_tokens, success_count, fail_count,
                                       wall_seconds, ttft_p50_ms, ttft_p95_ms,
                                       tps_per_stream_p50, tps_per_stream_p95, tps_aggregate,
                                       total_tokens, label, created_by
                                FROM benchmark_results WHERE model=$1 ORDER BY ts DESC LIMIT $2";
            cmd.Parameters.AddWithValue(model);
            cmd.Parameters.AddWithValue(limit);
        }

        var list = new List<BenchmarkResult>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new BenchmarkResult(
                Id:              r.GetInt64(0),
                Ts:              r.GetDateTime(1),
                Model:           r.GetString(2),
                Concurrency:     r.GetInt32(3),
                MaxTokens:       r.GetInt32(4),
                Success:         r.GetInt32(5),
                Failed:          r.GetInt32(6),
                WallSeconds:     r.GetDouble(7),
                TtftP50Ms:       r.GetDouble(8),
                TtftP95Ms:       r.GetDouble(9),
                TpsPerStreamP50: r.GetDouble(10),
                TpsPerStreamP95: r.GetDouble(11),
                TpsAggregate:    r.GetDouble(12),
                TotalTokens:     r.GetInt32(13),
                Label:           r.IsDBNull(14) ? null : r.GetString(14),
                CreatedBy:       r.GetString(15)));
        }
        return list;
    }

    private async Task<BenchmarkResult> SaveAsync(BenchmarkRequest req,
        int total, int success, int failed, double wall,
        double ttftP50, double ttftP95, double tpsP50, double tpsP95,
        double tpsAgg, int totalTokens, string createdBy, CancellationToken ct)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO benchmark_results
            (model, concurrency, max_tokens, success_count, fail_count, wall_seconds,
             ttft_p50_ms, ttft_p95_ms, tps_per_stream_p50, tps_per_stream_p95, tps_aggregate,
             total_tokens, label, created_by)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14)
            RETURNING id, ts";
        cmd.Parameters.AddWithValue(req.Model);
        cmd.Parameters.AddWithValue(req.Concurrency);
        cmd.Parameters.AddWithValue(req.MaxTokens);
        cmd.Parameters.AddWithValue(success);
        cmd.Parameters.AddWithValue(failed);
        cmd.Parameters.AddWithValue(wall);
        cmd.Parameters.AddWithValue(ttftP50);
        cmd.Parameters.AddWithValue(ttftP95);
        cmd.Parameters.AddWithValue(tpsP50);
        cmd.Parameters.AddWithValue(tpsP95);
        cmd.Parameters.AddWithValue(tpsAgg);
        cmd.Parameters.AddWithValue(totalTokens);
        cmd.Parameters.AddWithValue((object?)req.Label ?? DBNull.Value);
        cmd.Parameters.AddWithValue(createdBy);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new BenchmarkResult(
            Id: r.GetInt64(0), Ts: r.GetDateTime(1),
            Model: req.Model, Concurrency: req.Concurrency, MaxTokens: req.MaxTokens,
            Success: success, Failed: failed, WallSeconds: wall,
            TtftP50Ms: ttftP50, TtftP95Ms: ttftP95,
            TpsPerStreamP50: tpsP50, TpsPerStreamP95: tpsP95, TpsAggregate: tpsAgg,
            TotalTokens: totalTokens, Label: req.Label, CreatedBy: createdBy);
    }

    private sealed record StreamOutcome(bool Ok, double? TtftMs, double TotalMs,
        int Tokens, double TokensPerSec, string? Error);

    private async Task<StreamOutcome> RunOneAsync(int idx, BenchmarkRequest req,
        string callerJwt, CancellationToken ct)
    {
        var sw     = Stopwatch.StartNew();
        double? ttftMs = null;
        var tokens = 0;

        try
        {
            var http = _httpFactory.CreateClient("bench-internal");
            http.Timeout = TimeSpan.FromMinutes(2);
            var apiBase = _cfg["Tools:InternalApiBase"] ?? "http://localhost:5080";

            using var msg = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/api/llm/completions");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", callerJwt);

            var body = JsonSerializer.Serialize(new
            {
                messages = new[] { new { role = "user", content = $"#{idx} {req.Prompt}" } },
                model    = req.Model,
                stream   = true,
                temperature = req.Temperature,
                maxTokens   = req.MaxTokens,
            });
            msg.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return new(false, null, sw.Elapsed.TotalMilliseconds, 0, 0, $"HTTP {(int)resp.StatusCode}");

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using  var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var payload = line[5..].Trim();
                if (payload == "[DONE]") break;
                try
                {
                    using var doc = JsonDocument.Parse(payload);
                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() == 0) continue;
                    var delta = choices[0].GetProperty("delta");
                    if (delta.TryGetProperty("content", out var c))
                    {
                        var s = c.GetString();
                        if (!string.IsNullOrEmpty(s))
                        {
                            if (ttftMs == null) ttftMs = sw.Elapsed.TotalMilliseconds;
                            tokens++;
                        }
                    }
                } catch { /* skip non-JSON */ }
            }

            sw.Stop();
            var totalMs  = sw.Elapsed.TotalMilliseconds;
            var genTimeS = ttftMs.HasValue ? (totalMs - ttftMs.Value) / 1000.0 : 0;
            var tps      = genTimeS > 0 ? tokens / genTimeS : 0;

            return new(tokens > 0, ttftMs, totalMs, tokens, tps, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning("benchmark stream #{Idx} failed: {Msg}", idx, ex.Message);
            return new(false, null, sw.Elapsed.TotalMilliseconds, 0, 0, ex.Message);
        }
    }

    // p50 / p95 helpers — sorted input
    private static double P50(double[] sorted) =>
        sorted.Length == 0 ? 0 : sorted[Math.Max(0, (int)(sorted.Length * 0.5) - 1)];
    private static double P95(double[] sorted) =>
        sorted.Length == 0 ? 0 : sorted[Math.Max(0, (int)(sorted.Length * 0.95) - 1)];
}
