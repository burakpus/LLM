using System.Data.Common;
using System.Text;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace SetYazilim.Llm.Api.Sql;

public sealed record TableConfig(
    int      Id,
    int      ConnectionId,
    string   Schema,
    string   Table,
    string   PkCol,
    string   CreatedCol,
    string   UpdatedCol,
    int      RowLimit,
    string   WhereClause,
    string[] IncludedColumns,
    int?     GroupId,
    string   Collection,
    DateTime? LastSyncedAt,
    DateTime? LastMaxUpdatedAt)
{
    public string QualifiedName => string.IsNullOrEmpty(Schema) ? Table : $"{Schema}.{Table}";
    public string[] PkCols => PkCol.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed record RowResult(
    string                     PkValue,
    Dictionary<string, string> Columns,
    DateTime?                  UpdatedAt);

public static class SqlDataDelta
{
    /// <summary>Pull rows for delta sync. Returns rows where updated_col > lastMaxUpdatedAt (or all rows if no updated_col).</summary>
    public static async Task<List<RowResult>> FetchDeltaAsync(
        DbType dbType, string connStr, TableConfig cfg, CancellationToken ct)
    {
        if (cfg.PkCols.Length == 0)
            throw new InvalidOperationException("PK kolonu tanımlı değil");

        var hasUpdatedCol = !string.IsNullOrWhiteSpace(cfg.UpdatedCol);
        var quoteName     = QuoteIdentFn(dbType);
        var fmtParam      = ParamFn(dbType);

        // Columns to select
        string columnsSql = "*";
        if (cfg.IncludedColumns.Length > 0)
        {
            var cols = new HashSet<string>(cfg.IncludedColumns, StringComparer.OrdinalIgnoreCase);
            foreach (var pk in cfg.PkCols) cols.Add(pk);
            if (hasUpdatedCol) cols.Add(cfg.UpdatedCol);
            columnsSql = string.Join(", ", cols.Select(quoteName));
        }

        var schemaQ = quoteName(cfg.Schema);
        var tableQ  = quoteName(cfg.Table);
        var tableRef = $"{schemaQ}.{tableQ}";

        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(cfg.WhereClause))
            conditions.Add($"({cfg.WhereClause})");

        if (hasUpdatedCol && cfg.LastMaxUpdatedAt.HasValue)
            conditions.Add($"{quoteName(cfg.UpdatedCol)} > {fmtParam("p_since")}");

        var whereSql = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "";

        var orderBy = hasUpdatedCol ? $" ORDER BY {quoteName(cfg.UpdatedCol)} ASC" : "";
        var limit   = Math.Clamp(cfg.RowLimit, 1, 100_000);
        var topClause = dbType switch
        {
            DbType.MsSql    => $"SELECT TOP {limit} {columnsSql} FROM {tableRef}{whereSql}{orderBy}",
            DbType.Oracle   => $"SELECT {columnsSql} FROM {tableRef}{whereSql}{orderBy} FETCH FIRST {limit} ROWS ONLY",
            _               => $"SELECT {columnsSql} FROM {tableRef}{whereSql}{orderBy} LIMIT {limit}",
        };

        await using var conn = CreateConnection(dbType, connStr);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = topClause;
        cmd.CommandTimeout = 120;

        if (hasUpdatedCol && cfg.LastMaxUpdatedAt.HasValue)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = fmtParam("p_since").Replace("@","").Replace(":","");
            // For SqlClient need @-prefixed; Npgsql positional ($1); MySQL @; Oracle :name
            // Easier path: use vendor-specific param names
            switch (dbType)
            {
                case DbType.MsSql:    p.ParameterName = "@p_since"; break;
                case DbType.MySql:    p.ParameterName = "@p_since"; break;
                case DbType.Oracle:   p.ParameterName = "p_since";  break;
                case DbType.Postgres: p.ParameterName = "p_since";  break;
            }
            p.Value = cfg.LastMaxUpdatedAt.Value;
            cmd.Parameters.Add(p);
        }

        var rows = new List<RowResult>();
        await using var r = await cmd.ExecuteReaderAsync(ct);

        // Cache column ordinals
        var colNames = new string[r.FieldCount];
        for (int i = 0; i < r.FieldCount; i++) colNames[i] = r.GetName(i);
        var pkOrdinals = cfg.PkCols
            .Select(pc => Array.FindIndex(colNames, c => c.Equals(pc, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var updOrdinal = hasUpdatedCol
            ? Array.FindIndex(colNames, c => c.Equals(cfg.UpdatedCol, StringComparison.OrdinalIgnoreCase))
            : -1;

        while (await r.ReadAsync(ct))
        {
            var cols = new Dictionary<string, string>(r.FieldCount);
            for (int i = 0; i < r.FieldCount; i++)
            {
                var v = r.IsDBNull(i) ? "" : (r.GetValue(i)?.ToString() ?? "");
                if (SqlDataSampler.IsLikelyPII(colNames[i]) && !string.IsNullOrEmpty(v))
                    v = MaskValue(colNames[i], v);
                // Per-cell truncation: 500 → 12000 (~3000 token). Önceki 500 sınırı veri
                // sözlüğü view'lerindeki STRING_AGG ile birleşmiş columns_description
                // gibi büyük metin alanlarını yarıdan kesiyordu (örn. 61-kolon Quotation
                // açıklaması = ~5000 char). 12000 makul: BLOB/binary'yi sınırlar ama
                // doğal metin alanlarını korur. Embedding chunk size (4000) zaten ikincil
                // koruma — chunk split olur ama veri silinmez.
                if (v.Length > 12000) v = v[..12000] + "…";
                cols[colNames[i]] = v;
            }

            var pkParts = new string[pkOrdinals.Length];
            for (int i = 0; i < pkOrdinals.Length; i++)
            {
                var o = pkOrdinals[i];
                pkParts[i] = o >= 0 ? (r.IsDBNull(o) ? "" : r.GetValue(o)?.ToString() ?? "") : "";
            }
            var pkValue = string.Join("|", pkParts);
            DateTime? updatedAt = null;
            if (updOrdinal >= 0 && !r.IsDBNull(updOrdinal))
            {
                if (r.GetValue(updOrdinal) is DateTime dt) updatedAt = dt;
                else if (DateTime.TryParse(r.GetValue(updOrdinal)?.ToString(), out var parsed)) updatedAt = parsed;
            }

            rows.Add(new RowResult(pkValue, cols, updatedAt));
        }
        return rows;
    }

    public static string FormatRowAsMarkdown(TableConfig cfg, RowResult row)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## {cfg.QualifiedName} ({cfg.PkCol}={row.PkValue})");
        sb.AppendLine();
        sb.AppendLine("| Kolon | Değer |");
        sb.AppendLine("|---|---|");
        foreach (var (k, v) in row.Columns)
            sb.Append("| ").Append(EscapeMd(k)).Append(" | ").Append(EscapeMd(v)).AppendLine(" |");
        return sb.ToString();
    }

    static string EscapeMd(string s) => s.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");

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

    static Func<string, string> QuoteIdentFn(DbType t) => t switch
    {
        DbType.MsSql    => name => $"[{name}]",
        DbType.Postgres => name => $"\"{name}\"",
        DbType.MySql    => name => $"`{name}`",
        DbType.Oracle   => name => $"\"{name}\"",
        _ => name => name,
    };

    static Func<string, string> ParamFn(DbType t) => t switch
    {
        DbType.MsSql    => n => $"@{n}",
        DbType.Postgres => n => $"@{n}",  // Npgsql supports named with @ when AddWithValue
        DbType.MySql    => n => $"@{n}",
        DbType.Oracle   => n => $":{n}",
        _ => n => $"@{n}",
    };
}
