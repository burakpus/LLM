using System.Text;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace SetYazilim.Llm.Api.Sql;

// ─────────────────────────────────────────────────────────────────────────────
// PostgreSQL
// ─────────────────────────────────────────────────────────────────────────────
public sealed class PostgresSchemaProvider : ISqlSchemaProvider
{
    public DbType DbType => DbType.Postgres;

    public async Task<List<DbObjectInfo>> ListObjectsAsync(string connStr, HashSet<DbObjectType>? includeTypes, CancellationToken ct)
    {
        var typeFilter = includeTypes ?? new HashSet<DbObjectType> {
            DbObjectType.Table, DbObjectType.View, DbObjectType.Function, DbObjectType.Trigger,
        };

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(ct);

        var results = new List<DbObjectInfo>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT n.nspname AS schema_name, c.relname AS name, c.relkind AS kind
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema','pg_toast')
              AND c.relkind IN ('r','v','m')
            UNION ALL
            SELECT n.nspname, p.proname, 'f'
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema')
            UNION ALL
            SELECT n.nspname, t.tgname, 'g'
            FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE NOT t.tgisinternal
            ORDER BY schema_name, name";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0); var name = r.GetString(1); var k = r.GetString(2);
            var t = k switch { "r" => DbObjectType.Table, "v" or "m" => DbObjectType.View,
                               "f" => DbObjectType.Function, "g" => DbObjectType.Trigger, _ => DbObjectType.Table };
            if (typeFilter.Contains(t)) results.Add(new DbObjectInfo(schema, name, t));
        }
        return results;
    }

    public async Task<string> GetCreateScriptAsync(string connStr, DbObjectInfo obj, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync(ct);

        switch (obj.Type)
        {
            case DbObjectType.View:
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 'CREATE VIEW ' || quote_ident($1) || '.' || quote_ident($2) || ' AS ' || pg_get_viewdef(($1 || '.' || $2)::regclass, true)";
                cmd.Parameters.AddWithValue(obj.Schema); cmd.Parameters.AddWithValue(obj.Name);
                return (string?)await cmd.ExecuteScalarAsync(ct) ?? "";
            }
            case DbObjectType.Function:
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT pg_get_functiondef(p.oid)
                                    FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                                    WHERE n.nspname=$1 AND p.proname=$2 LIMIT 1";
                cmd.Parameters.AddWithValue(obj.Schema); cmd.Parameters.AddWithValue(obj.Name);
                return (string?)await cmd.ExecuteScalarAsync(ct) ?? "";
            }
            case DbObjectType.Table:
                return await BuildCreateTableAsync(conn, obj, ct);
            case DbObjectType.Trigger:
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT pg_get_triggerdef(t.oid, true)
                                    FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid
                                    JOIN pg_namespace n ON n.oid=c.relnamespace
                                    WHERE n.nspname=$1 AND t.tgname=$2 LIMIT 1";
                cmd.Parameters.AddWithValue(obj.Schema); cmd.Parameters.AddWithValue(obj.Name);
                return (string?)await cmd.ExecuteScalarAsync(ct) ?? "";
            }
            default: return $"-- {obj.Type} not supported";
        }
    }

    static async Task<string> BuildCreateTableAsync(NpgsqlConnection conn, DbObjectInfo obj, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE {obj.Schema}.{obj.Name} (");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT column_name,
                                   CASE WHEN data_type='character varying' THEN 'VARCHAR(' || character_maximum_length || ')'
                                        WHEN data_type='numeric' THEN 'NUMERIC(' || numeric_precision || ',' || numeric_scale || ')'
                                        ELSE UPPER(data_type) END AS dtype,
                                   is_nullable, column_default
                            FROM information_schema.columns
                            WHERE table_schema=$1 AND table_name=$2
                            ORDER BY ordinal_position";
        cmd.Parameters.AddWithValue(obj.Schema); cmd.Parameters.AddWithValue(obj.Name);
        var cols = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name  = r.GetString(0);
            var dtype = r.GetString(1);
            var nullable = r.GetString(2) == "YES";
            var deflt = r.IsDBNull(3) ? null : r.GetString(3);
            var line = $"    {name} {dtype}{(nullable ? "" : " NOT NULL")}";
            if (deflt != null) line += $" DEFAULT {deflt}";
            cols.Add(line);
        }
        sb.AppendLine(string.Join(",\n", cols));
        sb.AppendLine(");");
        return sb.ToString();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MySQL
// ─────────────────────────────────────────────────────────────────────────────
public sealed class MySqlSchemaProvider : ISqlSchemaProvider
{
    public DbType DbType => DbType.MySql;

    public async Task<List<DbObjectInfo>> ListObjectsAsync(string connStr, HashSet<DbObjectType>? includeTypes, CancellationToken ct)
    {
        var typeFilter = includeTypes ?? new HashSet<DbObjectType> {
            DbObjectType.Table, DbObjectType.View, DbObjectType.Procedure, DbObjectType.Function, DbObjectType.Trigger,
        };

        await using var conn = new MySqlConnection(connStr);
        await conn.OpenAsync(ct);
        var dbName = conn.Database;

        var results = new List<DbObjectInfo>();

        // Tables and views
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT table_name, table_type
                                FROM information_schema.tables
                                WHERE table_schema = @db
                                ORDER BY table_name";
            cmd.Parameters.AddWithValue("@db", dbName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                var type = r.GetString(1) == "VIEW" ? DbObjectType.View : DbObjectType.Table;
                if (typeFilter.Contains(type)) results.Add(new DbObjectInfo(dbName, name, type));
            }
        }

        // Procedures and functions
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT routine_name, routine_type
                                FROM information_schema.routines
                                WHERE routine_schema = @db";
            cmd.Parameters.AddWithValue("@db", dbName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var name = r.GetString(0);
                var type = r.GetString(1) == "PROCEDURE" ? DbObjectType.Procedure : DbObjectType.Function;
                if (typeFilter.Contains(type)) results.Add(new DbObjectInfo(dbName, name, type));
            }
        }

        // Triggers
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT trigger_name FROM information_schema.triggers WHERE trigger_schema = @db";
            cmd.Parameters.AddWithValue("@db", dbName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (typeFilter.Contains(DbObjectType.Trigger))
                    results.Add(new DbObjectInfo(dbName, r.GetString(0), DbObjectType.Trigger));
        }

        return results;
    }

    public async Task<string> GetCreateScriptAsync(string connStr, DbObjectInfo obj, CancellationToken ct)
    {
        await using var conn = new MySqlConnection(connStr);
        await conn.OpenAsync(ct);

        var keyword = obj.Type switch
        {
            DbObjectType.Table     => "TABLE",
            DbObjectType.View      => "VIEW",
            DbObjectType.Procedure => "PROCEDURE",
            DbObjectType.Function  => "FUNCTION",
            DbObjectType.Trigger   => "TRIGGER",
            _ => "TABLE",
        };

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SHOW CREATE {keyword} `{obj.Schema}`.`{obj.Name}`";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return "";
        // SHOW CREATE returns columns; second column is the DDL (or third for triggers/routines)
        var ddlColIdx = obj.Type switch
        {
            DbObjectType.Trigger   => 2,  // SHOW CREATE TRIGGER returns [Trigger, sql_mode, SQL Original Statement, ...]
            DbObjectType.Procedure or DbObjectType.Function => 2,
            _ => 1,
        };
        return r.GetString(ddlColIdx);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Oracle — uses DBMS_METADATA.GET_DDL
// ─────────────────────────────────────────────────────────────────────────────
public sealed class OracleSchemaProvider : ISqlSchemaProvider
{
    public DbType DbType => DbType.Oracle;

    public async Task<List<DbObjectInfo>> ListObjectsAsync(string connStr, HashSet<DbObjectType>? includeTypes, CancellationToken ct)
    {
        var typeFilter = includeTypes ?? new HashSet<DbObjectType> {
            DbObjectType.Table, DbObjectType.View, DbObjectType.Procedure, DbObjectType.Function, DbObjectType.Trigger,
        };

        await using var conn = new OracleConnection(connStr);
        await conn.OpenAsync(ct);

        var results = new List<DbObjectInfo>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT owner, object_name, object_type
            FROM all_objects
            WHERE owner NOT IN ('SYS','SYSTEM','XDB','MDSYS','ORDSYS','CTXSYS','OUTLN','DBSNMP','DIP','APPQOSSYS','GSMADMIN_INTERNAL','AUDSYS')
              AND object_type IN ('TABLE','VIEW','PROCEDURE','FUNCTION','TRIGGER','PACKAGE')
            ORDER BY object_type, object_name";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0); var name = r.GetString(1); var ot = r.GetString(2);
            var t = ot switch {
                "TABLE"     => DbObjectType.Table,
                "VIEW"      => DbObjectType.View,
                "PROCEDURE" or "PACKAGE" => DbObjectType.Procedure,
                "FUNCTION"  => DbObjectType.Function,
                "TRIGGER"   => DbObjectType.Trigger,
                _ => DbObjectType.Table,
            };
            if (typeFilter.Contains(t)) results.Add(new DbObjectInfo(schema, name, t));
        }
        return results;
    }

    public async Task<string> GetCreateScriptAsync(string connStr, DbObjectInfo obj, CancellationToken ct)
    {
        await using var conn = new OracleConnection(connStr);
        await conn.OpenAsync(ct);

        var oracleType = obj.Type switch
        {
            DbObjectType.Table     => "TABLE",
            DbObjectType.View      => "VIEW",
            DbObjectType.Procedure => "PROCEDURE",
            DbObjectType.Function  => "FUNCTION",
            DbObjectType.Trigger   => "TRIGGER",
            _ => "TABLE",
        };

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DBMS_METADATA.GET_DDL(:t, :n, :s) FROM dual";
        cmd.Parameters.Add(":t", oracleType);
        cmd.Parameters.Add(":n", obj.Name);
        cmd.Parameters.Add(":s", obj.Schema);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result?.ToString() ?? "";
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Factory
// ─────────────────────────────────────────────────────────────────────────────
public static class SqlSchemaProviderFactory
{
    public static ISqlSchemaProvider Get(DbType dbType) => dbType switch
    {
        DbType.MsSql    => new MsSqlSchemaProvider(),
        DbType.Postgres => new PostgresSchemaProvider(),
        DbType.MySql    => new MySqlSchemaProvider(),
        DbType.Oracle   => new OracleSchemaProvider(),
        _ => throw new ArgumentOutOfRangeException(nameof(dbType)),
    };
}
