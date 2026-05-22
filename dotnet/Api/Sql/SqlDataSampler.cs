using System.Data;
using System.Data.Common;
using System.Text;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace SetYazilim.Llm.Api.Sql;

public sealed record TableColumn(string Name, string DataType, bool IsPII);

public sealed record TableInfo(
    string             Schema,
    string             Name,
    long               EstimatedRows,
    List<TableColumn>  Columns)
{
    public string QualifiedName => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
}

public sealed record TableSampleRequest(
    string  Schema,
    string  Name,
    int     Limit,
    string? Where);

public static class SqlDataSampler
{
    // ── PII detection (column name heuristics) ────────────────────────────────
    static readonly string[] PiiHints = [
        "password", "passwd", "secret", "token", "api_key", "apikey",
        "tc_kimlik", "tckn", "ssn", "national_id", "kimlik_no",
        "iban", "credit_card", "card_no", "kart_no", "cvv",
        "email", "e_posta", "phone", "telefon", "mobile", "gsm",
    ];

    public static bool IsLikelyPII(string columnName)
    {
        var n = columnName.ToLowerInvariant();
        return PiiHints.Any(h => n.Contains(h));
    }

    // ── List tables (with row count + columns) ────────────────────────────────
    public static async Task<List<TableInfo>> ListTablesAsync(
        DbType dbType, string connStr, CancellationToken ct)
    {
        return dbType switch
        {
            DbType.MsSql    => await ListMsSqlAsync(connStr, ct),
            DbType.Postgres => await ListPostgresAsync(connStr, ct),
            DbType.MySql    => await ListMySqlAsync(connStr, ct),
            DbType.Oracle   => await ListOracleAsync(connStr, ct),
            _ => new()
        };
    }

    // ── Sample data from a table ──────────────────────────────────────────────
    public static async Task<(List<string> Cols, List<List<string>> Rows)> SampleAsync(
        DbType dbType, string connStr, TableSampleRequest req, CancellationToken ct)
    {
        var schema   = req.Schema;
        var table    = req.Name;
        var limit    = Math.Max(1, Math.Min(req.Limit, 10_000));
        var whereSql = string.IsNullOrWhiteSpace(req.Where) ? "" : $" WHERE {req.Where}";

        var sql = dbType switch
        {
            DbType.MsSql    => $"SELECT TOP {limit} * FROM [{schema}].[{table}]{whereSql}",
            DbType.Postgres => $"SELECT * FROM \"{schema}\".\"{table}\"{whereSql} LIMIT {limit}",
            DbType.MySql    => $"SELECT * FROM `{schema}`.`{table}`{whereSql} LIMIT {limit}",
            DbType.Oracle   => $"SELECT * FROM \"{schema}\".\"{table}\"{whereSql} FETCH FIRST {limit} ROWS ONLY",
            _ => throw new ArgumentOutOfRangeException(nameof(dbType)),
        };

        await using var conn = CreateConnection(dbType, connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = 60;

        await using var r = await cmd.ExecuteReaderAsync(ct);
        var cols = new List<string>();
        for (int i = 0; i < r.FieldCount; i++) cols.Add(r.GetName(i));

        var rows = new List<List<string>>();
        while (await r.ReadAsync(ct))
        {
            var row = new List<string>(r.FieldCount);
            for (int i = 0; i < r.FieldCount; i++)
            {
                var val = r.IsDBNull(i) ? "NULL" : r.GetValue(i)?.ToString() ?? "";
                if (IsLikelyPII(cols[i]) && val != "NULL")
                    val = MaskValue(cols[i], val);
                if (val.Length > 200) val = val[..200] + "…";
                row.Add(val);
            }
            rows.Add(row);
        }
        return (cols, rows);
    }

    // ── Format as markdown table ──────────────────────────────────────────────
    public static string FormatAsMarkdown(string schema, string table, List<string> cols, List<List<string>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## {schema}.{table}");
        sb.AppendLine();
        sb.Append("| ").AppendJoin(" | ", cols.Select(EscapeMd)).AppendLine(" |");
        sb.Append("| ").AppendJoin(" | ", cols.Select(_ => "---")).AppendLine(" |");
        foreach (var row in rows)
            sb.Append("| ").AppendJoin(" | ", row.Select(EscapeMd)).AppendLine(" |");
        return sb.ToString();
    }

    static string EscapeMd(string s) =>
        s.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");

    static string MaskValue(string colName, string val)
    {
        var n = colName.ToLowerInvariant();
        if (n.Contains("password") || n.Contains("secret") || n.Contains("token") || n.Contains("api_key") || n.Contains("apikey"))
            return "***";
        if (n.Contains("email") || n.Contains("e_posta"))
        {
            var at = val.IndexOf('@');
            return at > 1 ? $"{val[0]}***{val[at..]}" : "***";
        }
        if (n.Contains("phone") || n.Contains("telefon") || n.Contains("mobile") || n.Contains("gsm"))
            return val.Length > 4 ? new string('*', val.Length - 4) + val[^4..] : "***";
        // Default: show first 2 + last 2
        return val.Length > 6 ? $"{val[..2]}***{val[^2..]}" : "***";
    }

    static DbConnection CreateConnection(DbType t, string s) => t switch
    {
        DbType.MsSql    => new SqlConnection(s),
        DbType.Postgres => new NpgsqlConnection(s),
        DbType.MySql    => new MySqlConnection(s),
        DbType.Oracle   => new OracleConnection(s),
        _ => throw new ArgumentOutOfRangeException(),
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Per-DB listers
    // ─────────────────────────────────────────────────────────────────────────

    static async Task<List<TableInfo>> ListMsSqlAsync(string connStr, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);
        var tables = new Dictionary<string, TableInfo>();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT s.name AS schema_name, t.name AS table_name,
                       SUM(p.rows) AS row_count
                FROM sys.tables t
                JOIN sys.schemas s ON t.schema_id = s.schema_id
                JOIN sys.partitions p ON t.object_id = p.object_id
                WHERE p.index_id IN (0,1)
                GROUP BY s.name, t.name
                ORDER BY s.name, t.name";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var schema = r.GetString(0);
                var name   = r.GetString(1);
                var rows   = r.IsDBNull(2) ? 0L : Convert.ToInt64(r.GetValue(2));
                tables[$"{schema}.{name}"] = new TableInfo(schema, name, rows, new List<TableColumn>());
            }
        }

