using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SetYazilim.Llm.Api.Tools;

public sealed record FileGenRequest(string Kind, string Filename, JsonElement Spec);

public sealed record FileGenResult(
    bool   Ok,
    string Kind,
    string Filename,
    string Token,          // opaque ID used in download URL
    long   SizeBytes,
    string DownloadUrl,
    string? Error);

public interface IFileGenerator
{
    Task<FileGenResult> GenerateAsync(string username, FileGenRequest req, CancellationToken ct);
    string GeneratedRoot { get; }

    /// <summary>Resolves a token + filename to an absolute path, scoped under {root}/{user}.</summary>
    string? Resolve(string username, string token, string filename);
}

public sealed class FileGenerator : IFileGenerator
{
    private readonly ILogger<FileGenerator> _log;
    public string GeneratedRoot { get; }
    private readonly string _pythonExe;
    private readonly string _scriptPath;

    public FileGenerator(IConfiguration cfg, ILogger<FileGenerator> log, IHostEnvironment env)
    {
        _log = log;
        GeneratedRoot = cfg["Tools:GeneratedRoot"]
                        ?? Path.Combine(env.ContentRootPath, "generated");
        Directory.CreateDirectory(GeneratedRoot);

        _pythonExe  = cfg["Tools:PythonExe"]  ?? "python3";
        _scriptPath = cfg["Tools:FileGenScript"]
                      ?? Path.Combine(env.ContentRootPath, "scripts", "file-gen.py");
    }

    public async Task<FileGenResult> GenerateAsync(string username, FileGenRequest req, CancellationToken ct)
    {
        // Sanitize filename — no path separators
        var safeName = Path.GetFileName(req.Filename ?? "output").Trim();
        if (string.IsNullOrEmpty(safeName)) safeName = $"output.{req.Kind}";

        var token   = Guid.NewGuid().ToString("N");
        var userDir = Path.Combine(GeneratedRoot, SafeUser(username), token);
        Directory.CreateDirectory(userDir);
        var outPath = Path.Combine(userDir, safeName);

        var payload = JsonSerializer.Serialize(new
        {
            kind     = req.Kind,
            filename = safeName,
            spec     = req.Spec,
        });

        if (!File.Exists(_scriptPath))
            return new(false, req.Kind, safeName, token, 0, "", $"generator script missing: {_scriptPath}");

        var psi = new ProcessStartInfo(_pythonExe)
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add(_scriptPath);
        psi.ArgumentList.Add(outPath);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start python");

        await proc.StandardInput.WriteAsync(payload);
        proc.StandardInput.Close();

        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            _log.LogWarning("file-gen {Kind} failed (exit {Code}): {Err}", req.Kind, proc.ExitCode, stderr);
            string? errMsg = null;
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stdout);
                if (dict != null && dict.TryGetValue("error", out var e)) errMsg = e.GetString();
            } catch { }
            return new(false, req.Kind, safeName, token, 0, "",
                errMsg ?? (string.IsNullOrEmpty(stderr) ? $"exit {proc.ExitCode}" : stderr));
        }

        var info = new FileInfo(outPath);
        if (!info.Exists)
            return new(false, req.Kind, safeName, token, 0, "", "file not produced");

        var url = $"/api/tools/generated/{token}/{Uri.EscapeDataString(safeName)}";
        return new(true, req.Kind, safeName, token, info.Length, url, null);
    }

    public string? Resolve(string username, string token, string filename)
    {
        var safeName = Path.GetFileName(filename ?? "");
        if (string.IsNullOrEmpty(safeName) || string.IsNullOrEmpty(token)) return null;
        var userDir = Path.Combine(GeneratedRoot, SafeUser(username), token);
        var full = Path.GetFullPath(Path.Combine(userDir, safeName));
        if (!full.StartsWith(Path.GetFullPath(userDir), StringComparison.OrdinalIgnoreCase))
            return null;  // path traversal guard
        return File.Exists(full) ? full : null;
    }

    private static string SafeUser(string u) =>
        new(u.Where(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_').Take(64).ToArray());
}

public static class ContentTypes
{
    public static string Lookup(string filename) => Path.GetExtension(filename).ToLowerInvariant() switch
    {
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".pdf"  => "application/pdf",
        ".txt"  => "text/plain; charset=utf-8",
        ".md"   => "text/markdown; charset=utf-8",
        ".csv"  => "text/csv; charset=utf-8",
        _       => "application/octet-stream",
    };
}
