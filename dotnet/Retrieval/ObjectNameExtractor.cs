using System.Text.RegularExpressions;

namespace SetYazilim.Llm.Retrieval;

/// <summary>
/// Extract candidate database object names from a user query for "exact-name boost"
/// in HybridSearch. The pain point: a query like "quotation tablosu var mı?" should
/// surface <c>dbo.Quotation</c> (the actual main table) rather than just whatever
/// semantically/lexically matches first (QuotationInfoSummary, QuotationExpiry, …).
///
/// Strategy:
///   1. Strip schema prefixes (`dbo.X` → `X`) so users don't need to remember them
///   2. Extract names from Turkish/English "tablosu/table/view/procedure" patterns
///   3. Extract camelCase / PascalCase identifiers (qtPaymentPlan, MyTable)
///   4. Drop common stopwords + words shorter than 3 chars
///
/// The result feeds an EXACT match (case-insensitive) against the chunk metadata's
/// <c>name</c> field — so "quotation" boosts a chunk with <c>"name": "Quotation"</c>
/// but NOT one with <c>"name": "QuotationInfoSummary"</c>.
/// </summary>
public static class ObjectNameExtractor
{
    // Turkish / English object-kind suffixes that signal "the word before me is an object name".
    // Includes "isimli/adında/adlı" forms (Turkish: "named X") that don't directly say "table".
    private static readonly Regex SuffixPattern = new(
        @"\b([A-Za-z_][\w]{2,})\s+(?:tablosu|tablo|view|görünüm|gorunum|prosedürü|prosedur|prosedüru|procedure|stored\s+procedure|fonksiyonu|fonksiyon|function|tetikleyici|trigger|isimli|isimde|adlı|adli|adında|adinda|adlı\s+(?:ana\s+)?tablo|isimli\s+(?:ana\s+)?tablo)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // English-leading patterns: "table X", "view X"
    private static readonly Regex PrefixPattern = new(
        @"\b(?:table|view|procedure|stored\s+procedure|function|trigger|tablo|tablosu|tetikleyici)\s+([A-Za-z_][\w]{2,})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "dbo.X", "[dbo].[X]", "schema.X" patterns — strip schema, keep name.
    private static readonly Regex SchemaQualified = new(
        @"\[?(\w+)\]?\.\[?([\w]{2,})\]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // CamelCase / PascalCase identifiers with 2+ humps OR known prefixes (qt*, pp*, fnc*, sp*, vw*).
    private static readonly Regex CamelCasePattern = new(
        @"\b((?:qt|pp|fnc|fn|sp|vw|prc|tbl|trg)[A-Z]\w{2,})\b",
        RegexOptions.Compiled);

    private static readonly Regex PascalMultiHumpPattern = new(
        @"\b([A-Z][a-z]{2,}[A-Z]\w{2,})\b",
        RegexOptions.Compiled);

    /// <summary>Stop-words: Turkish/English common words that look like identifiers but aren't.</summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "var", "yok", "kaç", "kac", "nedir", "nasıl", "nasil", "hangi", "kim",
        "tablo", "tablosu", "view", "kolon", "kolonlar", "kolonlari", "kolonları",
        "tabloda", "veritabani", "veritabanı", "database",
        "table", "tables", "column", "columns", "schema", "field", "fields",
        "what", "where", "which", "how", "many", "list",
        "anlat", "göster", "goster", "söyle", "soyle",
    };

    /// <summary>
    /// Pull candidate object names from <paramref name="query"/>. Returns
    /// distinct, case-insensitive set of plain identifiers (no schema prefix).
    /// Empty array if nothing looks like an object name — safe default
    /// (HybridSearch falls back to vector + FTS only).
    /// </summary>
    public static string[] Extract(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<string>();

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) Schema-qualified strip: "dbo.Quotation" or "[dbo].[Quotation]" → "Quotation"
        foreach (Match m in SchemaQualified.Matches(query))
        {
            var name = m.Groups[2].Value;
            if (!Stopwords.Contains(name) && name.Length >= 3)
                found.Add(name);
        }

        // 2) Suffix patterns: "Quotation tablosu" → "Quotation"
        foreach (Match m in SuffixPattern.Matches(query))
        {
            var name = m.Groups[1].Value;
            if (!Stopwords.Contains(name) && name.Length >= 3)
                found.Add(name);
        }

        // 3) Prefix patterns: "table Quotation" / "tablo Quotation" → "Quotation"
        foreach (Match m in PrefixPattern.Matches(query))
        {
            var name = m.Groups[1].Value;
            if (!Stopwords.Contains(name) && name.Length >= 3)
                found.Add(name);
        }

        // 4) CamelCase prefix-typed identifiers: qtPaymentPlan, vwOrders, sp_RunReport
        foreach (Match m in CamelCasePattern.Matches(query))
            found.Add(m.Groups[1].Value);

        // 5) PascalCase multi-hump: MyOrderHistory, CustomerSettings (skip single-hump like Quotation
        //    — those are picked up by suffix/schema patterns, false-positive risk too high otherwise)
        foreach (Match m in PascalMultiHumpPattern.Matches(query))
        {
            var name = m.Groups[1].Value;
            if (!Stopwords.Contains(name))
                found.Add(name);
        }

        return found.ToArray();
    }
}
