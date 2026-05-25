using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;

namespace SetYazilim.Llm.Api.Sql;

public sealed class MsSqlSchemaProvider : ISqlSchemaProvider
{
    public DbType DbType => DbType.MsSql;

    public async Task<List<DbObjectInfo>> ListObjectsAsync(
        string connStr, HashSet<DbObjectType>? includeTypes, CancellationToken ct)
    {
        var typeFilter = includeTypes ?? new HashSet<DbObjectType> {
            DbObjectType.Table, DbObjectType.View, DbObjectType.Procedure,
            DbObjectType.Function, DbObjectType.Trigger,
        };

        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);

        var results = new List<DbObjectInfo>();

        // sys.objects type codes:
        //   U = User table  V = View  P = Stored procedure
        //   FN/IF/TF = Function (scalar / inline TVF / multi-statement TVF)
        //   TR = Trigger
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT SCHEMA_NAME(schema_id) AS schema_name, name, type
            FROM sys.objects
            WHERE type IN ('U','V','P','FN','IF','TF','TR')
              AND is_ms_shipped = 0
            ORDER BY type, name";

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schemaName = r.GetString(0);
            var name = r.GetString(1);
            var typeCode = r.GetString(2).Trim();
            var objType = typeCode switch
            {
                "U"  => DbObjectType.Table,
                "V"  => DbObjectType.View,
                "P"  => DbObjectType.Procedure,
                "FN" or "IF" or "TF" => DbObjectType.Function,
                "TR" => DbObjectType.Trigger,
                _    => DbObjectType.Table,
            };
            if (typeFilter.Contains(objType))
                results.Add(new DbObjectInfo(schemaName, name, objType));
        }
        return results;
    }

    public async Task<string> GetCreateScriptAsync(string connStr, DbObjectInfo obj, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connStr);
        await conn.OpenAsync(ct);

        return obj.Type switch
        {
            DbObjectType.Table => await BuildCreateTableAsync(conn, obj, ct),
            DbObjectType.View or DbObjectType.Procedure or DbObjectType.Function or DbObjectType.Trigger
                => await GetObjectDefinitionAsync(conn, obj, ct),
            _ => $"-- {obj.Type} {obj.QualifiedName}: not supported"
        };
    }

    static async Task<string> GetObjectDefinitionAsync(SqlConnection conn, DbObjectInfo obj, CancellationToken ct)
    {
        // OBJECT_DEFINITION returns the full CREATE statement text for views/procs/funcs/triggers
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT OBJECT_DEFINITION(OBJECT_ID(@n))";
        cmd.Parameters.Add("@n", SqlDbType.NVarChar, 520).Value = $"[{obj.Schema}].[{obj.Name}]";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result?.ToString() ?? $"-- {obj.QualifiedName}: definition not available";
    }

    static async Task<string> BuildCreateTableAsync(SqlConnection conn, DbObjectInfo obj, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE [{obj.Schema}].[{obj.Name}] (");

        // Columns
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT c.name AS col_name,
                       t.name AS type_name,
                       c.max_length, c.precision, c.scale,
                       c.is_nullable, c.is_identity,
                       OBJECT_DEFINITION(c.default_object_id) AS default_def
                FROM sys.columns c
                JOIN sys.types t ON c.user_type_id = t.user_type_id
                WHERE c.object_id = OBJECT_ID(@n)
                ORDER BY c.column_id";
            cmd.Parameters.Add("@n", SqlDbType.NVarChar, 520).Value = $"[{obj.Schema}].[{obj.Name}]";

            await using var r = await cmd.ExecuteReaderAsync(ct);
            var cols = new List<string>();
            while (await r.ReadAsync(ct))
            {
                var colName  = r.GetString(0);
                var typeName = r.GetString(1);
                var maxLen   = r.GetInt16(2);
                var prec     = r.GetByte(3);
                var scale    = r.GetByte(4);
                var nullable = r.GetBoolean(5);
                var identity = r.GetBoolean(6);
                var defaultDef = r.IsDBNull(7) ? null : r.GetString(7);

                var typeSpec = typeName.ToLowerInvariant() switch
                {
                    "nvarchar" or "nchar" => $"{typeName.ToUpperInvariant()}({(maxLen == -1 ? "MAX" : (maxLen / 2).ToString())})",
                    "varchar" or "char" or "varbinary" or "binary" => $"{typeName.ToUpperInvariant()}({(maxLen == -1 ? "MAX" : maxLen.ToString())})",
                    "decimal" or "numeric" => $"{typeName.ToUpperInvariant()}({prec},{scale})",
                    _ => typeName.ToUpperInvariant()
                };

                var line = $"    [{colName}] {typeSpec}";
                if (identity) line += " IDENTITY";
                line += nullable ? " NULL" : " NOT NULL";
                if (defaultDef != null) line += $" DEFAULT {defaultDef}";
                cols.Add(line);
            }
            sb.AppendLine(string.Join(",\n", cols));
        }

        // Primary key — read raw rows, aggregate in C# (SQL 2008+ compatible)
        var pkColumns = new List<(string IdxName, string ColName, int Ordinal)>();
        await using (var pkCmd = conn.CreateCommand())
        {
            pkCmd.CommandText = @"
                SELECT i.name AS pk_name, c.name AS col_name, ic.key_ordinal
                FROM sys.indexes i
                JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                WHERE i.object_id = OBJECT_ID(@n) AND i.is_primary_key = 1
                ORDER BY ic.key_ordinal";
            pkCmd.Parameters.Add("@n", SqlDbType.NVarChar, 520).Value = $"[{obj.Schema}].[{obj.Name}]";
            await using var r = await pkCmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                pkColumns.Add((r.GetString(0), r.GetString(1), r.GetByte(2)));
        }
        if (pkColumns.Count > 0)
        {
            var pkName = pkColumns[0].IdxName;
            var pkCols = string.Join(", ", pkColumns.OrderBy(p => p.Ordinal).Select(p => $"[{p.ColName}]"));
            sb.AppendLine($",    CONSTRAINT [{pkName}] PRIMARY KEY ({pkCols})");
        }

        sb.AppendLine(");");

        // Indexes (non-PK) — same aggregation strategy
        var indexes = new List<(string IdxName, string ColName, int Ordinal, bool Unique)>();
        await using (var idxCmd = conn.CreateCommand())
        {
            idxCmd.CommandText = @"
                SELECT i.name, c.name, ic.key_ordinal, i.is_unique
                FROM sys.indexes i
                JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                WHERE i.object_id = OBJECT_ID(@n)
                  AND i.is_primary_key = 0
                  AND i.type_desc <> 'HEAP'
                  AND i.name IS NOT NULL
                ORDER BY i.name, ic.key_ordinal";
            idxCmd.Parameters.Add("@n", SqlDbType.NVarChar, 520).Value = $"[{obj.Schema}].[{obj.Name}]";
            await using var r = await idxCmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                indexes.Add((r.GetString(0), r.GetString(1), r.GetByte(2), r.GetBoolean(3)));
        }
        foreach (var grp in indexes.GroupBy(x => x.IdxName))
        {
            var first = grp.First();
            var cols  = string.Join(", ", grp.OrderBy(x => x.Ordinal).Select(x => $"[{x.ColName}]"));
            sb.AppendLine($"CREATE {(first.Unique ? "UNIQUE " : "")}INDEX [{first.IdxName}] ON [{obj.Schema}].[{obj.Name}] ({cols});");
        }

        return sb.ToString();
    }
}
