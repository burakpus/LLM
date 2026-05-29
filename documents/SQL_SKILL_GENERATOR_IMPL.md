# SQL Skill Generator — Implementation Guide

Mevcut SQL Connections admin sayfasına "Generate Skill" butonu ekler.
Backend schema'yı okur, LiteLLM üzerinden skill .md üretir, Skills/ klasörüne yazar.

---

## 1. Backend — `dotnet/Api/Endpoints/MapSql.cs`

Dosyanın sonuna, diğer `app.MapPost` endpoint'lerinin yanına ekle:

```csharp
app.MapPost("/api/admin/sql-connections/{id}/generate-skill",
    [Authorize("AdminOnly")] async (
        int id,
        HttpContext ctx,
        NpgsqlDataSource db,
        IConfiguration cfg,
        IWebHostEnvironment env,
        IHttpClientFactory httpClientFactory) =>
    {
        await using var conn = await db.OpenConnectionAsync();

        var sqlConn = await conn.QuerySingleOrDefaultAsync<dynamic>(
            "SELECT * FROM sql_connections WHERE id = @id", new { id });
        if (sqlConn is null) return Results.NotFound();

        // Tablo + kolon metadata — mevcut /tables endpoint'indeki SQL ile aynı
        var tables = await conn.QueryAsync<dynamic>(@"
            SELECT
                t.TABLE_SCHEMA  AS [Schema],
                t.TABLE_NAME    AS [Name],
                c.COLUMN_NAME   AS ColumnName,
                c.DATA_TYPE     AS DataType,
                c.IS_NULLABLE   AS IsNullable,
                c.ORDINAL_POSITION AS Position
            FROM INFORMATION_SCHEMA.TABLES t
            JOIN INFORMATION_SCHEMA.COLUMNS c
                ON c.TABLE_NAME = t.TABLE_NAME AND c.TABLE_SCHEMA = t.TABLE_SCHEMA
            WHERE t.TABLE_TYPE = 'BASE TABLE'
            ORDER BY t.TABLE_SCHEMA, t.TABLE_NAME, c.ORDINAL_POSITION");

        // Tablo başına grupla
        var grouped = tables
            .GroupBy(r => $"{r.Schema}.{r.Name}")
            .Select(g => new {
                Schema  = (string)g.First().Schema,
                Name    = (string)g.First().Name,
                Columns = g.Select(c => new {
                    Name       = (string)c.ColumnName,
                    DataType   = (string)c.DataType,
                    IsNullable = (string)c.IsNullable == "YES"
                }).ToList()
            }).ToList();

        if (grouped.Count == 0)
            return Results.BadRequest(new { error = "No tables found in this connection." });

        // Schema text üret (14K char cap — LLM context için)
        var schemaText = SqlSkillGenerator.BuildSchemaText(grouped);

        // LiteLLM üzerinden skill üret
        var liteLlmBase = cfg["LiteLLM:BaseUrl"] ?? "http://localhost:4000";
        var model       = cfg["SkillGen:Model"]  ?? "qwen3-27b";
        var http        = httpClientFactory.CreateClient();

        var skillMd = await SqlSkillGenerator.GenerateAsync(
            http, liteLlmBase, model,
            (string)sqlConn.name, schemaText);

        // Skills/ klasörüne yaz
        var skillsDir = Path.Combine(env.ContentRootPath, "Skills");
        var slug      = Regex.Replace(
            ((string)sqlConn.name).ToLowerInvariant(),
            @"[^a-z0-9]+", "-").Trim('-');
        var fileName  = $"{slug}-db-model.md";
        var skillPath = Path.Combine(skillsDir, fileName);

        await File.WriteAllTextAsync(skillPath, skillMd);

        await ActivityLogger.LogAsync(conn, ctx,
            "skill.generate_from_db", "Data", "Info",
            new { connectionId = id, skillFile = fileName, tables = grouped.Count });

        return Results.Ok(new {
            skillFile = fileName,
            tables    = grouped.Count,
            chars     = skillMd.Length,
            note      = "Skill saved to Skills/. Visible after next service restart."
        });
    });
```

---

## 2. Yeni dosya — `dotnet/Api/Sql/SqlSkillGenerator.cs`

