using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SetYazilim.Llm.Api.Sql;

/// <summary>
/// Generates a static SQL-domain skill .md file from a connection's live schema.
///
/// IZOLE: SQL RAG akışına HİÇBİR dokunuş yok. Bu modül sadece existing
/// SqlDataSampler.ListTablesAsync sonucunu (Name+DataType+IsPII) tüketir,
/// LiteLLM'e gönderir, dönen Markdown'ı Skills/ klasörüne yazar.
///
/// Üretilen .md dosyası kb_documents'a yazılmaz, RAG retrieval'a girmez.
/// Kullanıcı sohbet ekranında bu skill'i seçtiğinde tüm dosya doğrudan
/// sistem prompt'una eklenir (tıpkı el-yazma cfs-db-model.md gibi).
///
/// Bölüm yapısı SQL_SKILL_GENERATOR_IMPL.md tasarımına göre 6 başlık. Her
/// bölüm ayrı bir LiteLLM çağrısı — tek bölümün hatası dokümanın tamamını
/// kaybetmemeli (try/catch ile sessizce atlanır).
/// </summary>
public static class SqlSkillGenerator
{
    private static readonly (string Title, string Instruction)[] Sections =
    [
        ("Domain Overview",
         "Write a Domain Overview section: schema purpose, business domain, numbered logical layers with table names. Short intro paragraph then numbered list."),

        ("Table Catalog with Column Definitions",
         "For EVERY table: ### heading with table name, one sentence describing business purpose, markdown table with columns: Column | Type | Description. Description must explain business semantics — not repeat the column name. Preserve exact column names and types."),

        ("Code Dictionaries",
         "Find all columns with status/type/enum-like codes (StatusCode, TypeID, CreditCode, etc). For each: subsection heading + table: Code | Meaning. Infer meanings from column names and domain context."),

        ("Query Blueprints",
         "Produce 5 representative T-SQL patterns developers commonly need. Each: ### heading, brief description, complete T-SQL using exact table/column names. Cover: status filtering, joins, financial aggregation, date range queries, NULL-safe arithmetic with ISNULL."),

        ("Common Anti-patterns and Pitfalls",
         "List 5 technical pitfalls specific to this schema. Each: numbered heading, problem description, correct approach. Cover: NULL arithmetic in money columns (ISNULL wrapping), CTE vs #temp materialisation for reused result sets, OPTION (HASH JOIN) risks (Msg 8622), scalar UDF serialisation, index usage on large tables."),

        ("Data Quality Control Checklist",
         "Markdown table with columns: Issue | Detection Logic. Include 7 reconciliation checks relevant to the schema: closed records with outstanding balance, unmatched FK references, amount component sum mismatch, duplicate rows, missing required dates, GL entry gaps, allocation inconsistencies."),
    ];

    // Top-N strategy (Job pipeline) zaten ön-filtreleme yapıyor. Cap 30K'ya
    // çekildi çünkü 100 tablo × ~30 kolon × ~25 char ≈ 75K worst-case; ortalamada
    // 30K civarı yeterli. Bu LLM'in görebileceği üst sınırdır — gerçek schema
    // seçimi job tarafında yapılır.
    private const int MaxSchemaChars = 30_000;

    /// <summary>
    /// Şema dump'ı — sadece TableInfo'nun bilinen alanlarını kullanır.
    /// Nullable/ObjectType gibi extra alanlara dokunmaz; mevcut SqlDataSampler
    /// kontratı korunur.
    /// </summary>
    public static string BuildSchemaText(IEnumerable<TableInfo> tables)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== TABLES ===");

        foreach (var t in tables)
        {
            sb.Append("\nTABLE: ").AppendLine(t.QualifiedName);
            sb.AppendLine("COLUMNS:");
            foreach (var c in t.Columns)
                sb.Append("  ").Append(c.Name).Append(' ').AppendLine(c.DataType);

            if (sb.Length > MaxSchemaChars)
            {
                sb.AppendLine("\n... [schema truncated for context window]");
                break;
            }
        }

        return sb.ToString();
    }

    public static async Task<string> GenerateAsync(
        HttpClient http,
        string liteLlmBase,
        string? liteLlmApiKey,
        string model,
        string connectionName,
        string schemaText,
        Func<int, int, string, Task>? onSectionDone = null,
        CancellationToken ct = default)
    {
        var parts = new List<string>
        {
            $"# SQL Skill Reference — {connectionName}\n\n" +
            "Standalone reference for writing reports and queries without needing source SQL files.\n" +
            "Covers schema, column semantics, code dictionaries, formulas, blueprints, and helpers.\n\n---\n",
        };

        for (int sIdx = 0; sIdx < Sections.Length; sIdx++)
        {
            var (title, instruction) = Sections[sIdx];
            var prompt =
                "You are a senior database architect writing a SQL skill reference document.\n" +
                "Style target: cfs-db-model.md — concise, technical, dense with semantic information.\n" +
                "Language: English only. SQL dialect: T-SQL (MSSQL).\n" +
                "Connection: " + connectionName + "\n\n" +
                "SCHEMA:\n" + schemaText + "\n\n" +
                "TASK: " + instruction + "\n\n" +
                "Rules:\n" +
                "- Output Markdown ONLY — no preamble, no \"Here is the section...\", no meta-commentary\n" +
                "- Start directly with ## " + title + "\n" +
                "- Use exact table and column names from the schema above\n" +
                "- For money/decimal columns always note ISNULL/COALESCE discipline\n";

            var payload = JsonSerializer.Serialize(new
            {
                model,
                max_tokens = 1500,
                messages   = new[] { new { role = "user", content = prompt } },
            });

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{liteLlmBase.TrimEnd('/')}/v1/chat/completions");
                req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                if (!string.IsNullOrWhiteSpace(liteLlmApiKey))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", liteLlmApiKey);

                using var resp = await http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) continue;

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var sectionMd = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? string.Empty;

                parts.Add("\n" + sectionMd.Trim() + "\n");
            }
            catch
            {
                // Section atla, dokümanın geri kalanı kaybolmasın
            }

            // Progress callback — job pipeline'ın UI'a yansıtması için
            if (onSectionDone is not null)
            {
                try { await onSectionDone(sIdx + 1, Sections.Length, title); }
                catch { /* callback hatası job'u bozmamalı */ }
            }
        }

        return string.Join("\n", parts);
    }
}
