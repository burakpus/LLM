using Microsoft.Extensions.Options;
using Novell.Directory.Ldap;

namespace SetYazilim.Llm.Api.Auth;

// ─────────────────────────────────────────────────────────────────────────────
// Options
// ─────────────────────────────────────────────────────────────────────────────

public sealed class LdapOptions
{
    public const string SectionName = "Ldap";

    /// <summary>"SETYAZILIM;SETSOFTWARE" — semicolon-separated domain keys (shown in login UI)</summary>
    public string DomainNames { get; init; } = string.Empty;

    public Dictionary<string, LdapDomainConfig> Domains { get; init; } = new();

    /// <summary>Set true to skip LDAP and accept any credentials (dev/test).</summary>
    public bool Bypass { get; init; } = false;

    /// <summary>Comma-separated AD group CN names whose members get admin access.</summary>
    public string AdminGroups { get; init; } = "setmanagement,Set AIAdmin";

    /// <summary>Comma-separated usernames that always get admin access regardless of group.</summary>
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
    // ── Connection ─────────────────────────────────────────────────────
    /// <summary>LDAP host (e.g. "setyazilim.com" or "172.16.0.5"). Inferred from Path if empty.</summary>
    public string Host { get; init; } = "";

    /// <summary>389 = LDAP, 636 = LDAPS. Defaults from UseSsl/StartTls if 0.</summary>
    public int Port { get; init; } = 0;

    /// <summary>LDAPS (port 636 with SSL from the start).</summary>
    public bool UseSsl { get; init; } = false;

    /// <summary>StartTLS (port 389, upgrade to TLS after connect).</summary>
    public bool StartTls { get; init; } = false;

    /// <summary>Skip server certificate validation (only for self-signed in dev).</summary>
    public bool IgnoreCertErrors { get; init; } = false;

    // ── Directory schema ───────────────────────────────────────────────
    /// <summary>Search base DN (e.g. "DC=setyazilim,DC=com"). Auto-derived from Host if empty.</summary>
    public string BaseDn { get; init; } = "";

    /// <summary>NETBIOS short domain name for `DOMAIN\user` bind (AD only). Legacy: Domain field.</summary>
    public string NetbiosDomain { get; init; } = "";

    /// <summary>UPN suffix for `user@suffix` bind (e.g. "setyazilim.com"). AD-friendly.</summary>
    public string UpnSuffix { get; init; } = "";

    /// <summary>LDAP filter to find user by username — {0} is replaced with the username.</summary>
    public string UserSearchFilter { get; init; } = "(sAMAccountName={0})";

    /// <summary>Attribute holding group memberships (default "memberOf" for AD).</summary>
    public string GroupAttribute { get; init; } = "memberOf";

    // ── Service account (recommended) ──────────────────────────────────
    /// <summary>Service account DN or UPN used to search for users (e.g. "CN=svc-ldap,OU=Users,DC=...")</summary>
    public string ServiceAccountDn { get; init; } = "";

    /// <summary>Password for the service account (encrypted at rest in production deployments).</summary>
    public string ServiceAccountPassword { get; init; } = "";

    // ── Legacy fields (backward compatibility) ─────────────────────────
    /// <summary>e.g. "LDAP://setyazilim.com" — parsed to Host if Host is empty.</summary>
    public string Path { get; init; } = "";

    /// <summary>Legacy NETBIOS domain (e.g. "setyazilim"). Used if NetbiosDomain is empty.</summary>
    public string Domain { get; init; } = "";

    // ── Derived (read-only) helpers ────────────────────────────────────
    public string EffectiveHost => string.IsNullOrEmpty(Host) ? HostFromPath(Path) : Host;
    public int    EffectivePort => Port > 0 ? Port : (UseSsl ? 636 : 389);
    public string EffectiveBaseDn => string.IsNullOrEmpty(BaseDn) ? BaseDnFromHost(EffectiveHost) : BaseDn;
    public string EffectiveNetbios => string.IsNullOrEmpty(NetbiosDomain) ? Domain : NetbiosDomain;

    private static string HostFromPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var lower = path.Replace("LDAP://", "ldap://", StringComparison.OrdinalIgnoreCase)
                        .Replace("LDAPS://", "ldaps://", StringComparison.OrdinalIgnoreCase);
        return new Uri(lower).Host;
    }

    private static string BaseDnFromHost(string host) =>
        string.IsNullOrEmpty(host) ? "" :
        string.Join(",", host.Split('.').Select(p => $"DC={p}"));
}

// ─────────────────────────────────────────────────────────────────────────────
// Diagnostic types
// ─────────────────────────────────────────────────────────────────────────────

public sealed record LdapDiagnosticStep(string Name, bool Ok, string Message, long DurationMs);

