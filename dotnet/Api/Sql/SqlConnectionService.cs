using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace SetYazilim.Llm.Api.Sql;

public enum DbType
{
    MsSql,
    Postgres,
    MySql,
    Oracle,
}

public sealed record SqlConnectionEntry(
    int      Id,
    string   Name,
    DbType   DbType,
    string   Host,
    int      Port,
    string   Database,
    string   Username,
    string   CreatedBy,
    DateTime CreatedAt);

public interface ISqlConnectionService
{
    /// <summary>Encrypts plaintext password for DB storage.</summary>
    string Encrypt(string plain);

    /// <summary>Decrypts password retrieved from DB.</summary>
    string Decrypt(string cipher);

    /// <summary>Builds an OLEDB-style connection string for the chosen DB type.</summary>
    string BuildConnectionString(DbType dbType, string host, int port, string database, string username, string plainPassword);

    /// <summary>Returns null on success, or an error message on failure.</summary>
    Task<string?> TestConnectionAsync(DbType dbType, string host, int port, string database, string username, string plainPassword, CancellationToken ct);

    /// <summary>Default port for a DB type (used when user leaves it blank).</summary>
    int DefaultPort(DbType dbType);
}

public sealed class SqlConnectionService : ISqlConnectionService
{
    private readonly IDataProtector _protector;
    private readonly ILogger<SqlConnectionService> _log;

    public SqlConnectionService(IDataProtectionProvider provider, ILogger<SqlConnectionService> log)
    {
        // Namespaced protector — keys are managed by ASP.NET Core DataProtection (machine-bound)
        _protector = provider.CreateProtector("SetYazilim.Llm.Api.SqlConnection.v1");
        _log       = log;
    }

    public string Encrypt(string plain) =>
        string.IsNullOrEmpty(plain) ? "" : _protector.Protect(plain);

    public string Decrypt(string cipher) =>
        string.IsNullOrEmpty(cipher) ? "" : _protector.Unprotect(cipher);

    public int DefaultPort(DbType dbType) => dbType switch
    {
        DbType.MsSql    => 1433,
        DbType.Postgres => 5432,
        DbType.MySql    => 3306,
        DbType.Oracle   => 1521,
        _               => 0,
    };

    public string BuildConnectionString(DbType dbType, string host, int port, string database, string username, string password)
    {
        return dbType switch
        {
            DbType.MsSql    => new SqlConnectionStringBuilder
            {
                DataSource              = port == 1433 || port == 0 ? host : $"{host},{port}",
                InitialCatalog          = database,
                UserID                  = username,
                Password                = password,
                TrustServerCertificate  = true,
                ConnectTimeout          = 10,
            }.ToString(),

            DbType.Postgres => new NpgsqlConnectionStringBuilder
            {
                Host             = host,
                Port             = port == 0 ? 5432 : port,
                Database         = database,
                Username         = username,
                Password         = password,
                Timeout          = 10,
                CommandTimeout   = 30,
            }.ToString(),

            DbType.MySql    => new MySqlConnectionStringBuilder
            {
                Server           = host,
                Port             = port == 0 ? 3306U : (uint)port,
                Database         = database,
                UserID           = username,
                Password         = password,
                ConnectionTimeout = 10,
                DefaultCommandTimeout = 30,
            }.ToString(),

            DbType.Oracle   => new OracleConnectionStringBuilder
            {
                DataSource       = $"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={host})(PORT={(port == 0 ? 1521 : port)}))(CONNECT_DATA=(SERVICE_NAME={database})))",
                UserID           = username,
                Password         = password,
                ConnectionTimeout = 10,
            }.ToString(),

            _ => throw new ArgumentOutOfRangeException(nameof(dbType)),
        };
    }

    public async Task<string?> TestConnectionAsync(DbType dbType, string host, int port, string database, string username, string password, CancellationToken ct)
    {
        try
        {
            var connStr = BuildConnectionString(dbType, host, port, database, username, password);
            switch (dbType)
            {
                case DbType.MsSql:
                {
                    await using var c = new SqlConnection(connStr);
                    await c.OpenAsync(ct);
                    return null;
                }
                case DbType.Postgres:
                {
                    await using var c = new NpgsqlConnection(connStr);
                    await c.OpenAsync(ct);
                    return null;
                }
                case DbType.MySql:
                {
                    await using var c = new MySqlConnection(connStr);
                    await c.OpenAsync(ct);
                    return null;
                }
                case DbType.Oracle:
                {
                    await using var c = new OracleConnection(connStr);
                    await c.OpenAsync(ct);
                    return null;
                }
                default:
                    return $"Unsupported DB type: {dbType}";
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("SQL test connection failed ({DbType} {Host}:{Port}/{Db}): {Msg}",
                dbType, host, port, database, ex.Message);
            return ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
        }
    }
}
