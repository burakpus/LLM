using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace SetYazilim.Llm.Retrieval;

/// <summary>
/// Türkçe ↔ İngilizce eşanlamlı genişletme — RAG sorgu kanalı için.
///
/// Kullanıcı "vergi" yazınca embedding/FTS sorgusu otomatik olarak
/// "vergi vat kdv tax kkdf bmsv" haline gelir → İngilizce kolon adları (VATAmount,
/// VATRate, qtKKDF, qtBMSV) doğru bulunur. Orijinal kullanıcı sorgusu LLM'e
/// olduğu gibi gönderilir, sadece retrieval kanalı genişletilmiş query kullanır.
///
/// Sözlük DB'de (`rag_synonyms` tablosu). İlk açılışta hardcoded
/// <see cref="RagSynonymService.Defaults"/> ile seed edilir. Admin paneli üzerinden CRUD yapılabilir
/// (POST/PUT/DELETE /api/admin/rag/synonyms). 60sn IMemoryCache → DB yükü minimal.
/// </summary>
public interface IRagSynonymService
{
    string Expand(string query);
    Task<IReadOnlyDictionary<string, string[]>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(string term, string[] synonyms, string? notes, string updatedBy, CancellationToken ct = default);
    Task<bool> DeleteAsync(string term, CancellationToken ct = default);
    void InvalidateCache();
}

public sealed class RagSynonymService : IRagSynonymService
{
    /// <summary>Maximum expanded query length (embedding model 2048-token limit guard).</summary>
    private const int MaxLength = 512;
    /// <summary>Cache TTL — 60s balances freshness vs DB round-trip on every chat.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private const string CacheKey = "ragSynonyms";

    private readonly NpgsqlDataSource _ds;
    private readonly IMemoryCache     _cache;
    private readonly ILogger<RagSynonymService> _log;

    public RagSynonymService(NpgsqlDataSource ds, IMemoryCache cache, ILogger<RagSynonymService> log)
    {
        _ds = ds; _cache = cache; _log = log;
    }

    public string Expand(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return query ?? string.Empty;
        var dict = GetCachedSync();
        return ExpandWith(query, dict);
    }