```csharp
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SetYazilim.Llm.Api.Sql;

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
         "Markdown table with columns: Issue | Detection Logic. Include 7 reconciliation checks relevant to the schema: closed records with outstanding balance, unmatched FK references, amount component sum mismatch, duplicate rows, missing required dates, GL entry gaps, allocation inconsistencies.")
    ];

    public static string BuildSchemaText(IEnumerable<dynamic> tables)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== TABLES ===");

        foreach (var t in tables)
        {
            sb.AppendLine($"\nTABLE: {t.Schema}.{t.Name}");
            sb.AppendLine("COLUMNS:");
            foreach (var c in t.Columns)
            {
                var nullable = c.IsNullable ? "NULL" : "NOT NULL";
                sb.AppendLine($"  {c.Name} {c.DataType} {nullable}");
            }

            if (sb.Length > 14_000)
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
        string model,
        string connectionName,
        string schemaText)
    {
        var parts = new List<string>
        {
            $"# SQL Skill Reference — {connectionName}\n\n" +
            "Standalone reference for writing reports and queries without needing source SQL files.\n" +
            "Covers schema, column semantics, code dictionaries, formulas, blueprints, and helpers.\n\n---\n"
        };

        foreach (var (title, instruction) in Sections)
        {
            var prompt = $"""
                You are a senior database architect writing a SQL skill reference document.
                Style target: cfs-db-model.md — concise, technical, dense with semantic information.
                Language: English only. SQL dialect: T-SQL (MSSQL).
                Connection: {connectionName}

                SCHEMA:
                {schemaText}

                TASK: {instruction}

                Rules:
                - Output Markdown ONLY — no preamble, no "Here is the section...", no meta-commentary
                - Start directly with ## {title}
                - Use exact table and column names from the schema above
                - For money/decimal columns always note ISNULL/COALESCE discipline
                """;

            var payload = JsonSerializer.Serialize(new
            {
                model,
                max_tokens = 1500,
                messages   = new[] { new { role = "user", content = prompt } }
            });

            try
            {
                var resp = await http.PostAsync(
                    $"{liteLlmBase}/v1/chat/completions",
                    new StringContent(payload, Encoding.UTF8, "application/json"));

                if (!resp.IsSuccessStatusCode) continue;

                var json = await resp.Content.ReadAsStringAsync();
                using var doc  = JsonDocument.Parse(json);
                var sectionMd  = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? string.Empty;

                parts.Add("\n" + sectionMd.Trim() + "\n");
            }
            catch
            {
                // Section atla, diğerlerine devam et
            }
        }

        return string.Join("\n", parts);
    }
}
```

---

## 3. `appsettings.json`

```json
"SkillGen": {
  "Model": "qwen3-27b"
}
```

---

## 4. Frontend — `SqlConnectionsTab.tsx`

### State ekle (component üstüne):

```tsx
const [generatingSkill, setGeneratingSkill] = useState<number | null>(null);
const [skillResult, setSkillResult]         = useState<Record<number, { ok: boolean; msg: string }>>({});
```

### Handler ekle:

```tsx
async function handleGenerateSkill(connId: number) {
  setGeneratingSkill(connId);
  setSkillResult(prev => ({ ...prev, [connId]: { ok: true, msg: '' } }));
  try {
    const res  = await fetch(`/api/admin/sql-connections/${connId}/generate-skill`, {
      method:  'POST',
      headers: { Authorization: `Bearer ${token}` }
    });
    const data = await res.json();
    if (res.ok) {
      setSkillResult(prev => ({
        ...prev,
        [connId]: { ok: true, msg: `✓ ${data.skillFile} — ${data.tables} tables · ${data.chars.toLocaleString()} chars` }
      }));
    } else {
      setSkillResult(prev => ({
        ...prev,
        [connId]: { ok: false, msg: `✗ ${data.error ?? 'Unknown error'}` }
      }));
    }
  } catch {
    setSkillResult(prev => ({
      ...prev,
      [connId]: { ok: false, msg: '✗ Network error' }
    }));
  } finally {
    setGeneratingSkill(null);
  }
}
```

### JSX — connection card içinde, mevcut action butonlarının yanına:

```tsx
<button
  onClick={() => handleGenerateSkill(conn.id)}
  disabled={generatingSkill === conn.id}
  className="btn-secondary text-sm gap-1"
  title="Generate SQL skill .md from schema and save to Skills/"
>
  {generatingSkill === conn.id ? (
    <><span className="animate-spin">⏳</span> Generating…</>
  ) : (
    <>🧠 Generate Skill</>
  )}
</button>

{skillResult[conn.id]?.msg && (
  <span className={`text-xs ml-2 ${skillResult[conn.id].ok ? 'text-green-600' : 'text-red-500'}`}>
    {skillResult[conn.id].msg}
  </span>
)}
```

---

## Notlar

**Süre:** 6 section × ~5 sn ≈ 30 sn. Endpoint sync çalışıyor — frontend `disabled` state ile bunu handle ediyor. 60 sn+ beklentisi varsa job kuyruğuna taşı.

**Skill görünürlüğü:** `Skills/` klasörüne yazılır ama `SkillRegistry` startup'ta eager-load yapıyor. Servis restart olmadan görünmesi için `SkillRegistry.ReloadAsync()` metodun varsa endpoint sonuna ekle:

```csharp
var registry = ctx.RequestServices.GetRequiredService<SkillRegistry>();
await registry.ReloadAsync(); // varsa
```

**Overwrite:** Aynı connection için tekrar çalıştırılırsa aynı dosyayı üzerine yazar (`File.WriteAllTextAsync`). Versiyon saklamak istersen `slug-db-model-{DateTime.Now:yyyyMMdd-HHmm}.md` yap.

**SQL bağlantısı:** Yukarıdaki `INFORMATION_SCHEMA` sorgusu SET LLM'deki kayıtlı SQL Server'a değil, PostgreSQL'e gidiyor. Doğru bağlantı `sql_connections` tablosundaki şifre ile ayrı bir `SqlConnection` açmalı — mevcut `tables` endpoint'indeki bağlantı mantığını kullan, aynı pattern.
