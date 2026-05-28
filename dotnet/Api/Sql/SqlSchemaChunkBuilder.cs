using System.Text;

namespace SetYazilim.Llm.Api.Sql;

/// <summary>
/// Structured chunk format for SQL schema objects — replaces raw DDL text split.
///
/// Goal: keep all columns of one table in ONE chunk so retrieval finds them
/// together. Adds derived domain tags (vergi/vat, müşteri/customer, ...) to
/// improve Turkish query recall.
///
/// Chunk format:
/// <code>
/// OBJECT_TYPE: TABLE
/// SCHEMA: dbo
/// NAME: Quotation
/// QUALIFIED: dbo.Quotation
///
/// COLUMNS (62):
///   Id int NOT NULL [PK] [IDENTITY]
///   VATAmount decimal(18,2) NULL
///   ...
///
/// FOREIGN_KEYS:
///   CustomerId -> dbo.Customer.Id
///
/// INDEXES:
///   IX_Quotation_Date (CreateDate)
///
/// TAGS: vat, kdv, vergi, tax, quotation, teklif
/// </code>
/// </summary>
public sealed record ColumnDef(
    string  Name,
    string  DataType,
    bool    Nullable,
    bool    Identity,
    string? Default,
    bool    IsPrimaryKey);

public sealed record FkDef(
    string Column,
    string RefSchema,
    string RefTable,
    string RefColumn);

public sealed record IndexDef(
    string   Name,
    string[] Columns,
    bool     Unique);

public sealed record TableSchema(
    string           Schema,
    string           Name,
    List<ColumnDef>  Columns,
    List<FkDef>      ForeignKeys,
    List<IndexDef>   Indexes);

public static class SqlSchemaChunkBuilder
{
    /// <summary>
    /// Max chars per chunk — nomic-embed-text-v1.5 supports only **2048 tokens** (vLLM enforces
    /// this hard with HTTP 400, not silent truncation). 1 token ≈ 3-4 chars for SQL-heavy text,
    /// so we keep chunks under ~6000 chars (safety margin of ~30%).
    ///
    /// Earlier 7000-char limit caused 2788/11043 procedures to fail with
    /// "maximum context length is 2048 tokens" — diagnosed 2026-05-28 after first
    /// large-scale ingest. Lowered to 5500 to fit reliably.
    /// </summary>
    public const int MaxChunkChars = 5500;

    /// <summary>
    /// Build one or more chunks describing a table.
    ///
    /// Small/medium tables (≤ ~60 cols → fits MaxChunkChars) → single chunk with full
    /// columns + FKs + indexes + tags.
    ///
    /// Wide tables (200+ cols, common in denormalized finance schemas) overflow the
    /// 2048-token embedding limit, so we split: chunk 1 carries header + first N
    /// columns + FKs + indexes + tags; subsequent chunks carry header + remaining
    /// column slices with a `COLUMNS_CONT` marker. Each chunk is independently
    /// retrievable and contains enough metadata to identify the table.
    /// </summary>
    public static IEnumerable<string> BuildTableChunks(TableSchema t)
    {
        var headerSb = new StringBuilder();
        headerSb.AppendLine("OBJECT_TYPE: TABLE");
        headerSb.AppendLine($"SCHEMA: {t.Schema}");
        headerSb.AppendLine($"NAME: {t.Name}");
        headerSb.AppendLine($"QUALIFIED: {t.Schema}.{t.Name}");
        var header = headerSb.ToString();

        // Pre-format all columns as lines
        var colLines = new List<string>(t.Columns.Count);
        foreach (var c in t.Columns)
        {
            var line = new StringBuilder($"  {c.Name} {c.DataType}");
            line.Append(c.Nullable ? " NULL" : " NOT NULL");
            if (c.Identity) line.Append(" [IDENTITY]");
            if (c.IsPrimaryKey) line.Append(" [PK]");
            if (!string.IsNullOrEmpty(c.Default)) line.Append($" DEFAULT {c.Default}");
            colLines.Add(line.ToString());
        }

        // FK + index sections
        var tailSb = new StringBuilder();
        if (t.ForeignKeys.Count > 0)
        {
            tailSb.AppendLine();
            tailSb.AppendLine("FOREIGN_KEYS:");
            foreach (var fk in t.ForeignKeys)
                tailSb.AppendLine($"  {fk.Column} -> {fk.RefSchema}.{fk.RefTable}.{fk.RefColumn}");
        }
        if (t.Indexes.Count > 0)
        {
            tailSb.AppendLine();
            tailSb.AppendLine("INDEXES:");
            foreach (var ix in t.Indexes)
            {
                var prefix = ix.Unique ? "UNIQUE " : "";
                tailSb.AppendLine($"  {prefix}{ix.Name} ({string.Join(", ", ix.Columns)})");
            }
        }
        var tags = DeriveTags(t.Name, t.Columns.Select(c => c.Name));
        var tagLine = tags.Length > 0 ? $"\nTAGS: {string.Join(", ", tags)}\n" : "";
        var tail = tailSb.ToString() + tagLine;

        // Try single chunk: header + COLUMNS + tail
        var fullColsBlock = new StringBuilder();
        fullColsBlock.AppendLine();
        fullColsBlock.AppendLine($"COLUMNS ({t.Columns.Count}):");
        foreach (var l in colLines) fullColsBlock.AppendLine(l);
        var singleChunk = header + fullColsBlock + tail;
        if (singleChunk.Length <= MaxChunkChars)
        {
            yield return singleChunk;
            yield break;
        }

        // Wide table — split COLUMNS across multiple chunks, keep FK/index/tags in last chunk
        var partial = new StringBuilder();
        int chunkIdx = 1;
        int totalCols = colLines.Count;
        int colsInChunk = 0;
        int colStart = 0;

        for (int i = 0; i < colLines.Count; i++)
        {
            // Build candidate body for this chunk
            if (partial.Length == 0)
            {
                partial.Append(header);
                partial.AppendLine();
                partial.AppendLine(chunkIdx == 1
                    ? $"COLUMNS ({totalCols}) — part {chunkIdx}:"
                    : $"COLUMNS_CONT (cols {colStart + 1}..) — part {chunkIdx}:");
            }
            // Estimate adding this column line + (possibly) tail at end
            var tentativeLen = partial.Length + colLines[i].Length + 1
                             + (i == colLines.Count - 1 ? tail.Length : 0);
            if (tentativeLen > MaxChunkChars && colsInChunk > 0)
            {
                // Flush this chunk WITHOUT tail (more columns follow)
                yield return partial.ToString();
                partial.Clear();
                colStart = i;
                colsInChunk = 0;
                chunkIdx++;
                i--;   // reprocess this column in the new chunk
                continue;
            }
            partial.AppendLine(colLines[i]);
            colsInChunk++;
        }

        // Last chunk gets the tail (FKs/indexes/tags) appended
        if (partial.Length > 0)
        {
            partial.Append(tail);
            yield return partial.ToString();
        }
    }

