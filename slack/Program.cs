using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

// =============================================================================
// LLM Stack Slack Bot
// Provides:
//   /llm-state                → reports current state
//   /llm-swap reasoning       → triggers swap-to-reasoning.sh
//   /llm-swap default         → triggers swap-to-default.sh
//
// Slack signature verification per:
//   https://api.slack.com/authentication/verifying-requests-from-slack
// =============================================================================

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SlackBotOptions>(builder.Configuration.GetSection("Slack"));
builder.Services.AddSingleton<ISwapExecutor, SwapExecutor>();
builder.Services.AddSingleton<ISlackSignatureVerifier, SlackSignatureVerifier>();
builder.Services.AddLogging();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/slack/commands", async (
    HttpContext ctx,
    [FromServices] ISlackSignatureVerifier verifier,
    [FromServices] ISwapExecutor executor,
    [FromServices] ILogger<Program> log) =>
{
    // 1. Read raw body for signature verification
    ctx.Request.EnableBuffering();
    using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
    var body = await reader.ReadToEndAsync();
    ctx.Request.Body.Position = 0;

    // 2. Verify Slack signature
    var timestamp = ctx.Request.Headers["X-Slack-Request-Timestamp"].FirstOrDefault();
    var signature = ctx.Request.Headers["X-Slack-Signature"].FirstOrDefault();

    if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature))
        return Results.Unauthorized();

    if (!verifier.Verify(timestamp, body, signature))
    {
        log.LogWarning("Invalid Slack signature from {RemoteIp}", ctx.Connection.RemoteIpAddress);
        return Results.Unauthorized();
    }

    // 3. Parse form-urlencoded payload
    var form = await ctx.Request.ReadFormAsync();
    var command = form["command"].FirstOrDefault() ?? "";
    var text = form["text"].FirstOrDefault() ?? "";
    var userName = form["user_name"].FirstOrDefault() ?? "unknown";
    var responseUrl = form["response_url"].FirstOrDefault() ?? "";

    log.LogInformation("Slack command: {Command} '{Text}' by {User}", command, text, userName);

    return command switch
    {
        "/llm-state" => Results.Ok(new
        {
            response_type = "ephemeral",
            text = await executor.GetStateAsync()
        }),

        "/llm-swap" => HandleSwap(text, userName, responseUrl, executor, log),

        _ => Results.Ok(new { response_type = "ephemeral", text = $"Unknown command: {command}" })
    };
});

app.Run();

static IResult HandleSwap(string text, string user, string responseUrl, ISwapExecutor executor, ILogger log)
{
    var target = text.Trim().ToLowerInvariant();
    if (target is not ("reasoning" or "default"))
    {
        return Results.Ok(new
        {
            response_type = "ephemeral",
            text = "Usage: `/llm-swap reasoning` or `/llm-swap default`"
        });
    }

    // Fire-and-forget the long-running swap; return 200 immediately to Slack
    _ = Task.Run(async () =>
    {
        try
        {
            await executor.SwapAsync(target, user, responseUrl);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Swap failed");
        }
    });

    return Results.Ok(new
    {
        response_type = "in_channel",
        text = $":hourglass_flowing_sand: *{user}* started swap to `{target}`. This takes ~5–8 minutes. " +
               "I'll post the result here when complete."
    });
}

// =============================================================================
// Services
// =============================================================================

public sealed class SlackBotOptions
{
    public string SigningSecret { get; set; } = "";
    public string ScriptsDir { get; set; } = "/opt/dgx-spark-llm-stack/scripts";
    public int CommandTimeoutSeconds { get; set; } = 1200; // 20 min cap
}

public interface ISlackSignatureVerifier
{
    bool Verify(string timestamp, string body, string signature);
}

public sealed class SlackSignatureVerifier(IConfiguration cfg, ILogger<SlackSignatureVerifier> log) : ISlackSignatureVerifier
{
    private readonly string _secret = cfg["Slack:SigningSecret"]
        ?? throw new InvalidOperationException("Slack:SigningSecret not configured");

    public bool Verify(string timestamp, string body, string signature)
    {
        // Reject replay attacks > 5 min old
        if (!long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts))
            return false;
        var ageSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts;
        if (Math.Abs(ageSeconds) > 300) { log.LogWarning("Slack request too old: {Age}s", ageSeconds); return false; }

        var basestring = $"v0:{timestamp}:{body}";
        var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(basestring));
        var expected = "v0=" + Convert.ToHexString(hash).ToLowerInvariant();

        // Constant-time comparison
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signature));
    }
}

public interface ISwapExecutor
{
    Task<string> GetStateAsync();
    Task SwapAsync(string target, string user, string responseUrl);
}

public sealed class SwapExecutor(IConfiguration cfg, ILogger<SwapExecutor> log, IHttpClientFactory httpFactory) : ISwapExecutor
{
    private readonly string _scriptsDir = cfg["Slack:ScriptsDir"] ?? "/opt/dgx-spark-llm-stack/scripts";
    private readonly int _timeoutSec = int.TryParse(cfg["Slack:CommandTimeoutSeconds"], out var t) ? t : 1200;

    public async Task<string> GetStateAsync()
    {
        var (exit, stdout, _) = await ExecAsync(Path.Combine(_scriptsDir, "state.sh"), TimeSpan.FromSeconds(15));
        return exit == 0
            ? "```\n" + stdout + "\n```"
            : ":warning: Could not read state. Check container logs.";
    }

    public async Task SwapAsync(string target, string user, string responseUrl)
    {
        var script = target switch
        {
            "reasoning" => "swap-to-reasoning.sh",
            "default"   => "swap-to-default.sh",
            _ => throw new ArgumentException("invalid target", nameof(target))
        };

        var path = Path.Combine(_scriptsDir, script);
        log.LogInformation("Executing {Script} on behalf of {User}", path, user);

        var sw = Stopwatch.StartNew();
        var (exit, stdout, stderr) = await ExecAsync(path, TimeSpan.FromSeconds(_timeoutSec));
        sw.Stop();

        var success = exit == 0;
        var icon = success ? ":white_check_mark:" : ":x:";
        var status = success ? "succeeded" : "failed";

        var msg = $"{icon} *Swap to `{target}` {status}* (initiated by *{user}*)\n" +
                  $"Duration: {sw.Elapsed:mm\\:ss}\n" +
                  $"Exit code: {exit}\n" +
                  (success ? "" : $"```\n{TruncateForSlack(stderr)}\n```");

        await PostToSlack(responseUrl, msg);
    }

    private async Task<(int exit, string stdout, string stderr)> ExecAsync(string path, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi) ?? throw new IOException($"Failed to start {path}");
        using var cts = new CancellationTokenSource(timeout);

        var stdoutTask = p.StandardOutput.ReadToEndAsync(cts.Token).AsTask();
        var stderrTask = p.StandardError.ReadToEndAsync(cts.Token).AsTask();

        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { p.Kill(entireProcessTree: true); throw new TimeoutException($"{path} timed out"); }

        return (p.ExitCode, await stdoutTask, await stderrTask);
    }

    private async Task PostToSlack(string responseUrl, string text)
    {
        if (string.IsNullOrEmpty(responseUrl)) return;
        using var client = httpFactory.CreateClient();
        await client.PostAsJsonAsync(responseUrl, new { response_type = "in_channel", text });
    }

    private static string TruncateForSlack(string s) =>
        s.Length <= 2500 ? s : string.Concat(s.AsSpan(0, 2500), "\n...[truncated]");
}
