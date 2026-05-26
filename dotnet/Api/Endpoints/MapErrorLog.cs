using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// /api/log/error — append client-side error reports to daily log files.
/// </summary>
public static class ErrorLogEndpoints
{
    public static IEndpointRouteBuilder MapErrorLog(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/log/error", [Authorize] async (
            [FromBody] ErrorLogRequest req,
            ClaimsPrincipal user,
            IWebHostEnvironment env) =>
        {
            var logDir  = Path.Combine(env.ContentRootPath, "Logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, $"error_{DateTime.Now:yyyy-MM-dd}.log");
            var line    = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] USER: {user.FindFirstValue(ClaimTypes.Name)}\n{req.Message}\n{new string('-', 40)}\n";
            await File.AppendAllTextAsync(logPath, line);
            return Results.Ok();
        });

        return app;
    }
}
