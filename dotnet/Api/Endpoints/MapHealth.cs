using Microsoft.Extensions.Options;
using Npgsql;
using Prometheus;
using SetYazilim.Llm.Api.Auth;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// /health, /health/deep, /metrics — no auth required (internal-only by network policy).
/// </summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok", ts = DateTime.UtcNow }));

        // GET /health/deep — derin sağlık kontrolü (DB + LDAP + vLLM modelleri)
        // Auth gerekli değil ama dış erişime karşı sadece internal kullanılmalı.
        // Tüm probe'ların kombine timeout'u 12 saniye.
        app.MapGet("/health/deep", async (
            NpgsqlDataSource ds, IConfiguration cfg,
            IOptions<LdapOptions> ldapOpts,
            IHttpClientFactory httpFactory,
            CancellationToken httpCt) =>
        {
            using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(httpCt);
            overallCts.CancelAfter(TimeSpan.FromSeconds(12));
            var ct = overallCts.Token;
            var probes = new Dictionary<string, object>();
            var allOk  = true;

            // 1) Database
            try
            {
                await using var conn = await ds.OpenConnectionAsync(ct);
                await using var cmd  = conn.CreateCommand();
                cmd.CommandText = "SELECT 1";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await cmd.ExecuteScalarAsync(ct);
                probes["db"] = new { ok = true, ms = sw.ElapsedMilliseconds };
            }
            catch (Exception ex)
            {
                probes["db"] = new { ok = false, error = ex.Message };
                allOk = false;
            }

            // 2) LDAP (her domain için TCP probe — bind yapmadan, 3sn hard timeout)
            foreach (var (name, dcfg) in ldapOpts.Value.Domains)
            {
                if (string.IsNullOrEmpty(dcfg.Host)) continue;
                var key = $"ldap.{name}";
                try
                {
                    using var tcp = new System.Net.Sockets.TcpClient();
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    probeCts.CancelAfter(TimeSpan.FromSeconds(3));
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await tcp.ConnectAsync(dcfg.Host, dcfg.EffectivePort, probeCts.Token);
                    probes[key] = new { ok = true, ms = sw.ElapsedMilliseconds, host = dcfg.Host, port = dcfg.EffectivePort };
                }
                catch (OperationCanceledException)
                {
                    probes[key] = new { ok = false, error = "timeout (>3s)", host = dcfg.Host, port = dcfg.EffectivePort };
                    allOk = false;
                }
                catch (Exception ex)
                {
                    probes[key] = new { ok = false, error = ex.Message, host = dcfg.Host, port = dcfg.EffectivePort };
                    allOk = false;
                }
            }

            // 3) vLLM modelleri — /v1/models (LiteLLM proxy üzerinden)
            var litellmBase = cfg["LiteLLM:BaseUrl"] ?? "http://localhost:4000";
            var litellmKey  = cfg["LiteLLM:ApiKey"];
            var http        = httpFactory.CreateClient("health");
            http.Timeout    = TimeSpan.FromSeconds(5);
            try
            {
                using var msg = new HttpRequestMessage(HttpMethod.Get, $"{litellmBase}/v1/models");
                if (!string.IsNullOrEmpty(litellmKey))
                    msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", litellmKey);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var resp = await http.SendAsync(msg, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var body  = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = System.Text.Json.JsonDocument.Parse(body);
                    var count = doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == System.Text.Json.JsonValueKind.Array
                        ? data.GetArrayLength() : 0;
                    probes["litellm"] = new { ok = true, ms = sw.ElapsedMilliseconds, modelCount = count };
                }
                else
                {
                    probes["litellm"] = new { ok = false, status = (int)resp.StatusCode };
                    allOk = false;
                }
            }
            catch (Exception ex)
            {
                probes["litellm"] = new { ok = false, error = ex.Message };
                allOk = false;
            }

            var payload = new {
                status = allOk ? "ok" : "degraded",
                ts     = DateTime.UtcNow,
                probes,
            };
            return allOk ? Results.Ok(payload) : Results.Json(payload, statusCode: 503);
        });

        // Prometheus scrape endpoint (no auth — internal only).
        // Returns IApplicationBuilder, so wrap to keep IEndpointRouteBuilder chain.
        if (app is WebApplication wapp) wapp.MapMetrics("/metrics");

        return app;
    }
}
