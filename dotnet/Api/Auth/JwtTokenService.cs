using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace SetYazilim.Llm.Api.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Secret  { get; init; } = string.Empty;
    public string Issuer  { get; init; } = "set-llm-api";
    public string Audience { get; init; } = "set-llm-ui";
    public int    ExpiryHours { get; init; } = 8;
}

public sealed class TokenResult
{
    public required string Token     { get; init; }
    public required string Username  { get; init; }
    public required string Domain    { get; init; }
    public required long   ExpiresAt { get; init; }  // unix epoch
}

public interface IJwtTokenService
{
    TokenResult Generate(string username, string domain, bool isAdmin = false);
    ClaimsPrincipal? Validate(string token);
}

public static class AppClaims
{
    public const string IsAdmin = "isAdmin";
}

public sealed class JwtTokenService : IJwtTokenService
{
    private readonly JwtOptions _opts;
    private readonly SymmetricSecurityKey _key;

    public JwtTokenService(IOptions<JwtOptions> opts)
    {
        _opts = opts.Value;
        _key  = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opts.Secret));
    }

    public TokenResult Generate(string username, string domain, bool isAdmin = false)
    {
        var expiry = DateTime.UtcNow.AddHours(_opts.ExpiryHours);
        var claims = new[]
        {
            new Claim(ClaimTypes.Name,            username),
            new Claim("domain",                   domain),
            new Claim(ClaimTypes.NameIdentifier,  $"{domain}\\{username}"),
            new Claim(AppClaims.IsAdmin,           isAdmin ? "true" : "false"),
        };

        var token = new JwtSecurityToken(
            issuer:             _opts.Issuer,
            audience:           _opts.Audience,
            claims:             claims,
            notBefore:          DateTime.UtcNow,
            expires:            expiry,
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));

        return new TokenResult
        {
            Token     = new JwtSecurityTokenHandler().WriteToken(token),
            Username  = username,
            Domain    = domain,
            ExpiresAt = new DateTimeOffset(expiry).ToUnixTimeSeconds()
        };
    }

    public ClaimsPrincipal? Validate(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            return handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer           = true,
                ValidIssuer              = _opts.Issuer,
                ValidateAudience         = true,
                ValidAudience            = _opts.Audience,
                ValidateLifetime         = true,
                IssuerSigningKey         = _key,
                ClockSkew                = TimeSpan.FromMinutes(2)
            }, out _);
        }
        catch { return null; }
    }
}
