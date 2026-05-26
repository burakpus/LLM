using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// /api/projects/{projectId}/* — per-user file storage at ~/llm-projects/{userId}/{projectId}/.
/// All paths are sandboxed (path traversal protected).
/// </summary>
public static class ProjectsEndpoints
{
    private static string ProjectRoot(string userId, string projectId)
    {
        var home  = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root  = Path.Combine(home, "llm-projects",
            string.Join("_", userId.Split(Path.GetInvalidFileNameChars())),
            string.Join("_", projectId.Split(Path.GetInvalidFileNameChars())));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string SafeJoin(string root, string relPath)
    {
        var full = Path.GetFullPath(Path.Combine(root, relPath.TrimStart('/')));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path traversal detected");
        return full;
    }

    public static IEndpointRouteBuilder MapProjects(this IEndpointRouteBuilder app)
    {
        // GET /api/projects/{projectId}/files
        app.MapGet("/api/projects/{projectId}/files", [Authorize] (
            string projectId, ClaimsPrincipal principal) =>
        {
            var root  = ProjectRoot(principal.FindFirstValue(ClaimTypes.Name) ?? "anon", projectId);
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => new {
                    path      = Path.GetRelativePath(root, f).Replace('\\', '/'),
                    updatedAt = File.GetLastWriteTimeUtc(f),
                    size      = new FileInfo(f).Length,
                })
                .OrderBy(f => f.path);
            return Results.Ok(files);
        });

        // GET /api/projects/{projectId}/files/{*path}
        app.MapGet("/api/projects/{projectId}/files/{*path}", [Authorize] async (
            string projectId, string path, ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var root = ProjectRoot(principal.FindFirstValue(ClaimTypes.Name) ?? "anon", projectId);
            var full = SafeJoin(root, path);
            if (!File.Exists(full)) return Results.NotFound();
            var content = await File.ReadAllTextAsync(full, ct);
            return Results.Ok(new { path, content });
        });

        // PUT /api/projects/{projectId}/files/{*path}
        app.MapPut("/api/projects/{projectId}/files/{*path}", [Authorize] async (
            string projectId, string path, HttpContext http,
            ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var root  = ProjectRoot(principal.FindFirstValue(ClaimTypes.Name) ?? "anon", projectId);
            var full  = SafeJoin(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var reader = new StreamReader(http.Request.Body);
            var body    = await reader.ReadToEndAsync(ct);
            var payload = System.Text.Json.JsonDocument.Parse(body);
            var content = payload.RootElement.GetProperty("content").GetString() ?? "";
            await File.WriteAllTextAsync(full, content, ct);
            return Results.Ok(new { path, updatedAt = File.GetLastWriteTimeUtc(full) });
        });

        // DELETE /api/projects/{projectId}/files/{*path}
        app.MapDelete("/api/projects/{projectId}/files/{*path}", [Authorize] (
            string projectId, string path, ClaimsPrincipal principal) =>
        {
            var root = ProjectRoot(principal.FindFirstValue(ClaimTypes.Name) ?? "anon", projectId);
            var full = SafeJoin(root, path);
            if (File.Exists(full)) File.Delete(full);
            return Results.NoContent();
        });

        // DELETE /api/projects/{projectId} — delete entire project directory
        app.MapDelete("/api/projects/{projectId}", [Authorize] (
            string projectId, ClaimsPrincipal principal) =>
        {
            var userId = principal.FindFirstValue(ClaimTypes.Name) ?? "anon";
            var home   = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var root   = Path.Combine(home, "llm-projects",
                string.Join("_", userId.Split(Path.GetInvalidFileNameChars())),
                string.Join("_", projectId.Split(Path.GetInvalidFileNameChars())));

            if (!Directory.Exists(root)) return Results.NotFound(new { error = "Project not found" });

            Directory.Delete(root, recursive: true);
            return Results.Ok(new { deleted = true, project = projectId });
        });

        return app;
    }
}
