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
    bool IsAdmin(string domain, string username, string password);
    IReadOnlyList<string> GetDomains();
}

/// <summary>AD groups whose members are granted admin access to the LLM platform.</summary>
public static class AdminGroups
{
    public static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "Set Management",
        "Set AIAdmin",
    };
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

    /// <summary>
    /// Checks whether the authenticated user is a member of any admin AD group.
    /// Queries the user's 'memberOf' attribute and matches against AdminGroups.Names.
    /// On bypass mode always returns true so developers can access admin panel.
    /// </summary>
    public bool IsAdmin(string domain, string username, string password)
    {
        if (_opts.Bypass) return true;

        if (!_opts.Domains.TryGetValue(domain.ToUpperInvariant(), out var cfg))
            return false;

        try
        {
            var uri    = new Uri(cfg.Path.Replace("LDAP://", "ldap://", StringComparison.OrdinalIgnoreCase));
            var server = new LdapDirectoryIdentifier(uri.Host, 389);
            var creds  = new NetworkCredential($"{cfg.Domain}\\{username}", password);

            using var conn = new LdapConnection(server, creds, AuthType.Ntlm);
            conn.SessionOptions.ProtocolVersion = 3;
            conn.Bind();

            // Search for the user and retrieve memberOf attribute
            var searchReq = new SearchRequest(
                string.Empty,
                $"(sAMAccountName={username})",
                SearchScope.Subtree,
                "memberOf");

            var resp = (SearchResponse)conn.SendRequest(searchReq);
            if (resp.Entries.Count == 0) return false;

            var entry    = resp.Entries[0];
            var memberOf = entry.Attributes["memberOf"];
            if (memberOf == null) return false;

            foreach (var raw in memberOf.GetValues(typeof(string)))
            {
                var dn = raw?.ToString() ?? "";
                // CN=Set Management,OU=... — extract CN part
                var cn = dn.Split(',').FirstOrDefault(p =>
                    p.TrimStart().StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                    ?.Substring(3).Trim();

                if (cn != null && AdminGroups.Names.Contains(cn))
                {
                    _log.LogInformation("Admin access granted: {User}@{Domain} via group '{Group}'", username, domain, cn);
                    return true;
                }
            }

            _log.LogDebug("Admin access denied: {User}@{Domain} — not in any admin group", username, domain);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "IsAdmin LDAP error for {User}@{Domain}", username, domain);
            return false;
        }
    }
}