    public async Task<IReadOnlyDictionary<string, string[]>> GetAllAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out object? raw) && raw is Dictionary<string, string[]> cached)
            return cached;
        var fresh = await LoadFromDbAsync(ct);
        Cache(fresh);
        return fresh;
    }

    public async Task UpsertAsync(string term, string[] synonyms, string? notes, string updatedBy, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO rag_synonyms (term, synonyms, notes, created_by, updated_at)
                            VALUES ($1, $2, $3, $4, NOW())
                            ON CONFLICT (term) DO UPDATE
                            SET synonyms=$2, notes=$3, created_by=$4, updated_at=NOW()";
        cmd.Parameters.AddWithValue(term.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue(synonyms);
        cmd.Parameters.AddWithValue(notes ?? "");
        cmd.Parameters.AddWithValue(updatedBy);
        await cmd.ExecuteNonQueryAsync(ct);
        InvalidateCache();
    }

    public async Task<bool> DeleteAsync(string term, CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM rag_synonyms WHERE term=$1";
        cmd.Parameters.AddWithValue(term.Trim().ToLowerInvariant());
        var n = await cmd.ExecuteNonQueryAsync(ct);
        if (n > 0) InvalidateCache();
        return n > 0;
    }

    public void InvalidateCache() => _cache.Remove(CacheKey);

    // ── Internal: sync cache fetch (used by Expand, called per chat — must be fast) ──
    private Dictionary<string, string[]> GetCachedSync()
    {
        if (_cache.TryGetValue(CacheKey, out object? raw) && raw is Dictionary<string, string[]> cached)
            return cached;
        // Cache miss — load synchronously (acceptable: <50ms DB roundtrip, happens once/min)
        try
        {
            var fresh = LoadFromDbAsync(CancellationToken.None).GetAwaiter().GetResult();
            Cache(fresh);
            return fresh;
        }
        catch (Exception ex)
        {
            // DB hiccup — fall back to defaults so retrieval keeps working
            _log.LogWarning(ex, "RagSynonymService DB load failed, falling back to defaults");
            return new Dictionary<string, string[]>(Defaults, StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<Dictionary<string, string[]>> LoadFromDbAsync(CancellationToken ct)
    {
        var dict = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        await using var conn = await _ds.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT term, synonyms FROM rag_synonyms";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            dict[r.GetString(0)] = (string[])r.GetValue(1);
        return dict;
    }

    private void Cache(IReadOnlyDictionary<string, string[]> dict)
    {
        using var entry = _cache.CreateEntry(CacheKey);
        entry.Value = dict is Dictionary<string, string[]> d
            ? d
            : new Dictionary<string, string[]>(dict, StringComparer.OrdinalIgnoreCase);
        entry.AbsoluteExpirationRelativeToNow = CacheTtl;
        entry.Size = 1;
    }

    /// <summary>
    /// Pure expansion logic — separated so we can unit-test without DB.
    /// </summary>
    public static string ExpandWith(string query, IReadOnlyDictionary<string, string[]> synonyms)
    {
        if (string.IsNullOrWhiteSpace(query)) return query ?? string.Empty;

        var tokens = query.Split(new[] { ' ', '\t', '\n', '\r', ',', '.', ';', ':', '?', '!', '(', ')', '"', '\'' },
                                 StringSplitOptions.RemoveEmptyEntries);
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tokens) seen.Add(t);

        var additions = new List<string>();
        foreach (var t in tokens)
        {
            if (synonyms.TryGetValue(t, out var direct))
            {
                foreach (var s in direct)
                    if (seen.Add(s)) additions.Add(s);
                continue;
            }
            // Loose suffix-tolerant prefix match (Turkish: "vergisi", "vergiler" → "vergi")
            foreach (var (key, syns) in synonyms)
            {
                if (key.Length >= 4 && t.Length > key.Length
                    && t.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var s in syns)
                        if (seen.Add(s)) additions.Add(s);
                    break;
                }
            }
        }

        if (additions.Count == 0) return query;
        var expanded = query + " " + string.Join(" ", additions);
        return expanded.Length > MaxLength ? expanded[..MaxLength] : expanded;
    }

    // ── Default seed dictionary ───────────────────────────────────────────────
    // Used at startup (via HostedService) if rag_synonyms table is empty.
    // Also fallback when DB read fails so retrieval continues to work.
    public static readonly IReadOnlyDictionary<string, string[]> Defaults = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        // ── Finansal kavramlar (TR vergi sistemine özel kısaltmalar)
        ["vergi"]    = ["vat", "kdv", "tax", "taxation", "kkdf", "bmsv", "ötv", "otv"],
        ["kdv"]      = ["vat", "tax", "vergi"],
        ["kkdf"]     = ["vergi", "tax", "fund", "fon"],
        ["bmsv"]     = ["vergi", "tax", "banka sigorta"],
        ["ötv"]      = ["vergi", "tax", "ozel tuketim"],
        ["otv"]      = ["vergi", "tax", "özel tüketim"],
        ["müşteri"]  = ["customer", "client", "cari", "musteri"],
        ["musteri"]  = ["customer", "client", "cari", "müşteri"],
        ["cari"]     = ["customer", "client", "müşteri", "musteri"],
        ["ödeme"]    = ["payment", "tahsilat", "odeme"],
        ["odeme"]    = ["payment", "tahsilat", "ödeme"],
        ["tahsilat"] = ["payment", "collection", "ödeme"],
        ["fatura"]   = ["invoice", "bill"],
        ["teklif"]   = ["quotation", "quote", "offer"],
        ["sözleşme"] = ["contract", "agreement", "sozlesme"],
        ["sozlesme"] = ["contract", "agreement", "sözleşme"],
        ["sipariş"]  = ["order", "salesorder", "siparis"],
        ["siparis"]  = ["order", "salesorder", "sipariş"],
        ["stok"]     = ["stock", "inventory", "envanter"],
        ["envanter"] = ["inventory", "stock", "stok"],
        ["depo"]     = ["warehouse", "depot", "location"],
        ["bakiye"]   = ["balance", "remaining"],
        ["limit"]    = ["limit", "creditlimit", "credit limit", "kredi limiti"],
        ["kredi"]    = ["credit", "loan", "financing"],
        ["tutar"]    = ["amount", "total", "money"],
        ["fiyat"]    = ["price", "cost", "rate"],
        ["para"]     = ["money", "amount", "currency"],
        ["döviz"]    = ["currency", "fx", "exchange", "doviz"],
        ["doviz"]    = ["currency", "fx", "exchange", "döviz"],
        ["banka"]    = ["bank", "branch"],
        ["hesap"]    = ["account", "ledger"],
        ["iban"]     = ["iban", "bank account"],
        ["faiz"]     = ["interest", "interestrate", "rate"],
        ["vade"]     = ["maturity", "duedate", "term"],

        // ── İş süreçleri
        ["ürün"]     = ["product", "item", "material", "urun"],
        ["urun"]     = ["product", "item", "material", "ürün"],
        ["malzeme"]  = ["material", "item", "product"],
        ["proje"]    = ["project"],
        ["servis"]   = ["service"],
        ["şube"]     = ["branch", "office", "sube"],
        ["sube"]     = ["branch", "office", "şube"],
        ["şirket"]   = ["company", "corporation", "sirket"],
        ["sirket"]   = ["company", "corporation", "şirket"],
        ["personel"] = ["employee", "staff", "user"],
        ["kullanıcı"]= ["user", "operator", "username", "kullanici"],
        ["kullanici"]= ["user", "operator", "username", "kullanıcı"],

        // ── Veri/yapı kavramları
        ["tarih"]    = ["date", "datetime", "timestamp"],
        ["zaman"]    = ["time", "datetime"],
        ["açıklama"] = ["description", "note", "remark", "aciklama"],
        ["aciklama"] = ["description", "note", "remark", "açıklama"],
        ["durum"]    = ["status", "state"],
        ["tip"]      = ["type", "kind"],
        ["kategori"] = ["category", "group"],
        ["adres"]    = ["address", "addr"],
        ["telefon"]  = ["phone", "mobile", "gsm"],
        ["adı"]      = ["name", "title", "adi"],
        ["adi"]      = ["name", "title", "adı"],
        ["kimlik"]   = ["identity", "id", "tckn"],
        ["kolon"]    = ["column", "field"],
        ["tablo"]    = ["table"],
        ["alan"]     = ["field", "column"],
    };
}

