using Microsoft.Extensions.Options;
using Novell.Directory.Ldap;

namespace SetYazilim.Llm.Api.Auth;

public sealed class LdapOptions
{
    public const string SectionName = "Ldap";

    /// <summary>"SETYAZILIM;SETSOFTWARE" — semicolon-separated domain keys</summary>
    public string DomainNames { get; init; } = string.Empty;

    public Dictionary<string, LdapDomainConfig> Domains { get; init; } = new();

    /// <summary>Set true to skip LDAP and accept any credentials (dev/test).</summary>
    public bool Bypass { get; init; } = false;

    /// <summary>
    /// Comma-separated AD group names whose members get admin access.
    /// Example: "setmanagement,Set AIAdmin"
    /// </summary>
    public string AdminGroups { get; init; } = "setmanagement,Set AIAdmin";

    /// <summary>
    /// Comma-separated usernames that always get admin access regardless of group.
    /// Fallback for when LDAP group lookup fails or user is not in a group.
    /// Example: "burakpus,admin"
    /// </summary>
    public string AdminUsers { get; init; } = "";

    public IReadOnlyList<string> DomainList =>
        DomainNames.Split(';', StringSplitOptions.RemoveEmptyEntries);

    public IReadOnlySet<string> AdminGroupSet =>
        AdminGroups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> AdminUserSet =>
        AdminUsers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
    /// <summary>Returns all AD group CN names the user belongs to (empty on error/bypass).</summary>
    string[] GetUserGroups(string domain, string username, string password);
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

    // Extracts the host from "LDAP://setyazilim.com" → "setyazilim.com"
    private static string HostFromPath(string path) =>
        new Uri(path.Replace("LDAP://", "ldap://", StringComparison.OrdinalIgnoreCase)).Host;

    // Builds base DN from host: "setyazilim.com" → "DC=setyazilim,DC=com"
    private static string BaseDnFromHost(string host) =>
        string.Join(",", host.Split('.').Select(p => $"DC={p}"));

    // Extracts CN names from a list of DN strings like "CN=setmanagement,OU=..."
    private static IEnumerable<string> ExtractCns(IEnumerable<string> dns) =>
        dns.Select(dn =>
                dn.Split(',')
                  .FirstOrDefault(p => p.TrimStart().StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                  ?.Substring(3).Trim() ?? "")
           .Where(cn => !string.IsNullOrEmpty(cn));

    /// <summary>Returns all AD group CN names (e.g. "setmanagement") for the user.</summary>
    public string[] GetUserGroups(string domain, string username, string password)
    {
        if (_opts.Bypass) return [];

        if (!_opts.Domains.TryGetValue(domain.ToUpperInvariant(), out var cfg))
            return [];

        try
        {
            var host   = HostFromPath(cfg.Path);
            var baseDn = BaseDnFromHost(host);

            using var conn = new LdapConnection();
            conn.Connect(host, 389);
            conn.Bind($"{cfg.Domain}\\{username}", password);

            var results = conn.Search(baseDn, LdapConnection.ScopeSub,
                $"(sAMAccountName={username})", new[] { "memberOf" }, false);

            var memberOfValues = new List<string>();
            while (results.HasMore())
            {
                LdapEntry entry;
                try { entry = results.Next(); }
                catch (LdapException) { break; }

                var attr = entry.GetAttributeSet().GetAttribute("memberOf");
                if (attr != null)
                    memberOfValues.AddRange(attr.StringValueArray);
            }

            conn.Disconnect();
            return ExtractCns(memberOfValues).ToArray();
        }
        catch (Exception ex)
        {
            _log.LogWarning("GetUserGroups failed for {User}@{Domain}: {Msg}", username, domain, ex.Message);
            return [];
        }
    }

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
            var host = HostFromPath(cfg.Path);

            using var conn = new LdapConnection();
            conn.Connect(host, 389);
            conn.Bind($"{cfg.Domain}\\{username}", password);
            conn.Disconnect();

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
    /// Queries the user's 'memberOf' attribute and matches against Ldap:AdminGroups config.
    /// On bypass mode always returns false so only AdminUsers have admin access.
    /// </summary>
    public bool IsAdmin(string domain, string username, string password)
    {
        // Direct username override — always checked first, even in bypass mode
        if (_opts.AdminUserSet.Contains(username))
        {
            _log.LogInformation("Admin access granted: {User}@{Domain} via AdminUsers config", username, domain);
            return true;
        }

        // In bypass mode, skip LDAP group lookup (LDAP not available)
        if (_opts.Bypass)
        {
            _log.LogDebug("LDAP bypass — skipping group lookup for {User}@{Domain}", username, domain);
            return false;
        }

        if (!_opts.Domains.TryGetValue(domain.ToUpperInvariant(), out var cfg))
            return false;

        try
        {
            var host   = HostFromPath(cfg.Path);
            var baseDn = BaseDnFromHost(host);

            using var conn = new LdapConnection();
            conn.Connect(host, 389);
            conn.Bind($"{cfg.Domain}\\{username}", password);

            // Search for the user and retrieve memberOf attribute
            var results = conn.Search(baseDn, LdapConnection.ScopeSub,
                $"(sAMAccountName={username})", new[] { "memberOf" }, false);

            bool foundUser = false;
            while (results.HasMore())
            {
                LdapEntry entry;
                try { entry = results.Next(); }
                catch (LdapException) { break; }

                foundUser = true;
                var attr = entry.GetAttributeSet().GetAttribute("memberOf");
                if (attr == null)
                {
                    _log.LogWarning("IsAdmin: user {User}@{Domain} has no memberOf attribute", username, domain);
                    continue;
                }

                foreach (var dn in attr.StringValueArray)
                {
                    // CN=setmanagement,OU=... — extract CN part
                    var cn = dn.Split(',').FirstOrDefault(p =>
                        p.TrimStart().StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                        ?.Substring(3).Trim();

                    _log.LogDebug("IsAdmin: {User}@{Domain} memberOf CN='{Cn}'", username, domain, cn);

                    if (cn != null && _opts.AdminGroupSet.Contains(cn))
                    {
                        _log.LogInformation("Admin access granted: {User}@{Domain} via group '{Group}'", username, domain, cn);
                        conn.Disconnect();
                        return true;
                    }
                }
            }

            conn.Disconnect();

            if (!foundUser)
                _log.LogWarning("IsAdmin: user {User}@{Domain} not found in LDAP (baseDn={BaseDn})", username, domain, baseDn);
            else
                _log.LogWarning("Admin access denied: {User}@{Domain} — not in any admin group. AdminGroups={Groups}",
                    username, domain, _opts.AdminGroups);

            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "IsAdmin LDAP error for {User}@{Domain}", username, domain);
            return false;
        }
    }
}
