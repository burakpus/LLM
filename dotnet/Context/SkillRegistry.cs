using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SetYazilim.Llm.Context;

public sealed record SkillMeta(
    string Id,
    string Name,
    string Description,
    string Icon,
    string? Collection,
    bool   IsFolder,
    int    ReferenceCount,
    long   ContentBytes);

/// <summary>
/// Loads skill system prompts from Markdown files at startup.
///
/// Two modes:
/// 1. Flat file:   Skills/{skillId}.md
/// 2. Folder:      Skills/{skillId}/SKILL.md  (+ optional reference .md files)
///
/// Folder-based skills concatenate SKILL.md body + all *.md files in the folder
/// (excluding LICENSE.txt, binary dirs like scripts/, schemas/) up to 100 KB total.
/// </summary>
public sealed class SkillRegistry
{
    private readonly ConcurrentDictionary<string, string>    _prompts  = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SkillMeta> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<SkillRegistry> _log;

    // Subdirectory names we never include reference docs from
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "scripts", "schemas", "templates", "assets", "canvas-fonts", "core", "eval-viewer" };

    public SkillRegistry(ILogger<SkillRegistry> log) => _log = log;

    public string? SkillsPath { get; private set; }

    public IReadOnlyDictionary<string, string>    All      => _prompts;
    public IReadOnlyDictionary<string, SkillMeta> Metadata => _metadata;

    public void LoadFromDirectory(string skillsPath, bool setPath = false)
    {
        if (setPath) SkillsPath = skillsPath;

        if (!Directory.Exists(skillsPath))
        {
            _log.LogWarning("Skills directory not found: {Path}", skillsPath);
            return;
        }

        // Clear existing state — full reload semantics
        _prompts.Clear();
        _metadata.Clear();

        // Pass 1: folder-based skills (dirs containing SKILL.md at top level)
        var folderSkillIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subDir in Directory.EnumerateDirectories(skillsPath))
        {
            var skillMdPath = Path.Combine(subDir, "SKILL.md");
            if (!File.Exists(skillMdPath)) continue;
            var skillId = Path.GetFileName(subDir);
            folderSkillIds.Add(skillId);
            LoadFolderSkill(skillId, subDir);
        }

        // Pass 2: flat .md files at root level (legacy convention)
        foreach (var file in Directory.EnumerateFiles(skillsPath, "*.md"))
        {
            var skillId = Path.GetFileNameWithoutExtension(file);
            if (folderSkillIds.Contains(skillId)) continue;  // folder takes precedence

            var content = File.ReadAllText(file).Trim();
            // Strip first H1 if present (legacy flat-file convention)
            if (content.StartsWith("# "))
            {
                var nl = content.IndexOf('\n');
                content = nl > 0 ? content[(nl + 1)..].TrimStart() : string.Empty;
            }
            _prompts[skillId] = content;
            _metadata[skillId] = BuildMeta(skillId, content, isFolder: false, refCount: 0);
            _log.LogInformation("Loaded flat skill: {Id}", skillId);
        }
    }

    private void LoadFolderSkill(string skillId, string dir)
    {
        var skillMdPath = Path.Combine(dir, "SKILL.md");
        if (!File.Exists(skillMdPath)) return;

        var primary = File.ReadAllText(skillMdPath).Trim();
        var body    = StripFrontmatter(primary, out _);

        // Collect reference .md files from the folder (excluding excluded subdirs)
        var refs       = new List<(string RelPath, string Content)>();
        long totalBytes = Encoding.UTF8.GetByteCount(body);
        const long maxBytes = 100_000; // 100 KB cap

        foreach (var mdFile in Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories)
                                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                        .Where(f => !string.Equals(f, skillMdPath, StringComparison.OrdinalIgnoreCase)))
        {
            var rel      = Path.GetRelativePath(dir, mdFile).Replace('\\', '/');
            var firstSeg = rel.Split('/')[0];
            if (SkipDirs.Contains(firstSeg)) continue;
            if (Path.GetFileName(mdFile).Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase)) continue;

            var refContent = File.ReadAllText(mdFile).Trim();
            var refBytes   = Encoding.UTF8.GetByteCount(refContent);
            if (totalBytes + refBytes > maxBytes)
            {
                _log.LogWarning("Skill {Id}: ref file {Rel} skipped — would exceed 100KB cap", skillId, rel);
                break;
            }

            refs.Add((rel, refContent));
            totalBytes += refBytes;
        }

        // Assemble final prompt
        var sb = new StringBuilder(body);
        foreach (var (relPath, content) in refs)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine($"## Reference: {relPath}");
            sb.AppendLine();
            sb.AppendLine(content);
        }

        var finalPrompt = sb.ToString().TrimEnd();
        _prompts[skillId]  = finalPrompt;
        _metadata[skillId] = BuildMeta(skillId, primary, isFolder: true, refCount: refs.Count);
        _log.LogInformation("Loaded folder skill: {Id} ({Refs} ref files, {KB} KB)",
            skillId, refs.Count, totalBytes / 1024);
    }

    private static string StripFrontmatter(string content, out Dictionary<string, string> fm)
    {
        fm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trimmed = content.TrimStart();
        if (!trimmed.StartsWith("---")) return trimmed;

        var end = trimmed.IndexOf("---", 3, StringComparison.Ordinal);
        if (end < 0) return trimmed;

        foreach (var line in trimmed[3..end].Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            fm[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return trimmed[(end + 3)..].TrimStart();
    }

    private static SkillMeta BuildMeta(string id, string rawContent, bool isFolder, int refCount)
    {
        var name = id; var desc = ""; var icon = "sparkles"; string? collection = null;
        var trimmed = rawContent.TrimStart();
        if (trimmed.StartsWith("---"))
        {
            var end = trimmed.IndexOf("---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                foreach (var line in trimmed[3..end].Split('\n'))
                {
                    var colon = line.IndexOf(':');
                    if (colon < 0) continue;
                    var k = line[..colon].Trim().ToLowerInvariant();
                    var v = line[(colon + 1)..].Trim();
                    if (k == "name")        name       = v;
                    if (k == "description") desc       = v;
                    if (k == "icon")        icon       = v;
                    if (k == "collection")  collection = v;
                }
            }
        }
        // If no frontmatter name, try first H1
        if (string.Equals(name, id, StringComparison.OrdinalIgnoreCase))
        {
            var body = StripFrontmatter(rawContent, out _);
            if (body.TrimStart().StartsWith("# "))
            {
                var nl = body.IndexOf('\n');
                if (nl > 0) name = body[2..nl].Trim();
            }
        }
        return new SkillMeta(id, name, desc, icon, collection, isFolder, refCount,
            Encoding.UTF8.GetByteCount(rawContent));
    }

    public void Register(string key, string systemPrompt)
    {
        _prompts[key]  = systemPrompt;
        _metadata[key] = BuildMeta(key, systemPrompt, isFolder: false, refCount: 0);
    }

    public bool Remove(string key)
    {
        _metadata.TryRemove(key, out _);
        return _prompts.TryRemove(key, out _);
    }

    public string GetSystemPrompt(string agentId, string skillName)
    {
        var specific = $"{agentId}/{skillName}";
        if (_prompts.TryGetValue(specific, out var p1)) return p1;
        if (_prompts.TryGetValue(skillName, out var p2)) return p2;
        _log.LogWarning("No skill prompt found for {Agent}/{Skill}", agentId, skillName);
        return $"Sen {agentId} ajanının {skillName} uzmanısın. Doğru, kısa ve Türkçe yanıtlar ver.";
    }
}
