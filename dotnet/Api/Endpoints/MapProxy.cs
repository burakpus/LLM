using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// /api/proxy — CORS bypass for agentic tool calls. Server fetches arbitrary URL on behalf
/// of the authenticated user (15s timeout, no streaming).
/// </summary>
public static class ProxyEndpoints
{
    public static IEndpointRouteBuilder MapProxy(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/proxy", [Authorize] async (
            [FromBody] ProxyRequest req,
            IHttpClientFactory httpFactory,
            CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(req.Url))
                return Results.BadRequest(new { error = "url is required" });

            try
            {
                using var client = httpFactory.CreateClient("proxy");
                client.Timeout = TimeSpan.FromSeconds(15);

                var method  = new HttpMethod(req.Method?.ToUpperInvariant() ?? "GET");
                using var msg = new HttpRequestMessage(method, req.Url);
                msg.Headers.Add("User-Agent", "SET-LLM-Agent/2.0");

                if (method != HttpMethod.Get && req.Body is not null)
                    msg.Content = new StringContent(req.Body, Encoding.UTF8,
                        req.ContentType ?? "application/json");

                using var resp = await client.SendAsync(msg, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                return Results.Text(body, "application/json", statusCode: (int)resp.StatusCode);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        return app;
    }
}
