using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SetYazilim.Llm.Context;

/// <summary>
/// Loads skill system prompts from Markdown files at startup.
/// File convention: Skills/{agentId}/{skillName}.md  OR  Skills/{skillName}.md
/// The first H1 heading is stripped and used as title; the rest becomes the system prompt.
/// </summary>
public sealed class SkillRegistry
{
    private readonly ConcurrentDictionary<string, string> _prompts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<SkillRegistry> _log;

    public SkillRegistry(ILogger<SkillRegistry> log) => _log = log;

    public void LoadFromDirectory(string skillsPath)
    {
        if (!Directory.Exists(skillsPath))
        {
            _log.LogWarning("Skills directory not found: {Path}", skillsPath);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(skillsPath, "*.md", SearchOption.AllDirectories))
        {
            var key = BuildKey(skillsPath, file);
            var content = File.ReadAllText(file).Trim();
            // Strip first H1 line if present
            if (content.StartsWith("# "))
            {
                var nl = content.IndexOf('\n');
                content = nl > 0 ? content[(nl + 1)..].TrimStart() : string.Empty;
            }
            _prompts[key] = content;
            _log.LogInformation("Loaded skill: {Key}", key);
        }
    }

    /// <summary>
    /// Returns the system prompt for agent+skill.
    /// Falls back to skill-only key, then a default.
    /// </summary>
    public string GetSystemPrompt(string agentId, string skillName)
    {
        var specific = $"{agentId}/{skillName}";
        if (_prompts.TryGetValue(specific, out var p1)) return p1;
        if (_prompts.TryGetValue(skillName, out var p2)) return p2;

        _log.LogWarning("No skill prompt found for {Agent}/{Skill} — using default", agentId, skillName);
        return $"Sen {agentId} ajanının {skillName} uzmanısın. Doğru, kısa ve Türkçe yanıtlar ver.";
    }

    public void Register(string key, string systemPrompt) =>
        _prompts[key] = systemPrompt;

    public bool Remove(string key) =>
        _prompts.TryRemove(key, out _);

    public string? SkillsPath { get; private set; }

    public void LoadFromDirectory(string skillsPath, bool setPath)
    {
        if (setPath) SkillsPath = skillsPath;
        LoadFromDirectory(skillsPath);
    }

    public IReadOnlyDictionary<string, string> All => _prompts;

    private static string BuildKey(string root, string file)
    {
        var rel = Path.GetRelativePath(root, file)
                      .Replace('\\', '/')
                      .Replace(".md", "", StringComparison.OrdinalIgnoreCase);
        return rel;
    }
}
