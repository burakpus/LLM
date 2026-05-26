using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using SetYazilim.Llm.Context;

namespace SetYazilim.Llm.Api.Endpoints;

/// <summary>
/// Skill endpoints — public (read), admin (CRUD, import-anthropic, order, examples),
/// plus /api/models/capabilities (model feature matrix).
/// </summary>
public static class SkillsEndpoints
{
    // ── Skill order overrides — loaded from DB, cached 30s ─────────────────────
    private static async Task<Dictionary<string, int>> LoadSkillOrderOverrides(NpgsqlDataSource ds, CancellationToken ct)
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = "SELECT skill_id, order_value FROM skill_settings";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                dict[r.GetString(0)] = r.GetInt32(1);
        }
        catch { /* table may not exist yet — fall through with empty dict */ }
        return dict;
    }

    private static async Task<Dictionary<string, int>> GetCachedSkillOrders(
        IMemoryCache cache, NpgsqlDataSource ds, CancellationToken ct)
    {
        const string key = "skillOrderOverrides";
        if (cache.TryGetValue(key, out object? rawCached) && rawCached is Dictionary<string, int> cached)
            return cached;
        var fresh = await LoadSkillOrderOverrides(ds, ct);
        using var ce = cache.CreateEntry(key);
        ce.Value = fresh;
        ce.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
        ce.Size = 1;
        return fresh;
    }

    public static IEndpointRouteBuilder MapSkills(this IEndpointRouteBuilder app)
    {
        // GET /api/models/capabilities — model feature matrix for frontend routing decisions
        app.MapGet("/api/models/capabilities", [Authorize] () => Results.Ok(new Dictionary<string, object>
        {
            ["chat"]   = new { supportsVision = true,  supportsTools = false, contextWindow = 32768,
                               description = "Gemma 4 26B — genel asistan, doküman analizi, görsel anlama" },
            ["code"]   = new { supportsVision = false, supportsTools = true,  contextWindow = 32768,
                               description = "Qwen3.6 27B — kod üretimi, ajansal görevler, tool kullanımı" },
            ["reason"] = new { supportsVision = false, supportsTools = true,  contextWindow = 32768,
                               description = "GPT-OSS 120B — derin muhakeme, agent orchestration" },
            ["embed"]  = new { supportsVision = false, supportsTools = false, contextWindow = 2048,
                               description = "nomic-embed-text-v1.5 — 768 boyut embedding" },
        }));

        // GET /api/skills — list skills with metadata (all authenticated users)
        app.MapGet("/api/skills", [Authorize] async (SkillRegistry registry, NpgsqlDataSource ds,
            IMemoryCache cache, CancellationToken ct) =>
        {
            var overrides = await GetCachedSkillOrders(cache, ds, ct);
            var skills = registry.Metadata.Values
                .Select(m =>
                {
                    var ord = overrides.TryGetValue(m.Id, out var ov) ? ov : m.Order;
                    return new
                    {
                        id              = m.Id,
                        name            = m.Name,
                        description     = m.Description,
                        icon            = m.Icon,
                        collection      = m.Collection,
                        order           = ord,
                        isFolder        = m.IsFolder,
                        referenceCount  = m.ReferenceCount,
                        contentBytes    = m.ContentBytes,
                    };
                })
                .OrderBy(s => s.order)
                .ThenBy(s => s.name, StringComparer.OrdinalIgnoreCase);
            return Results.Ok(skills);
        });

        // GET /api/skills/{id} — get skill system prompt body (no frontmatter)
        app.MapGet("/api/skills/{id}", [Authorize] (string id, SkillRegistry registry) =>
        {
            var prompt = registry.GetSystemPrompt("default", id);
            return Results.Text(prompt, "text/plain; charset=utf-8");
        });

        // GET /api/skills/{id}/examples — list examples (all authenticated users)
        app.MapGet("/api/skills/{id}/examples", [Authorize] async (
            string id, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = @"SELECT id, user_message, assistant_message, sort_order
                                FROM skill_examples WHERE skill_id=$1 ORDER BY sort_order, id";
            cmd.Parameters.AddWithValue(id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var rows = new List<object>();
            while (await r.ReadAsync(ct))
                rows.Add(new { id = r.GetInt32(0), userMessage = r.GetString(1), assistantMessage = r.GetString(2), sortOrder = r.GetInt32(3) });
            return Results.Ok(rows);
        });

        // POST /api/admin/skills/{id}/examples — add example (admin only)
        app.MapPost("/api/admin/skills/{id}/examples", [Authorize("AdminOnly")] async (
            string id, [FromBody] SkillExampleRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO skill_examples (skill_id, user_message, assistant_message, sort_order)
                                VALUES ($1, $2, $3, (SELECT COALESCE(MAX(sort_order)+1, 0) FROM skill_examples WHERE skill_id=$1))
                                RETURNING id, sort_order";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(req.UserMessage);
            cmd.Parameters.AddWithValue(req.AssistantMessage);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return Results.Ok(new { id = r.GetInt32(0), sortOrder = r.GetInt32(1) });
        });

        // PUT /api/admin/skills/{id}/examples/{exId} — update example (admin only)
        app.MapPut("/api/admin/skills/{id}/examples/{exId:int}", [Authorize("AdminOnly")] async (
            string id, int exId, [FromBody] SkillExampleRequest req, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = @"UPDATE skill_examples SET user_message=$1, assistant_message=$2
                                WHERE id=$3 AND skill_id=$4";
            cmd.Parameters.AddWithValue(req.UserMessage);
            cmd.Parameters.AddWithValue(req.AssistantMessage);
            cmd.Parameters.AddWithValue(exId);
            cmd.Parameters.AddWithValue(id);
            var rows = await cmd.ExecuteNonQueryAsync(ct);
            return rows == 0 ? Results.NotFound() : Results.Ok(new { ok = true });
        });

        // DELETE /api/admin/skills/{id}/examples/{exId} — delete example (admin only)
        app.MapDelete("/api/admin/skills/{id}/examples/{exId:int}", [Authorize("AdminOnly")] async (
            string id, int exId, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM skill_examples WHERE id=$1 AND skill_id=$2";
            cmd.Parameters.AddWithValue(exId);
            cmd.Parameters.AddWithValue(id);
            await cmd.ExecuteNonQueryAsync(ct);
            return Results.NoContent();
        });

        // GET /api/admin/skills — admin list with order overrides
        app.MapGet("/api/admin/skills", [Authorize("AdminOnly")] async (
            SkillRegistry registry, NpgsqlDataSource ds,
            IMemoryCache cache, CancellationToken ct) =>
        {
            var overrides = await GetCachedSkillOrders(cache, ds, ct);
            var items = registry.Metadata.Values
                .Select(m =>
                {
                    var ord = overrides.TryGetValue(m.Id, out var ov) ? ov : m.Order;
                    return new
                    {
                        id              = m.Id,
                        name            = m.Name,
                        description     = m.Description,
                        order           = ord,
                        isFolder        = m.IsFolder,
                        referenceCount  = m.ReferenceCount,
                        size            = (int)m.ContentBytes,
                    };
                })
                .OrderBy(s => s.order)
                .ThenBy(s => s.name, StringComparer.OrdinalIgnoreCase);
            return Results.Ok(items);
        });

        // GET /api/admin/skills/{id}
        app.MapGet("/api/admin/skills/{id}", [Authorize("AdminOnly")] (string id, SkillRegistry registry) =>
        {
            if (!registry.All.ContainsKey(id)) return Results.NotFound();
            return Results.Text(registry.All[id], "text/plain; charset=utf-8");
        });

        // POST /api/admin/skills — upload a .md skill file OR a .zip skill folder
        app.MapPost("/api/admin/skills", [Authorize("AdminOnly")] async (
            HttpContext http,
            SkillRegistry registry,
            ClaimsPrincipal user,
            NpgsqlDataSource ds,
            CancellationToken ct) =>
        {
            if (!http.Request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data required" });

            var form     = await http.Request.ReadFormAsync(ct);
            var username = user.FindFirstValue(ClaimTypes.Name) ?? "unknown";
            var results  = new List<object>();

            foreach (var file in form.Files)
            {
                var fname = file.FileName;
                var ext   = Path.GetExtension(fname).ToLowerInvariant();

                if (ext == ".zip")
                {
                    // Folder-based skill upload via zip
                    if (registry.SkillsPath is null)
                    {
                        results.Add(new { file = fname, ok = false, error = "SkillsPath not configured" });
                        continue;
                    }
                    try
                    {
                        using var ms = new MemoryStream();
                        await file.CopyToAsync(ms, ct);
                        ms.Position = 0;
                        using var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);

                        // Determine skill id from zip: look for SKILL.md entry
                        string? skillId = null;
                        foreach (var entry in zip.Entries)
                        {
                            if (entry.Name.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                            {
                                skillId = entry.FullName.Contains('/')
                                    ? entry.FullName.Split('/')[0]
                                    : Path.GetFileNameWithoutExtension(fname);
                                break;
                            }
                        }
                        skillId ??= Path.GetFileNameWithoutExtension(fname);

                        var destDir = Path.GetFullPath(Path.Combine(registry.SkillsPath, skillId));
                        if (!destDir.StartsWith(registry.SkillsPath, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(new { file = fname, ok = false, error = "invalid skill id" });
                            continue;
                        }
                        Directory.CreateDirectory(destDir);

                        foreach (var entry in zip.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry
                            var relPath = entry.FullName;
                            if (relPath.StartsWith(skillId + "/", StringComparison.OrdinalIgnoreCase))
                                relPath = relPath[(skillId.Length + 1)..];

                            var destPath = Path.GetFullPath(Path.Combine(destDir, relPath));
                            if (!destPath.StartsWith(destDir + Path.DirectorySeparatorChar) &&
                                !destPath.Equals(destDir, StringComparison.OrdinalIgnoreCase))
                                continue; // zip-slip guard

                            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                            using var stream = entry.Open();
                            using var fs     = File.Create(destPath);
                            await stream.CopyToAsync(fs, ct);
                        }

                        // Hot reload skills
                        registry.LoadFromDirectory(registry.SkillsPath);
                        results.Add(new { file = fname, ok = true, id = skillId });
                        _ = ActivityLogger.LogAsync(ds, username, "skill.upload", skillId, $"type=zip size={file.Length}");
                    }
                    catch (Exception ex)
                    {
                        results.Add(new { file = fname, ok = false, error = ex.Message });
                    }
                    continue;
                }

                if (ext != ".md")
                {
                    results.Add(new { file = fname, ok = false, error = "Only .md or .zip files allowed" });
                    continue;
                }

                using var reader = new StreamReader(file.OpenReadStream());
                var content = (await reader.ReadToEndAsync(ct)).Trim();
                var skillId2 = Path.GetFileNameWithoutExtension(fname);

                if (registry.SkillsPath is not null)
                {
                    var filePath = Path.Combine(registry.SkillsPath, fname);
                    await File.WriteAllTextAsync(filePath, content, ct);
                }

                registry.Register(skillId2, content);
                results.Add(new { file = fname, ok = true, id = skillId2 });
                _ = ActivityLogger.LogAsync(ds, username, "skill.upload", skillId2, $"size={file.Length}");
            }

            return Results.Ok(results);
        });

        // POST /api/admin/skills/import-anthropic — download selected skills from anthropics/skills GitHub repo
        app.MapPost("/api/admin/skills/import-anthropic", [Authorize("AdminOnly")] async (
            [FromBody] AnthropicSkillImportRequest req,
            SkillRegistry registry,
            ClaimsPrincipal user,
            NpgsqlDataSource ds,
            IHttpClientFactory httpClientFactory,
            CancellationToken ct) =>
        {
            if (registry.SkillsPath is null)
                return Results.BadRequest(new { error = "SkillsPath not configured" });
            if (req.Skills is null || req.Skills.Length == 0)
                return Results.BadRequest(new { error = "No skills specified" });

            var username = user.FindFirstValue(ClaimTypes.Name) ?? "unknown";
            var http     = httpClientFactory.CreateClient("github");
            var results  = new List<object>();

            // Subdirectory names we never download — scripts/templates/schemas etc.
            var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "scripts", "schemas", "templates", "assets", "canvas-fonts", "core", "eval-viewer" };

            // Get full repo tree once (single API call)
            List<GithubTreeItem>? tree = null;
            try
            {
                var treeResp = await http.GetFromJsonAsync<GithubTreeResponse>(
                    "https://api.github.com/repos/anthropics/skills/git/trees/main?recursive=1",
                    ct);
                tree = treeResp?.Tree ?? new();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"GitHub tree fetch failed: {ex.Message}" });
            }

            foreach (var rawName in req.Skills)
            {
                ct.ThrowIfCancellationRequested();
                var skillName = (rawName ?? "").Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(skillName)) continue;

                try
                {
                    // Path-safety: skillName must be a simple folder name
                    if (skillName.Contains('/') || skillName.Contains('\\') || skillName.Contains(".."))
                    {
                        results.Add(new { skill = skillName, ok = false, error = "invalid skill name" });
                        continue;
                    }

                    var destDir = Path.GetFullPath(Path.Combine(registry.SkillsPath, skillName));
                    if (!destDir.StartsWith(registry.SkillsPath, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new { skill = skillName, ok = false, error = "path traversal" });
                        continue;
                    }

                    if (Directory.Exists(destDir) && !req.Overwrite)
                    {
                        results.Add(new { skill = skillName, ok = true, action = "skipped (already exists)" });
                        continue;
                    }
                    if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
                    Directory.CreateDirectory(destDir);

                    var prefix = $"skills/{skillName}/";
                    var mdFiles = tree
                        .Where(i => i.Type == "blob"
                                    && i.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                    && i.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (mdFiles.Count == 0)
                    {
                        results.Add(new { skill = skillName, ok = false, error = "no .md files found in repo" });
                        Directory.Delete(destDir, recursive: true);
                        continue;
                    }

                    var downloaded = new List<string>();
                    foreach (var item in mdFiles)
                    {
                        var pathInSkill = item.Path[prefix.Length..];
                        var firstSeg    = pathInSkill.Contains('/') ? pathInSkill.Split('/')[0] : "";
                        if (!string.IsNullOrEmpty(firstSeg) && skipDirs.Contains(firstSeg)) continue;

                        var rawUrl = $"https://raw.githubusercontent.com/anthropics/skills/main/{item.Path}";
                        var content = await http.GetStringAsync(rawUrl, ct);
                        var localPath = Path.GetFullPath(Path.Combine(destDir, pathInSkill));
                        if (!localPath.StartsWith(destDir + Path.DirectorySeparatorChar) &&
                            !localPath.Equals(destDir, StringComparison.OrdinalIgnoreCase))
                            continue; // path-traversal guard
                        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                        await File.WriteAllTextAsync(localPath, content, ct);
                        downloaded.Add(pathInSkill);
                    }

                    if (!downloaded.Any(f => f.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)))
                    {
                        Directory.Delete(destDir, recursive: true);
                        results.Add(new { skill = skillName, ok = false, error = "SKILL.md missing after download" });
                        continue;
                    }

                    results.Add(new { skill = skillName, ok = true, action = "imported", files = downloaded.Count });
                    _ = ActivityLogger.LogAsync(ds, username, "skill.import", skillName, $"files={downloaded.Count}");
                }
                catch (Exception ex)
                {
                    results.Add(new { skill = skillName, ok = false, error = ex.Message });
                }
            }

            // Hot-reload all skills from directory
            registry.LoadFromDirectory(registry.SkillsPath);

            var imported = results.Count(r =>
            {
                var ok = r.GetType().GetProperty("ok")?.GetValue(r);
                var action = r.GetType().GetProperty("action")?.GetValue(r) as string;
                return ok is true && action != null && action.StartsWith("imported");
            });
            return Results.Ok(new { results, imported });
        });

        // PUT /api/admin/skills/{id}/order — update skill order (DB-stored, survives deploys)
        // body: { order: number }
        app.MapPut("/api/admin/skills/{id}/order", [Authorize("AdminOnly")] async (
            string id, [FromBody] SkillOrderRequest req,
            SkillRegistry registry, ClaimsPrincipal user, NpgsqlDataSource ds,
            IMemoryCache cache,
            CancellationToken ct) =>
        {
            if (!registry.All.ContainsKey(id))
                return Results.NotFound(new { error = $"Skill '{id}' not found" });

            await using var conn = await ds.OpenConnectionAsync(ct);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO skill_settings (skill_id, order_value, updated_at)
                                VALUES ($1, $2, NOW())
                                ON CONFLICT (skill_id) DO UPDATE
                                SET order_value = $2, updated_at = NOW()";
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(req.Order);
            await cmd.ExecuteNonQueryAsync(ct);

            cache.Remove("skillOrderOverrides");  // invalidate cached list

            var username = user.FindFirstValue(ClaimTypes.Name) ?? "unknown";
            _ = ActivityLogger.LogAsync(ds, username, "skill.order.update", id, $"order={req.Order}");
            return Results.Ok(new { id, order = req.Order });
        });

        // DELETE /api/admin/skills/{id}
        app.MapDelete("/api/admin/skills/{id}", [Authorize("AdminOnly")] (
            string id, SkillRegistry registry,
            ClaimsPrincipal user, NpgsqlDataSource ds) =>
        {
            if (!registry.All.ContainsKey(id))
                return Results.NotFound(new { error = $"Skill '{id}' not found" });

            if (registry.SkillsPath is not null)
            {
                // Folder-based skill?
                var folderPath = Path.Combine(registry.SkillsPath, id);
                if (Directory.Exists(folderPath))
                {
                    Directory.Delete(folderPath, recursive: true);
                }
                else
                {
                    var filePath = Path.Combine(registry.SkillsPath, id + ".md");
                    if (File.Exists(filePath)) File.Delete(filePath);
                }
            }

            registry.Remove(id);

            // Cleanup DB-stored order override
            try
            {
                using var c = ds.OpenConnection();
                using var d = c.CreateCommand();
                d.CommandText = "DELETE FROM skill_settings WHERE skill_id=$1";
                d.Parameters.AddWithValue(id);
                d.ExecuteNonQuery();
            }
            catch { /* table may be missing — non-fatal */ }

            var username = user.FindFirstValue(ClaimTypes.Name) ?? "unknown";
            _ = ActivityLogger.LogAsync(ds, username, "skill.delete", id);
            return Results.Ok(new { deleted = id });
        });

        return app;
    }
}