/// <summary>
/// Startup task: if rag_synonyms table is empty, seed from Defaults so the
/// service starts up usefully without manual config. Runs once per app start.
/// </summary>
public sealed class RagSynonymSeeder : IHostedService
{
    private readonly NpgsqlDataSource _ds;
    private readonly ILogger<RagSynonymSeeder> _log;
    public RagSynonymSeeder(NpgsqlDataSource ds, ILogger<RagSynonymSeeder> log) { _ds = ds; _log = log; }

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await _ds.OpenConnectionAsync(ct);
            await using (var check = conn.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM rag_synonyms";
                var n = Convert.ToInt64(await check.ExecuteScalarAsync(ct));
                if (n > 0) { _log.LogInformation("RagSynonymSeeder: {N} rows already present, skipping seed", n); return; }
            }
            int seeded = 0;
            foreach (var (term, syns) in RagSynonymService.Defaults)
            {
                await using var ins = conn.CreateCommand();
                ins.CommandText = @"INSERT INTO rag_synonyms (term, synonyms, notes, created_by)
                                    VALUES ($1, $2, $3, 'system')
                                    ON CONFLICT (term) DO NOTHING";
                ins.Parameters.AddWithValue(term);
                ins.Parameters.AddWithValue(syns);
                ins.Parameters.AddWithValue("Default seed — bilingual TR↔EN finance/CRM synonym");
                seeded += await ins.ExecuteNonQueryAsync(ct);
            }
            _log.LogInformation("RagSynonymSeeder: seeded {N} default synonyms", seeded);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "RagSynonymSeeder failed (non-fatal — Expander will use Defaults at runtime)");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Static facade for backwards-compat — DI olmadan çağrılabilen yerler için.</summary>
public static class TurkishSynonymExpander
{
    private static IRagSynonymService? _service;
    public static void Configure(IRagSynonymService service) => _service = service;
    public static string Expand(string query)
        => _service?.Expand(query) ?? RagSynonymService.ExpandWith(query, RagSynonymService.Defaults);
}