public sealed record LdapDiagnosticResult(
    string                       Domain,
    string                       Username,
    bool                         OverallOk,
    string                       Summary,
    IReadOnlyList<LdapDiagnosticStep> Steps);

// ─────────────────────────────────────────────────────────────────────────────
// Service interface
// ─────────────────────────────────────────────────────────────────────────────

public interface ILdapAuthService
{
    bool Authenticate(string domain, string username, string password);
    bool IsAdmin(string domain, string username, string password);
    string[] GetUserGroups(string domain, string username, string password);
    IReadOnlyList<string> GetDomains();
    LdapDiagnosticResult Diagnose(string domain, string username, string password);
}

// ─────────────────────────────────────────────────────────────────────────────
// Implementation
// ─────────────────────────────────────────────────────────────────────────────

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

    // ── Connection factory ─────────────────────────────────────────────
    private LdapConnection CreateConnection(LdapDomainConfig cfg)
    {
        var opts = new LdapConnectionOptions();
        if (cfg.UseSsl) opts.UseSsl();

        if (cfg.IgnoreCertErrors)
        {
            opts.ConfigureRemoteCertificateValidationCallback(
                (sender, cert, chain, errors) => true);
        }

        var conn = new LdapConnection(opts);
        conn.Connect(cfg.EffectiveHost, cfg.EffectivePort);

        if (cfg.StartTls && !cfg.UseSsl)
            conn.StartTls();

        return conn;
    }

    // ── Build the bind DN/name from various AD/OpenLDAP conventions ────
    private static IEnumerable<string> CandidateBindNames(LdapDomainConfig cfg, string username)
    {
        // 1) NETBIOS\user (most common AD form)
        if (!string.IsNullOrEmpty(cfg.EffectiveNetbios))
            yield return $"{cfg.EffectiveNetbios}\\{username}";
        // 2) user@upn (works for AD with UPN suffix configured)
        if (!string.IsNullOrEmpty(cfg.UpnSuffix))
            yield return $"{username}@{cfg.UpnSuffix}";
        // 3) Bare username (some OpenLDAP setups accept this)
        yield return username;
    }

    // ── Search for user DN with service account bind ──────────────────
    private string? FindUserDn(LdapConnection conn, LdapDomainConfig cfg, string username)
    {
        var filter = string.Format(cfg.UserSearchFilter, EscapeFilter(username));
        var results = conn.Search(
            cfg.EffectiveBaseDn,
            LdapConnection.ScopeSub,
            filter,
            new[] { "distinguishedName" },
            typesOnly: false);

        while (results.HasMore())
        {
            LdapEntry? entry = null;
            try { entry = results.Next(); }
            catch (LdapException) { break; }
            if (entry != null) return entry.Dn;
        }
        return null;
    }

    private static string EscapeFilter(string input) =>
        input.Replace("\\", "\\5c")
             .Replace("*",  "\\2a")
             .Replace("(",  "\\28")
             .Replace(")",  "\\29")
             .Replace("\0", "\\00");

    // ── Extract CNs from list of DN strings ───────────────────────────
    private static IEnumerable<string> ExtractCns(IEnumerable<string> dns) =>
        dns.Select(dn =>
                dn.Split(',')
                  .FirstOrDefault(p => p.TrimStart().StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                  ?.Substring(3).Trim() ?? "")
           .Where(cn => !string.IsNullOrEmpty(cn));

    // ─────────────────────────────────────────────────────────────────
    // Authenticate
    // ─────────────────────────────────────────────────────────────────

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

        return TryAuthenticateAndGet(cfg, username, password, out _);
    }

    /// <summary>
    /// Tries all available bind strategies in priority order:
    /// 1) Service account: search for user DN, then bind with that DN
    /// 2) Direct bind: NETBIOS\user, user@upn, bare username
    /// Returns the successful user DN (for further queries) via out param.
    /// </summary>
    private bool TryAuthenticateAndGet(LdapDomainConfig cfg, string username, string password, out string? userDn)
    {
        userDn = null;

        // Strategy 1: Service account search → user bind
        if (!string.IsNullOrEmpty(cfg.ServiceAccountDn))
        {
            try
            {
                using var svc = CreateConnection(cfg);
                svc.Bind(cfg.ServiceAccountDn, cfg.ServiceAccountPassword);
                var dn = FindUserDn(svc, cfg, username);
                if (dn == null)
                {
                    _log.LogWarning("LDAP: user '{User}' not found via service account search", username);
                    return false;
                }
                using var userConn = CreateConnection(cfg);
                userConn.Bind(dn, password);
                userDn = dn;
                _log.LogInformation("LDAP auth OK (svc-account) for {User} → {Dn}", username, dn);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning("LDAP svc-account auth failed for {User}: {Msg}", username, ex.Message);
                // Fall through to direct bind attempts
            }
        }

        // Strategy 2: Direct bind attempts
        foreach (var bindName in CandidateBindNames(cfg, username))
        {
            try
            {
                using var conn = CreateConnection(cfg);
                conn.Bind(bindName, password);
                _log.LogInformation("LDAP auth OK (direct '{Name}') for {User}", bindName, username);
                userDn = bindName;
                return true;
            }
            catch (LdapException ex)
            {
                _log.LogDebug("LDAP direct bind failed for {Name}: {Msg}", bindName, ex.Message);
            }
            catch (Exception ex)
            {
                _log.LogWarning("LDAP direct bind unexpected error for {Name}: {Msg}", bindName, ex.Message);
            }
        }

        _log.LogWarning("LDAP auth failed for {User}@{Domain} — all bind strategies exhausted",
            username, cfg.EffectiveNetbios);
        return false;
    }

    // ─────────────────────────────────────────────────────────────────
    // Group lookup
    // ─────────────────────────────────────────────────────────────────

    public string[] GetUserGroups(string domain, string username, string password)
    {
        if (_opts.Bypass)
        {
            return _opts.AdminUserSet.Contains(username)
                ? _opts.AdminGroupSet.ToArray()
                : [];
        }
        if (!_opts.Domains.TryGetValue(domain.ToUpperInvariant(), out var cfg))
            return [];

        try
        {
            // Prefer service account for group queries (more reliable than user-bound query)
            if (!string.IsNullOrEmpty(cfg.ServiceAccountDn))
            {
                using var svc = CreateConnection(cfg);
                svc.Bind(cfg.ServiceAccountDn, cfg.ServiceAccountPassword);
                return ReadGroupsFor(svc, cfg, username);
            }

            // Fallback: bind as user, then query
            if (!TryAuthenticateAndGet(cfg, username, password, out var _))
                return [];

            using var conn = CreateConnection(cfg);
            // Re-bind as user (TryAuthenticateAndGet already validated, but conn was disposed)
            foreach (var bn in CandidateBindNames(cfg, username))
            {
                try { conn.Bind(bn, password); break; }
                catch { /* try next */ }
            }
            return ReadGroupsFor(conn, cfg, username);
        }
        catch (Exception ex)
        {
            _log.LogWarning("GetUserGroups error for {User}@{Domain}: {Msg}", username, domain, ex.Message);
            return [];
        }
    }

    private string[] ReadGroupsFor(LdapConnection conn, LdapDomainConfig cfg, string username)
    {
        var filter = string.Format(cfg.UserSearchFilter, EscapeFilter(username));
        var results = conn.Search(
            cfg.EffectiveBaseDn,
            LdapConnection.ScopeSub,
            filter,
            new[] { cfg.GroupAttribute },
            typesOnly: false);

        var memberOf = new List<string>();
        while (results.HasMore())
        {
            LdapEntry? entry = null;
            try { entry = results.Next(); }
            catch (LdapException) { break; }
            if (entry == null) continue;

            var attr = entry.GetAttributeSet().GetAttribute(cfg.GroupAttribute);
            if (attr != null) memberOf.AddRange(attr.StringValueArray);
        }
        return ExtractCns(memberOf).ToArray();
    }

    // ─────────────────────────────────────────────────────────────────
    // Admin check
    // ─────────────────────────────────────────────────────────────────

    public bool IsAdmin(string domain, string username, string password)
    {
        if (_opts.AdminUserSet.Contains(username))
        {
            _log.LogInformation("Admin granted to {User} via AdminUsers config", username);
            return true;
        }
        if (_opts.Bypass) return false;

        var groups = GetUserGroups(domain, username, password);
        foreach (var g in groups)
        {
            if (_opts.AdminGroupSet.Contains(g))
            {
                _log.LogInformation("Admin granted to {User} via group '{Group}'", username, g);
                return true;
            }
        }
        return false;
    }

    // ─────────────────────────────────────────────────────────────────
    // Diagnose — step-by-step LDAP health check
    // ─────────────────────────────────────────────────────────────────

    public LdapDiagnosticResult Diagnose(string domain, string username, string password)
    {
        var steps = new List<LdapDiagnosticStep>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        void Add(string name, Action body, string okMsg)
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                body();
                steps.Add(new(name, true, okMsg, t.ElapsedMilliseconds));
            }
            catch (Exception ex)
            {
                steps.Add(new(name, false, $"{ex.GetType().Name}: {ex.Message}", t.ElapsedMilliseconds));
                throw;
            }
        }

        try
        {
            if (_opts.Bypass)
            {
                steps.Add(new("bypass-mode", true, "Ldap:Bypass=true — LDAP atlanır, tüm credential'lar kabul edilir", 0));
                return new(domain, username, true, "Bypass active", steps);
            }

            if (!_opts.Domains.TryGetValue(domain.ToUpperInvariant(), out var cfg))
            {
                steps.Add(new("config-lookup", false, $"Domain '{domain}' yapılandırılmamış. Mevcut: {string.Join(",", _opts.Domains.Keys)}", 0));
                return new(domain, username, false, "Domain not configured", steps);
            }

            steps.Add(new("config-lookup", true,
                $"Host={cfg.EffectiveHost} Port={cfg.EffectivePort} SSL={cfg.UseSsl} StartTLS={cfg.StartTls} BaseDn={cfg.EffectiveBaseDn}",
                0));

            // Step: connect
            LdapConnection? conn = null;
            try { Add("connect", () => { conn = CreateConnection(cfg); }, "Bağlantı + TLS başarılı"); }
            catch { return Done(false, "Connection/TLS failed"); }

            // Step: service account bind (if configured)
            if (!string.IsNullOrEmpty(cfg.ServiceAccountDn))
            {
                try { Add("svc-account-bind", () => conn!.Bind(cfg.ServiceAccountDn, cfg.ServiceAccountPassword),
                    $"Service account bind OK: {cfg.ServiceAccountDn}"); }
                catch { return Done(false, "Service account bind failed"); }

                // Step: user search via service account
                string? userDn = null;
                try { Add("user-search", () => { userDn = FindUserDn(conn!, cfg, username); if (userDn == null) throw new Exception("user not found"); },
                    $"Kullanıcı DN bulundu"); }
                catch { return Done(false, "User search failed"); }
                steps[^1] = steps[^1] with { Message = $"DN={userDn}" };

                // Step: bind as user (with their password) to verify credentials
                if (!string.IsNullOrWhiteSpace(password))
                {
                    try
                    {
                        Add("user-bind", () =>
                        {
                            using var userConn = CreateConnection(cfg);
                            userConn.Bind(userDn!, password);
                        }, "Kullanıcı parolası doğrulandı");
                    }
                    catch { return Done(false, "User password bind failed"); }
                }
                else
                {
                    steps.Add(new("user-bind", false, "Parola verilmedi — atlandı", 0));
                }

                // Step: group fetch
                try
                {
                    string[] groups = [];
                    Add("group-fetch", () => { groups = ReadGroupsFor(conn!, cfg, username); }, "");
                    steps[^1] = steps[^1] with { Message = $"Gruplar: {(groups.Length > 0 ? string.Join(", ", groups) : "(yok)")}" };
                }
                catch { /* group fetch failure is non-fatal */ }
            }
            else
            {
                // No service account — try direct user bind
                if (string.IsNullOrWhiteSpace(password))
                {
                    steps.Add(new("user-bind", false, "Service account yok ve parola verilmedi — bind atlandı", 0));
                    return Done(false, "Cannot validate without service account or user password");
                }
                var bound = false;
                foreach (var bindName in CandidateBindNames(cfg, username))
                {
                    try
                    {
                        var t = System.Diagnostics.Stopwatch.StartNew();
                        using var userConn = CreateConnection(cfg);
                        userConn.Bind(bindName, password);
                        steps.Add(new($"user-bind ({bindName})", true, $"Bind OK", t.ElapsedMilliseconds));
                        bound = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        steps.Add(new($"user-bind ({bindName})", false, ex.Message, 0));
                    }
                }
                if (!bound) return Done(false, "All direct binds failed");

                // Group fetch as user
                try
                {
                    var t = System.Diagnostics.Stopwatch.StartNew();
                    using var groupConn = CreateConnection(cfg);
                    foreach (var bn in CandidateBindNames(cfg, username))
                    {
                        try { groupConn.Bind(bn, password); break; } catch { }
                    }
                    var groups = ReadGroupsFor(groupConn, cfg, username);
                    steps.Add(new("group-fetch", true, $"Gruplar: {(groups.Length > 0 ? string.Join(", ", groups) : "(yok)")}", t.ElapsedMilliseconds));
                }
                catch (Exception ex)
                {
                    steps.Add(new("group-fetch", false, ex.Message, 0));
                }
            }

            return Done(true, "All checks passed");

            LdapDiagnosticResult Done(bool ok, string summary)
            {
                conn?.Disconnect();
                return new(domain, username, ok, summary, steps);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Diagnose unexpected error");
            steps.Add(new("unexpected", false, ex.Message, 0));
            return new(domain, username, false, ex.Message, steps);
        }
    }
}