    /// <summary>Backward-compat shim — single concatenated chunk (debugging / unit tests).</summary>
    public static string BuildTableChunk(TableSchema t) => string.Join("\n---\n", BuildTableChunks(t));

    /// <summary>
    /// Wrap raw DDL (view/function/trigger) with structured header — splits if oversize.
    /// Use this in ingest paths; <see cref="BuildDdlChunk(DbObjectInfo, string)"/> is the
    /// single-chunk variant kept for callers that pre-check size.
    /// </summary>
    public static IEnumerable<string> BuildDdlChunkOrSplit(DbObjectInfo obj, string ddl)
    {
        var single = BuildDdlChunk(obj, ddl);
        if (single.Length <= MaxChunkChars) { yield return single; yield break; }
        // Reuse procedure-chunking logic (boundary-aware split)
        foreach (var chunk in BuildProcedureChunks(obj, ddl))
            yield return chunk;
    }

    /// <summary>Wrap raw DDL (view/function/trigger) with a structured header for retrieval cues.</summary>
    public static string BuildDdlChunk(DbObjectInfo obj, string ddl)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OBJECT_TYPE: {obj.Type.ToString().ToUpperInvariant()}");
        sb.AppendLine($"SCHEMA: {obj.Schema}");
        sb.AppendLine($"NAME: {obj.Name}");
        sb.AppendLine($"QUALIFIED: {obj.QualifiedName}");

        var tags = DeriveTags(obj.Name, Array.Empty<string>());
        if (tags.Length > 0) sb.AppendLine($"TAGS: {string.Join(", ", tags)}");

