using System.Diagnostics;

namespace SetYazilim.Llm.Api.Admin;

/// <summary>
/// Optional OCR/parsing pipeline using the <see href="https://github.com/run-llama/liteparse">LiteParse</see>
/// CLI as a subprocess. Used as a fallback when PdfPig produces empty/near-empty
/// text (typically scanned PDFs), and as the primary parser for image inputs.
///
/// Deployment: the <c>lit</c> binary + Tesseract tessdata files (tur, eng) must be
/// installed on the server. Configure path via <c>LiteParse:BinaryPath</c> in
/// appsettings.json. The class checks binary availability on first call and
/// disables itself silently if missing — so PdfPig-only deployments keep working.
/// </summary>
public sealed class LiteParseInvoker
{
    private readonly ILogger<LiteParseInvoker> _log;
    private readonly LiteParseOptions          _opts;
    private bool?   _binaryAvailable;          // null = not checked yet
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public LiteParseInvoker(IConfiguration cfg, ILogger<LiteParseInvoker> log)
    {
        _log  = log;
        _opts = new LiteParseOptions
        {
            Enabled      = cfg.GetValue<bool?>("LiteParse:Enabled")        ?? true,
            BinaryPath   = cfg["LiteParse:BinaryPath"]                     ?? "lit",
            LangPack     = cfg["LiteParse:LangPack"]                       ?? "tur+eng",
            TessdataPath = cfg["LiteParse:TessdataPath"],   // optional
            TimeoutSec   = cfg.GetValue<int?>("LiteParse:TimeoutSec")      ?? 30,
        };
    }

    public LiteParseOptions Options => _opts;

    /// <summary>
    /// Probe the binary once. Caches the result. Returns false if disabled in config
    /// or the binary can't be invoked.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!_opts.Enabled) return false;
        if (_binaryAvailable.HasValue) return _binaryAvailable.Value;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_binaryAvailable.HasValue) return _binaryAvailable.Value;
            try
            {
                var psi = new ProcessStartInfo(_opts.BinaryPath, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc is null) { _binaryAvailable = false; return false; }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                await proc.WaitForExitAsync(cts.Token);
                _binaryAvailable = proc.ExitCode == 0;
                if (_binaryAvailable.Value)
                    _log.LogInformation("LiteParse available at {Path}", _opts.BinaryPath);
                else
                    _log.LogInformation("LiteParse binary at {Path} returned exit {Code}, disabling", _opts.BinaryPath, proc.ExitCode);
                return _binaryAvailable.Value;
            }
            catch (Exception ex)
            {
                _log.LogInformation("LiteParse not available ({Path}): {Msg} — falling back to native parsers", _opts.BinaryPath, ex.Message);
                _binaryAvailable = false;
                return false;
            }
        }
        finally { _initLock.Release(); }
    }

    /// <summary>
    /// Pipe document bytes into <c>lit parse --format text</c> via stdin, return extracted text.
    /// Returns null on failure. Caller should fall back to native parsing.
    /// </summary>
    public async Task<string?> ParseAsync(byte[] data, string? hintFilename, CancellationToken ct = default)
    {
        if (!await IsAvailableAsync(ct)) return null;
        if (data is null || data.Length == 0) return string.Empty;

        try
        {
            // lit parse --format text --ocr-language tur+eng -
            // (Trailing '-' tells lit to read from stdin.)
            var psi = new ProcessStartInfo(_opts.BinaryPath)
            {
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("parse");
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add("text");
            psi.ArgumentList.Add("--ocr-language");
            psi.ArgumentList.Add(_opts.LangPack);
            if (!string.IsNullOrEmpty(_opts.TessdataPath))
            {
                psi.ArgumentList.Add("--tessdata-path");
                psi.ArgumentList.Add(_opts.TessdataPath);
            }
            psi.ArgumentList.Add("-");   // stdin sentinel

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start lit");

            using var stdinStream = proc.StandardInput.BaseStream;
            await stdinStream.WriteAsync(data, ct);
            stdinStream.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_opts.TimeoutSec));

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (proc.ExitCode != 0)
            {
                _log.LogWarning("lit parse failed for {File} (exit {Code}): {Err}",
                    hintFilename ?? "(stream)", proc.ExitCode, stderr[..Math.Min(300, stderr.Length)]);
                return null;
            }
            return stdout;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("lit parse timed out after {Sec}s for {File}", _opts.TimeoutSec, hintFilename ?? "(stream)");
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "lit parse exception for {File}", hintFilename ?? "(stream)");
            return null;
        }
    }
}

public sealed class LiteParseOptions
{
    public bool    Enabled      { get; set; } = true;
    public string  BinaryPath   { get; set; } = "lit";
    public string  LangPack     { get; set; } = "tur+eng";
    public string? TessdataPath { get; set; }
    public int     TimeoutSec   { get; set; } = 30;
}