        await using (var colCmd = conn.CreateCommand())
        {
            colCmd.CommandText = @"
                SELECT s.name AS schema_name, t.name AS table_name,
                       c.name AS col_name, ty.name AS data_type
                FROM sys.columns c
                JOIN sys.tables t  ON c.object_id = t.object_id
                JOIN sys.schemas s ON t.schema_id = s.schema_id
                JOIN sys.types ty  ON c.user_type_id = ty.user_type_id
                ORDER BY s.name, t.name, c.column_id";
            await using var r = await colCmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var k = $"{r.GetString(0)}.{r.GetString(1)}";
                if (tables.TryGetValue(k, out var t))
                    t.Columns.Add(new TableColumn(r.GetString(2), r.GetString(3), IsLikelyPII(r.GetString(2))));
            }
        }
        return tables.Values.ToList();
    }

    static async Task<List<TableInfo>> ListPostgresAsync(string connStr, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(ct);
        var tables = new Dictionary<string, TableInfo>();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT n.nspname, c.relname, c.reltuples::bigint AS row_count
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE c.relkind = 'r' AND n.nspname NOT IN ('pg_catalog','information_schema','pg_toast')
                ORDER BY n.nspname, c.relname";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var s = r.GetString(0); var t = r.GetString(1);
                tables[$"{s}.{t}"] = new TableInfo(s, t, r.GetInt64(2), new List<TableColumn>());
            }
        }
        await using (var colCmd = conn.CreateCommand())
        {
            colCmd.CommandText = @"
                SELECT table_schema, table_name, column_name, data_type
                FROM information_schema.columns
                WHERE table_schema NOT IN ('pg_catalog','information_schema')
                ORDER BY table_schema, table_name, ordinal_position";
            await using var r = await colCmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var k = $"{r.GetString(0)}.{r.GetString(1)}";
                if (tables.TryGetValue(k, out var t))
                    t.Columns.Add(new TableColumn(r.GetString(2), r.GetString(3), IsLikelyPII(r.GetString(2))));
            }
        }
        return tables.Values.ToList();
    }

    static async Task<List<TableInfo>> ListMySqlAsync(string connStr, CancellationToken ct)
    {
        await using var conn = new MySqlConnection(connStr);
        await conn.OpenAsync(ct);
        var dbName = conn.Database;
        var tables = new Dictionary<string, TableInfo>();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT table_name, COALESCE(table_rows,0) FROM information_schema.tables WHERE table_schema=@db AND table_type='BASE TABLE'";
            cmd.Parameters.AddWithValue("@db", dbName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var t = r.GetString(0);
                tables[$"{dbName}.{t}"] = new TableInfo(dbName, t, r.GetInt64(1), new List<TableColumn>());
            }
        }
        await using (var colCmd = conn.CreateCommand())
        {
            colCmd.CommandText = "SELECT table_name, column_name, data_type FROM information_schema.columns WHERE table_schema=@db ORDER BY table_name, ordinal_position";
            colCmd.Parameters.AddWithValue("@db", dbName);
            await using var r = await colCmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var k = $"{dbName}.{r.GetString(0)}";
                if (tables.TryGetValue(k, out var t))
                    t.Columns.Add(new TableColumn(r.GetString(1), r.GetString(2), IsLikelyPII(r.GetString(1))));
            }
        }
        return tables.Values.ToList();
    }

    static async Task<List<TableInfo>> ListOracleAsync(string connStr, CancellationToken ct)
    {
        await using var conn = new OracleConnection(connStr);
        await conn.OpenAsync(ct);
        var tables = new Dictionary<string, TableInfo>();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT owner, table_name, NVL(num_rows,0)
                FROM all_tables
                WHERE owner NOT IN ('SYS','SYSTEM','XDB','MDSYS','ORDSYS','CTXSYS','OUTLN','DBSNMP','DIP','APPQOSSYS','GSMADMIN_INTERNAL','AUDSYS')
                ORDER BY owner, table_name";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var s = r.GetString(0); var t = r.GetString(1);
                tables[$"{s}.{t}"] = new TableInfo(s, t, Convert.ToInt64(r.GetValue(2)), new List<TableColumn>());
            }
        }
        await using (var colCmd = conn.CreateCommand())
        {
            colCmd.CommandText = @"
                SELECT owner, table_name, column_name, data_type
                FROM all_tab_columns
                WHERE owner NOT IN ('SYS','SYSTEM','XDB','MDSYS','ORDSYS','CTXSYS','OUTLN','DBSNMP','DIP','APPQOSSYS','GSMADMIN_INTERNAL','AUDSYS')
                ORDER BY owner, table_name, column_id";
            await using var r = await colCmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var k = $"{r.GetString(0)}.{r.GetString(1)}";
                if (tables.TryGetValue(k, out var t))
                    t.Columns.Add(new TableColumn(r.GetString(2), r.GetString(3), IsLikelyPII(r.GetString(2))));
            }
        }
        return tables.Values.ToList();
    }
}
