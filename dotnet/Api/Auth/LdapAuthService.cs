using System.DirectoryServices.Protocols;
using System.Net;
using Microsoft.Extensions.Options;

namespace SetYazilim.Llm.Api.Auth;

public sealed class LdapOptions
{
    public const string SectionName = "Ldap";

    /// <summary>"SETYAZILIM;SETSOFTWARE" — semicolon-separated domain keys</summary>
    public string DomainNames { get; init; } = string.Empty;

    public Dictionary<string, LdapDomainConfig> Domains { get; init; } = new();

    /// <summary>Set true to skip LDAP and accept any credentials (dev/test).</summary>
    public bool Bypass { get; init; } = false;

    public IReadOnlyList<string> DomainList =>
        DomainNames.Split(';', StringSplitOptions.RemoveEmptyEntries);
}

public sealed class LdapDomainConfig
{
    public string Path   { get; init; } = string.Empty;   // e.g. LDAP://setyazilim.com
    public string Domain { get; init; } = string.Empty;   // e.g. setyazilim
}

public interface ILdapAuthService
{
    bool Authenticate(string domain, string username, string password);
    IReadOnlyList<string> GetDomains();
}

public sealed class LdapAuthService : ILdapAuthService
{
    private readonly LdapOptions _opts;
    private readonly ILogger<LdapAuthService> _log;

    public LdapAuthService(IOptions<LdapOptions> opts, ILogger<LdapAuthService> log)
    {
        _opts = opts.Value;
        _log  = log;
    }

    public IReadOnlyList<string> GetDomains() => _opts.DomainList;

    public bool Authenticate(string domain, string username, string password)
    {
        if (_opts.Bypass)
        {
            _log.LogWarning("LDAP bypass enabled — accepting credentials for {User}@{Domain}", username, domain);
            return true;
        }

        if (!_opts.Domains.TryGetValue(domain.ToUpperInvariant(), out var cfg))
        {
            _log.LogWarning("LDAP domain not configured: {Domain}", domain);
            return false;
        }

        try
        {
            // Parse host from LDAP://host path
            var uri    = new Uri(cfg.Path.Replace("LDAP://", "ldap://", StringComparison.OrdinalIgnoreCase));
            var server = new LdapDirectoryIdentifier(uri.Host, 389);
            var creds  = new NetworkCredential($"{cfg.Domain}\\{username}", password);

            using var conn = new LdapConnection(server, creds, AuthType.Ntlm);
            conn.SessionOptions.ProtocolVersion = 3;
            conn.Bind();
            _log.LogInformation("LDAP auth succeeded: {User}@{Domain}", username, domain);
            return true;
        }
        catch (LdapException ex)
        {
            _log.LogWarning("LDAP auth failed for {User}@{Domain}: {Msg}", username, domain, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "LDAP unexpected error for {User}@{Domain}", username, domain);
            return false;
        }
    }
}