        sb.AppendLine();
        sb.AppendLine("DEFINITION:");
        sb.AppendLine(ddl.TrimEnd());
        return sb.ToString();
    }

    /// <summary>
    /// Procedure-specific chunking. SP bodies can be huge — split into sections by
    /// SQL statement boundaries (BEGIN/END blocks, SELECT/UPDATE/INSERT/DELETE/EXEC).
    /// Each chunk gets a SECTION header so retrieval shows which part matched.
    /// </summary>
    public static IEnumerable<string> BuildProcedureChunks(DbObjectInfo obj, string ddl)
    {
        // If the SP fits in one chunk, emit one
        if (ddl.Length <= MaxChunkChars)
        {
            yield return BuildDdlChunk(obj, ddl);
            yield break;
        }

        // Section-aware split: try to break at statement keywords or empty lines.
        // Fall back to line-by-line accumulation.
        var lines = ddl.Replace("\r\n", "\n").Split('\n');
        var current = new StringBuilder();
        int sectionNum = 1;

        foreach (var line in lines)
        {
            // If adding this line would exceed budget AND we're at a natural boundary, emit.
            var isBoundary = string.IsNullOrWhiteSpace(line)
                          || line.TrimStart().StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)
                          || line.TrimStart().StartsWith("END",   StringComparison.OrdinalIgnoreCase)
                          || line.TrimStart().StartsWith("SELECT",StringComparison.OrdinalIgnoreCase)
                          || line.TrimStart().StartsWith("INSERT",StringComparison.OrdinalIgnoreCase)
                          || line.TrimStart().StartsWith("UPDATE",StringComparison.OrdinalIgnoreCase)
                          || line.TrimStart().StartsWith("DELETE",StringComparison.OrdinalIgnoreCase);

            if (current.Length + line.Length + 1 > MaxChunkChars && current.Length > 0 && isBoundary)
            {
                yield return BuildDdlChunkSection(obj, current.ToString(), sectionNum++);
                current.Clear();
            }
            current.Append(line).Append('\n');

            // Hard cap — emit even if no boundary (safety net)
            if (current.Length > MaxChunkChars + 1000)
            {
                yield return BuildDdlChunkSection(obj, current.ToString(), sectionNum++);
                current.Clear();
            }
        }

        if (current.Length > 0)
            yield return BuildDdlChunkSection(obj, current.ToString(), sectionNum);
    }

    private static string BuildDdlChunkSection(DbObjectInfo obj, string section, int part)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OBJECT_TYPE: {obj.Type.ToString().ToUpperInvariant()}");
        sb.AppendLine($"SCHEMA: {obj.Schema}");
        sb.AppendLine($"NAME: {obj.Name}");
        sb.AppendLine($"QUALIFIED: {obj.QualifiedName}");
        sb.AppendLine($"PART: {part}");

        var tags = DeriveTags(obj.Name, Array.Empty<string>());
        if (tags.Length > 0) sb.AppendLine($"TAGS: {string.Join(", ", tags)}");

        sb.AppendLine();
        sb.AppendLine("DEFINITION:");
        sb.AppendLine(section.TrimEnd());
        return sb.ToString();
    }

    /// <summary>
    /// Derived domain tags — bilingual hints that help Turkish queries match
    /// English column/table names. Heuristic substring match over table name + columns.
    /// </summary>
    public static string[] DeriveTags(string objName, IEnumerable<string> columnNames)
    {
        var combined = (objName + " " + string.Join(" ", columnNames)).ToLowerInvariant();
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, syns) in DomainHints)
        {
            if (combined.Contains(key))
            {
                tags.Add(key);
                foreach (var s in syns) tags.Add(s);
            }
        }

        return tags.OrderBy(t => t).ToArray();
    }

    /// <summary>Per-type collection naming: base + suffix. Used during ingest.</summary>
    public static string GetCollectionName(string baseCollection, DbObjectType type)
    {
        var clean = string.IsNullOrWhiteSpace(baseCollection) ? "sql" : baseCollection.Trim();
        var suffix = type switch
        {
            DbObjectType.Table     => "tables",
            DbObjectType.View      => "views",
            DbObjectType.Procedure => "procedures",
            DbObjectType.Function  => "functions",
            DbObjectType.Trigger   => "triggers",
            DbObjectType.Index     => "indexes",
            _                      => "other"
        };
        return $"{clean}-{suffix}";
    }

    // ── Domain hints ──────────────────────────────────────────────────────────
    // Bilingual TR↔EN synonym pairs — used to add searchable tags to schema chunks.
    // Keep additions short, lowercase, and ASCII-normalized where possible (the
    // turkish_unaccent FTS config will handle the unaccented forms anyway).
    private static readonly Dictionary<string, string[]> DomainHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vat"]         = ["vergi", "kdv", "tax"],
        ["tax"]         = ["vergi", "kdv"],
        ["kdv"]         = ["vergi", "vat", "tax"],
        ["customer"]    = ["musteri", "müşteri", "cari", "client"],
        ["client"]      = ["musteri", "müşteri", "cari", "customer"],
        ["invoice"]     = ["fatura", "bill"],
        ["payment"]     = ["odeme", "ödeme", "tahsilat"],
        ["quotation"]   = ["teklif", "quote", "offer"],
        ["order"]       = ["siparis", "sipariş"],
        ["limit"]       = ["kredi limiti", "creditlimit"],
        ["balance"]     = ["bakiye", "remaining"],
        ["stock"]       = ["stok", "inventory", "envanter"],
        ["amount"]      = ["tutar", "money"],
        ["currency"]    = ["para", "doviz", "döviz"],
        ["date"]        = ["tarih"],
        ["user"]        = ["kullanici", "kullanıcı", "operator"],
        ["description"] = ["aciklama", "açıklama", "note", "remark"],
        ["item"]        = ["urun", "ürün", "material", "malzeme"],
        ["product"]     = ["urun", "ürün", "item"],
        ["warehouse"]   = ["depo"],
        ["address"]     = ["adres"],
    };
}
