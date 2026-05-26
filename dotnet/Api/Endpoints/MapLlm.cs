using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Prometheus;
using SetYazilim.Llm;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// /api/llm/completions — authenticated proxy to LiteLLM /v1/chat/completions.
/// Injects the authenticated username into the upstream request so LiteLLM tracks
/// usage per user. Detects model warm-up (5xx with "Connection error") and converts
/// to 503 with a friendly message. Records request metrics.
/// </summary>
public static class LlmEndpoints
{
    public static IEndpointRouteBuilder MapLlm(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/llm/completions", [Authorize] [RequestSizeLimit(100 * 1024 * 1024)] async (
            HttpContext http,
            IOptions<LiteLLMOptions> litellmOpts,
            IHttpClientFactory httpFactory,
            ILoggerFactory loggerFactory,
            ClaimsPrincipal principal,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("LlmEndpoints");

            using var reader = new StreamReader(http.Request.Body);
            var bodyStr = await reader.ReadToEndAsync(ct);

            var debug = Environment.GetEnvironmentVariable("LLM_DEBUG_VISION") == "1";
            var rid   = debug ? Guid.NewGuid().ToString("N").Substring(0, 6) : "";
            if (debug) logger.LogInformation("[VISION {Rid}] B1. /api/llm/completions hit — body={Bytes}B", rid, bodyStr.Length);

            // Inject authenticated username so LiteLLM tracks usage per user
            var username = principal.FindFirstValue(ClaimTypes.Name) ?? "anonymous";
            try
            {
                var doc = JsonDocument.Parse(bodyStr);
                if (debug)
                {
                    var modelName = doc.RootElement.TryGetProperty("model", out var mdl) ? mdl.GetString() ?? "?" : "?";
                    var msgCount  = doc.RootElement.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array
                        ? msgs.GetArrayLength() : 0;
                    var hasVision = false;
                    if (doc.RootElement.TryGetProperty("messages", out var ms) && ms.ValueKind == JsonValueKind.Array)
                        foreach (var m in ms.EnumerateArray())
                            if (m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
                                foreach (var part in c.EnumerateArray())
                                    if (part.TryGetProperty("type", out var t) && t.GetString() == "image_url")
                                    { hasVision = true; break; }
                    logger.LogInformation("[VISION {Rid}] B2. parsed — user={User} model={Model} msgs={Msgs} hasVision={Vision}",
                        rid, username, modelName, msgCount, hasVision);
                }

                if (!doc.RootElement.TryGetProperty("user", out _))
                {
                    var obj = new Dictionary<string, object>();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        obj[prop.Name] = prop.Value.Clone();
                    obj["user"] = username;
                    bodyStr = JsonSerializer.Serialize(obj);
                }
            }
            catch (Exception ex)
            {
                if (debug) logger.LogError(ex, "[VISION {Rid}] B2. JSON parse failed", rid);
            }

            // Extract model name for metrics labels
            var metricModel = "unknown";
            try { if (JsonDocument.Parse(bodyStr).RootElement
                      .TryGetProperty("model", out var mdl)) metricModel = mdl.GetString() ?? "unknown"; }
            catch { /* ignore */ }

            var metricsTimer = LlmMetrics.DurationSeconds.WithLabels(metricModel).NewTimer();

            var opts      = litellmOpts.Value;
            var targetUrl = opts.BaseUrl.TrimEnd('/') + "/v1/chat/completions";
            var bearerKey = opts.ApiKey;

            using var client = httpFactory.CreateClient("proxy");
            client.Timeout = TimeSpan.FromSeconds(600);

            using var req = new HttpRequestMessage(HttpMethod.Post, targetUrl);
            req.Content = new StringContent(bodyStr, Encoding.UTF8, "application/json");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerKey);
            req.Headers.TryAddWithoutValidation("x-litellm-user", username);

            using var resp = await client.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead, ct);
            if (debug) logger.LogInformation("[VISION {Rid}] B5. upstream status={Status}", rid, (int)resp.StatusCode);

            // ── Warming-up detection ──────────────────────────────────────────────────
            // LiteLLM returns 500 with "Connection error" when the vLLM container is
            // still loading the model. Convert this to a 503 with a friendly message
            // so the frontend can show a warning instead of a hard error.
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                var isWarmingUp =
                    errBody.Contains("Connection error", StringComparison.OrdinalIgnoreCase) ||
                    errBody.Contains("No fallback model group", StringComparison.OrdinalIgnoreCase) ||
                    errBody.Contains("ServiceUnavailable", StringComparison.OrdinalIgnoreCase) ||
                    errBody.Contains("health check", StringComparison.OrdinalIgnoreCase);

                if (isWarmingUp)
                {
                    logger.LogWarning("Model warming up – upstream error for user {User}: {Error}",
                        username, errBody[..Math.Min(300, errBody.Length)]);
                    metricsTimer.Dispose();
                    LlmMetrics.RequestsTotal.WithLabels(metricModel, "warming").Inc();
                    return Results.Json(
                        new { error = "warming_up", message = "Model henüz yükleniyor, lütfen birkaç saniye sonra tekrar deneyin." },
                        statusCode: 503);
                }

                // Other non-success: proxy as-is
                metricsTimer.Dispose();
                LlmMetrics.RequestsTotal.WithLabels(metricModel, "error").Inc();
                http.Response.StatusCode = (int)resp.StatusCode;
                foreach (var h in resp.Headers)
                    if (!h.Key.StartsWith("Transfer", StringComparison.OrdinalIgnoreCase))
                        http.Response.Headers[h.Key] = h.Value.ToArray();
                foreach (var h in resp.Content.Headers)
                    http.Response.Headers[h.Key] = h.Value.ToArray();
                http.Response.ContentType = "application/json";
                await http.Response.WriteAsync(errBody, ct);
                return Results.Empty;
            }

            http.Response.StatusCode = (int)resp.StatusCode;
            foreach (var h in resp.Headers)
                if (!h.Key.StartsWith("Transfer", StringComparison.OrdinalIgnoreCase))
                    http.Response.Headers[h.Key] = h.Value.ToArray();
            foreach (var h in resp.Content.Headers)
                http.Response.Headers[h.Key] = h.Value.ToArray();

            try
            {
                await resp.Content.CopyToAsync(http.Response.Body, ct);
                if (debug) logger.LogInformation("[VISION {Rid}] B6. response streamed", rid);
                LlmMetrics.RequestsTotal.WithLabels(metricModel, "success").Inc();
            }
            catch (OperationCanceledException)
            {
                // Client disconnected — not an error
                LlmMetrics.RequestsTotal.WithLabels(metricModel, "cancelled").Inc();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "LLM stream error for model {Model} user {User}", metricModel, username);
                LlmMetrics.RequestsTotal.WithLabels(metricModel, "error").Inc();
            }
            finally
            {
                metricsTimer.Dispose();
            }
            return Results.Empty;
        });

        return app;
    }
}
