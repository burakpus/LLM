namespace SetYazilim.Llm.Retrieval;

/// <summary>
/// Türkçe ↔ İngilizce eşanlamlı genişletme — RAG sorgu kanalı için.
///
/// Kullanıcı "vergi" yazınca embedding/FTS sorgusu otomatik olarak
/// "vergi vat kdv tax" haline gelir → İngilizce kolon adları (VATAmount,
/// VATRate, TaxRate) doğru bulunur. Orijinal kullanıcı sorgusu LLM'e
/// olduğu gibi gönderilir, sadece retrieval kanalı genişletilmiş query
/// kullanır.
///
/// Eşanlamlı sözlüğü <see cref="Synonyms"/>'da tanımlanmıştır — finans/CRM
/// domain'ine özel. Yeni terim eklemek için tek dosya değişikliği yeterli.
/// </summary>
public static class TurkishSynonymExpander
{
    /// <summary>Maximum expanded query length (embedding model limit guard).</summary>
    private const int MaxLength = 512;

    /// <summary>
    /// Term → synonym list. Match is case-insensitive, whole-token (handles Turkish suffixes
    /// loosely via startsWith heuristic — see <see cref="Expand"/>).
    /// </summary>
    public static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Finansal kavramlar (TR vergi sistemine özel kısaltmalar)
        // KKDF: Kaynak Kullanımı Destekleme Fonu — finansman vergisi
        // BMSV: Banka Sigorta Muameleleri Vergisi
        // ÖTV : Özel Tüketim Vergisi
        // gvk : Gelir Vergisi Kanunu
        // tckn/vkn: Türk Kimlik No / Vergi Kimlik No
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

    /// <summary>
    /// Verilen sorguyu eşanlamlılarla genişletir. Orijinal token'lar korunur,
    /// eşleşen her terim için synonym listesi eklenir. Duplicate edilmez,
    /// sonuç MaxLength karakterde sınırlanır.
    /// </summary>
    public static string Expand(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return query ?? string.Empty;

        // Split keeping original casing for the output, lowercase for matching.
        var tokens = query.Split(new[] { ' ', '\t', '\n', '\r', ',', '.', ';', ':', '?', '!', '(', ')', '"', '\'' },
                                 StringSplitOptions.RemoveEmptyEntries);
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tokens) seen.Add(t);

        var additions = new List<string>();
        foreach (var t in tokens)
        {
            // Direct match
            if (Synonyms.TryGetValue(t, out var direct))
            {
                foreach (var s in direct)
                    if (seen.Add(s)) additions.Add(s);
                continue;
            }
            // Loose match — Turkish suffix tolerance. "vergisi", "vergiler" etc.
            // match "vergi" if the dictionary key is a prefix of the token (>=4 char keys only).
            foreach (var (key, syns) in Synonyms)
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
}
