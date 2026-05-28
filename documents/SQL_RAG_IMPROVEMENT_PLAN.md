# SQL RAG İyileştirme — Coding Agent Faz Planı

> **İlerleme (2026-05-29)**:
> - ✅ Faz 1 (structured chunking + collection separation) tamam
> - ✅ Faz 2 (Türkçe synonym expansion) tamam
> - ✅ TASK-2.3 (synonym admin UI) tamam — DB-backed, admin panelinden CRUD
> - 🟡 Faz 3 (Reranker) **kısmen tamam**: `IRerankService` abstraction + `LlmReranker`
>      (DB-GPT'den ilham — mevcut Gemma chat modelini kullanır, ekstra VRAM gerektirmez)
>      + `CrossEncoderRerankService` skeleton (bge-reranker-v2-m3 deploy edildiğinde aktif).
>      `CompositeRerankService` strategy ile yönetilir: `llm | crossencoder | auto | off`.
>      Default: `llm`. Wire'lı, ContextBuilder `HybridSearch top-20 → rerank → top-6`.
> - ⚠ Faz 3 kalan: cross-encoder vLLM service'ini docker-compose'a ekleme (VRAM bütçesi)
> - ❌ Faz 4 (Relation graph) — beklemede



> **Platform:** SET LLM · .NET 8 Minimal API · pgvector · nomic-embed-text-v1.5 · PostgreSQL 16  
> **Hedef:** 11,040 SQL objesinin (4527 table + 3825 proc + 1921 view + 746 fn + 24 trigger)  
> RAG retrieval kalitesini artırmak — özellikle Türkçe doğal dil sorgularında kolon bulma doğruluğu.  
> **Kural:** Her faz sonunda `scripts/e2e-test.sh` tüm senaryolar PASS olmalı. Regresyon = blocker.

---

## Mevcut Durum

| Metrik | Değer |
|--------|-------|
| Toplam obje | 11,040 |
| Toplam chunk | 59,729 |
| Chunk/obje ort. | **~5.4** ← kritik sorun |
| Collection sayısı | 1 (`sql-cfs-vizyon`) |
| Embedding model | nomic-embed-text-v1.5 (768d) |
| Search | HybridSearch (pgvector + Postgres FTS) — ZATEN VAR |
| Synonym expansion | YOK |
| Reranker | YOK |

**Kök sorun:** Tablolar token limit aşınca mekanik text split ile bölünüyor.  
Table kolonları birbirinden kopuk chunk'lara dağılıyor → retrieval "VATAmount var mı?" sorusunu cevaplayamıyor.

---

## Faz 1 — Structured Chunking + Collection Separation

> **Efor:** 1-2 gün · **Risk:** Düşük · **Beklenen kazanım:** Kolon bulma doğruluğu +40-50%

### Bağlam

- Schema ingest: `dotnet/Api/Endpoints/MapSql.cs` → `POST /api/admin/sql-connections/{id}/ingest-schema`
- Job handler: `dotnet/Api/Jobs/` içinde `SqlSchemaIngestJobHandler` (veya benzeri)
- RAG insert: `documents` tablosuna pgvector embedding ile INSERT
- Mevcut collection field: `documents.collection` string kolonu

---

### TASK-1.1 — Structured Chunk Builder

**Dosya:** `dotnet/Api/Sql/SqlSchemaChunkBuilder.cs` (YENİ)

```
[ ] SqlSchemaChunkBuilder static class oluştur
[ ] BuildTableChunk(SqlObject obj) → string metodu
    - Format:
        OBJECT_TYPE: TABLE
        SCHEMA: dbo
        NAME: Quotation
        
        COLUMNS:
          Id int NOT NULL
          VATAmount decimal(18,2) -- KDV tutarı
          VATRate decimal(5,2)    -- KDV oranı
          CustomerId int NOT NULL
        
        RELATED_TABLES: Invoice, QuotationScenario
        TAGS: vat, kdv, vergi, tax, quotation, sales
    - Hiçbir koşulda token split uygulanmayacak
    - nomic-embed max 8192 token → table chunk bu sınırı aşamaz
      (aşıyorsa kolonları COLUMNS_OVERFLOW: bölümüne al, ayrı chunk DEĞİL, tek chunk içinde)
[ ] BuildViewChunk(SqlObject obj) → string — aynı format, OBJECT_TYPE: VIEW
[ ] BuildFunctionChunk(SqlObject obj) → string — OBJECT_TYPE: FUNCTION
[ ] BuildTriggerChunk(SqlObject obj) → string — OBJECT_TYPE: TRIGGER
[ ] BuildProcedureChunks(SqlObject obj) → IEnumerable<string>
    - SP body'yi logical section'lara böl:
      her CREATE/ALTER, SELECT blok başına PROCEDURE_SECTION: header ekle
    - Her section'a NAME: <sp_adı> prefixini koru
    - Max 1000 satır = 1 section heuristic
[ ] Unit test: Quotation tablosu için chunk üret → VATAmount, VATRate aynı chunk içinde mi?
```

---

### TASK-1.2 — Collection Name Strategy

**Dosya:** `dotnet/Api/Sql/SqlSchemaChunkBuilder.cs` veya ingest handler

```
[ ] GetCollectionName(string connectionName, string objectType) helper metodu:
    TABLE     → "{connectionName}-tables"
    VIEW      → "{connectionName}-views"
    PROCEDURE → "{connectionName}-procedures"
    FUNCTION  → "{connectionName}-functions"
    TRIGGER   → "{connectionName}-triggers"
    
    Örnek: "sql-cfs-vizyon-tables", "sql-cfs-vizyon-procedures"
[ ] Mevcut tek collection "sql-cfs-vizyon" → legacy, silinmeyecek (re-ingest sonrası hidden yapılacak)
```

---

### TASK-1.3 — Ingest Handler Güncelleme

**Dosya:** `dotnet/Api/Jobs/` → schema ingest job handler

```
[ ] Mevcut raw DDL text split mantığını SqlSchemaChunkBuilder çağrısıyla değiştir
[ ] Her obje için:
    - SqlSchemaChunkBuilder.Build*Chunk() çağır
    - GetCollectionName() ile doğru collection seç
    - Embed + INSERT documents tablosuna (mevcut flow)
[ ] SP'ler için BuildProcedureChunks() → birden fazla chunk INSERT
[ ] Re-ingest job'ı mevcut collection'daki eski chunk'ları önce sil
    (documents WHERE collection = '{connectionName}-%' AND source_object = obj.Name)
[ ] Progress logging değişmesin — mevcut job progress UI çalışmaya devam etmeli
```

---

### TASK-1.4 — Collection Settings Seed

**Dosya:** `dotnet/Api/Endpoints/MapSql.cs` veya ingest handler post-step

```
[ ] Re-ingest tamamlandığında collection_settings tablosuna UPSERT:
    tables     → priority='high',   data_type='sql-schema', description='SQL Tabloları'
    views      → priority='normal', data_type='sql-schema', description='SQL Viewler'
    procedures → priority='low',    data_type='sql-schema', description='Stored Procedures'
    functions  → priority='low',    data_type='sql-schema', description='Fonksiyonlar'
    triggers   → priority='low',    data_type='sql-schema', description='Triggerlar'
[ ] Eski "sql-cfs-vizyon" collection'ına priority='hidden' set et (retrieval'dan çıkar)
```

---

### TASK-1.5 — E2E Test Güncelleme

**Dosya:** `scripts/e2e-test.sh`

```
[ ] T10 güncelle: yeni collection isimleri ile obje sayısını doğrula
    (sql-cfs-vizyon-tables: 4527, sql-cfs-vizyon-procedures: 3825, ...)
[ ] T13 EKLE: "quotation tablosunda vat kolonları" RAG sorgusu → VATAmount içeriyor mu?
    (Admin API üzerinden veya /api/llm/completions mode=rag ile)
[ ] Tüm mevcut 14 test PASS olmalı
```

---

### Faz 1 Doğrulama Kriterleri

```
✓ Re-ingest sonrası chunk sayısı: ~11,040 (1:1 obje:chunk) — SP'ler hariç
✓ "sql-cfs-vizyon-tables" collection'da 4527 obje
✓ T13: VATAmount doğru chunk'tan retrieve ediliyor
✓ Tüm mevcut E2E testleri PASS
```

---

## Faz 2 — Türkçe Synonym Expansion

> **Efor:** 2-4 saat · **Risk:** Sıfır (additive) · **Beklenen kazanım:** Türkçe sorgu recall +25%

### Bağlam

- HybridSearch çağrısı: `dotnet/Api/` → RAG sorgu akışı, embed öncesi query işleme
- FTS (Postgres full-text search) mevcut — genişletilmiş query her iki kanala gider

---

### TASK-2.1 — TurkishSynonymExpander

**Dosya:** `dotnet/Api/Retrieval/TurkishSynonymExpander.cs` (YENİ)

```
[ ] Static class TurkishSynonymExpander
[ ] Dictionary<string, string[]> Synonyms başlangıç seti:
    vergi     → ["vat", "kdv", "tax", "taxation"]
    müşteri   → ["customer", "client", "cari", "carı"]
    ödeme     → ["payment", "tahsilat", "odeme"]
    fatura    → ["invoice", "bill", "invoice"]
    teklif    → ["quotation", "quote", "offer", "teklif"]
    limit     → ["credit limit", "kredi limiti", "creditlimit"]
    bakiye    → ["balance", "remaining", "bakiye"]
    sipariş   → ["order", "siparis", "sales order"]
    stok      → ["stock", "inventory", "envanter"]
    depo      → ["warehouse", "depot", "location"]
    malzeme   → ["item", "material", "product", "urun", "ürün"]
    para      → ["amount", "currency", "money", "tutar"]
    tarih     → ["date", "datetime", "timestamp"]
    kullanıcı → ["user", "username", "operator"]
    açıklama  → ["description", "note", "remark", "aciklama"]
    
[ ] string Expand(string query) metodu:
    - Query'yi lowercase token'lara böl
    - Her token için eşleşme varsa synonym'leri ekle
    - Orijinal query + genişletilmiş terimler döndür (boşlukla ayrılmış)
    - Max 512 karakter sınırı (embedding model için)
[ ] Unit test: "quotation tablosunda vergi kolonları" → "vat kdv tax" içeriyor mu?
```

---

### TASK-2.2 — HybridSearch Entegrasyonu

**Dosya:** RAG sorgu akışı (MapLlm.cs veya ilgili retrieval servisi)

```
[ ] Embed çağrısı öncesinde TurkishSynonymExpander.Expand(query) uygula
[ ] Genişletilmiş query hem vector embed hem FTS sorgusuna gönderilsin
[ ] Orijinal query değiştirilmesin — kullanıcıya gösterilen soru aynı kalır
[ ] Log: expanded query'yi debug log'a yaz (ILogger.LogDebug)
```

---

### TASK-2.3 — Synonym Dictionary Admin Endpoint (OPSİYONEL)

**Dosya:** `dotnet/Api/Endpoints/MapDocuments.cs` veya yeni `MapRag.cs`

```
[ ] GET  /api/admin/rag/synonyms → mevcut dictionary listesi
[ ] POST /api/admin/rag/synonyms → { term, synonyms[] } ekle/güncelle
[ ] Opsiyonel: DB'ye persist et (prompt_templates tablosu veya yeni rag_synonyms tablosu)
[ ] İlk iterasyonda hardcoded Dictionary yeterli — bu task ertelenebilir
```

---

### Faz 2 Doğrulama Kriterleri

```
✓ "vergi" sorgusu "VATAmount" içeren chunk'ı retrieve ediyor
✓ "müşteri limiti" sorgusu "CreditLimit" kolonunu buluyor
✓ Mevcut tüm E2E testleri PASS (additive değişiklik, regresyon riski sıfır)
```

---

## Faz 3 — Reranker Entegrasyonu

> **Efor:** 2-3 gün · **Risk:** Orta (yeni vLLM instance) · **Beklenen kazanım:** Precision +30%

### Bağlam

- DGX Spark üzerinde mevcut: Gemma 4 26B (port 8000), Qwen3 27B (port 8002), GPT-OSS 120B (port 8003), nomic-embed (port 8004)
- Reranker ayrı bir vLLM instance olarak deploy edilecek → `docker-compose.yml` güncellenir
- Model önerisi: **BAAI/bge-reranker-v2-m3** (cross-encoder, multilingual, MSSQL schema için iyi)

---

### TASK-3.1 — Reranker vLLM Deploy

**Dosya:** `docker-compose.yml`

```
[ ] bge-reranker-v2-m3 için yeni vLLM servis bloğu ekle (port 8005)
[ ] GPU memory budget kontrol et: mevcut 3 model + nomic-embed ne kadar kullanıyor?
    nvidia-smi → kalan VRAM yeterli mi?
[ ] appsettings.json'a yeni config:
    "Reranker": {
      "BaseUrl": "http://localhost:8005",
      "Model": "BAAI/bge-reranker-v2-m3",
      "Enabled": true,
      "TopN": 5
    }
[ ] docker-compose up -d reranker → sağlık kontrolü
```

---

### TASK-3.2 — RerankService

**Dosya:** `dotnet/Api/Retrieval/RerankService.cs` (YENİ)

```
[ ] IRerankService interface:
    Task<IEnumerable<RankedChunk>> RerankAsync(string query, IEnumerable<Chunk> candidates, int topN)

[ ] VllmRerankService implementasyonu:
    - POST /v1/rerank (vLLM cross-encoder endpoint) veya
      /v1/embeddings ile manual cross-score (model desteklemiyorsa fallback)
    - Request: { model, query, documents[] }
    - Response: score listesi → sırala → top N döndür
    - Timeout: 5 saniye (reranker yavaşsa skip et — fallback: hybrid score sıralaması)
    
[ ] DI: builder.Services.AddSingleton<IRerankService, VllmRerankService>()
    (appsettings Reranker:Enabled=false ise NoOpRerankService inject et)
```

---

### TASK-3.3 — HybridSearch Pipeline Güncelleme

**Dosya:** RAG sorgu akışı

```
[ ] Mevcut akış: HybridSearch → top 5 chunk → LLM
[ ] Yeni akış:
    HybridSearch → top 20 chunk
         ↓
    RerankService.RerankAsync(query, top20, topN=5)
         ↓
    top 5 reranked chunk → LLM
    
[ ] collection_settings priority multiplier reranker'dan ÖNCE uygulanmaya devam etsin
    (hidden collection'lar candidate set'e hiç girmesin)
[ ] Reranker disabled ise eski davranış — feature flag ile kontrol
[ ] Latency logging: rerank süresi ms cinsinden debug log
```

---

### TASK-3.4 — E2E Test Güncelleme

**Dosya:** `scripts/e2e-test.sh`

```
[ ] T14 EKLE: Reranker health check — port 8005 cevap veriyor mu?
[ ] T13 güncelle: reranker etkinken "VATAmount" hâlâ top-5'te mi?
[ ] Mevcut tüm testler PASS
```

---

### Faz 3 Doğrulama Kriterleri

```
✓ Reranker port 8005 sağlıklı
✓ RAG sorgu latency < 3 saniye (reranker dahil)
✓ "quotation VATAmount" → top 1 sonuç doğru tablo chunk'ı
✓ Reranker:Enabled=false → eski davranış (regresyon yok)
✓ Tüm E2E testleri PASS
```

---

## Faz 4 — Metadata Enrichment + Relation Graph (Uzun Vade)

> **Efor:** 1-2 sprint · **Risk:** Yüksek (schema değişikliği) · **Beklenen kazanım:** Cross-table sorgu doğruluğu

> ⚠️ Faz 1-3 tamamlanmadan başlama. Bu faz Faz 1-3'ün üzerine inşa eder.

---

### TASK-4.1 — FK Relation Extraction

```
[ ] Schema ingest sırasında sys.foreign_keys sorgula:
    SELECT 
      OBJECT_NAME(fk.parent_object_id) AS parent_table,
      COL_NAME(fkc.parent_object_id, fkc.parent_column_id) AS parent_column,
      OBJECT_NAME(fk.referenced_object_id) AS referenced_table,
      COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) AS referenced_column
    FROM sys.foreign_keys fk
    JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id

[ ] SqlObject modeline Relations: List<FkRelation> ekle
[ ] BuildTableChunk içinde RELATED_TABLES: bölümüne FK hedeflerini yaz
```

---

### TASK-4.2 — SP → Table Dependency

```
[ ] sys.sql_expression_dependencies sorgula:
    SP içinde hangi tablolar/view'ler referans ediliyor?
[ ] BuildProcedureChunks içinde DEPENDS_ON: bölümüne ekle
[ ] Cross-collection retrieval: SP sorgulandığında ilgili table chunk'ları da fetch et
```

---

### TASK-4.3 — sql_object_relations Tablosu (OPSİYONEL)

```
[ ] Yeni DB tablosu:
    CREATE TABLE sql_object_relations (
      id SERIAL PRIMARY KEY,
      connection_id INT,
      source_object TEXT,
      source_type TEXT,
      target_object TEXT,
      relation_type TEXT, -- 'fk', 'view_uses', 'sp_uses', 'trigger_on'
      created_at TIMESTAMPTZ DEFAULT NOW()
    );
    
[ ] Re-ingest sırasında bu tabloyu populate et
[ ] HybridSearch'e "relation walk" ekle:
    Quotation retrieve edildiğinde Invoice ve QuotationScenario da candidate'e gir
```

---

## Ortak Kurallar (Tüm Fazlar)

```
MIMARI
[ ] Her yeni C# dosyası namespace SetYazilim.Llm.Api.{Katman} kuralına uyar
[ ] DI: AddSingleton (stateless) veya AddScoped (DB bağlantısı olan)
[ ] Yeni endpoint → ilgili Map*.cs dosyasına eklenir, Program.cs'e dokunulmaz
[ ] DB değişikliği → Program.cs startup'taki CREATE TABLE IF NOT EXISTS bloğuna eklenir

GÜVENLİK
[ ] Yeni admin endpoint → [Authorize("AdminOnly")] zorunlu
[ ] Yeni Data olayı → ActivityLogger.LogAsync ile event_log'a yazılır

TEST
[ ] Her faz sonunda scripts/e2e-test.sh çalıştır → PASS/FAIL rapor et
[ ] Yeni senaryo eklendiyse T numarasını sırayla devam ettir (T13, T14, ...)
[ ] FAIL varsa → faza devam etme, önce fix et

DEPLOY
[ ] Her faz bir commit: "feat(rag): faz-X açıklama"
[ ] main'e push → GitHub Actions otomatik deploy eder (~20-25 sn)
[ ] Deploy sonrası /health/deep → tüm probe OK
```

---

## Öncelik Özeti

| Faz | Görev | Efor | Etki | Sıra |
|-----|-------|------|------|------|
| 1 | Structured chunking | 1-2 gün | ⭐⭐⭐⭐⭐ | **HEMEN** |
| 1 | Collection separation | 2 saat | ⭐⭐⭐⭐ | **HEMEN** |
| 2 | Türkçe synonym expansion | 2-4 saat | ⭐⭐⭐ | Faz 1 sonrası |
| 3 | Reranker deploy + entegrasyon | 2-3 gün | ⭐⭐⭐⭐ | Faz 2 sonrası |
| 4 | Relation graph | 1-2 sprint | ⭐⭐⭐ | Uzun vade |

---

## Başlangıç Komutu

Faz 1'e başlamak için ilk adım:

```
MapSql.cs içindeki schema ingest job handler'ı bul →
SqlSchemaChunkBuilder.cs oluştur (TASK-1.1) →
Unit test yaz (Quotation tablosu chunk → VATAmount aynı chunk'ta mı?) →
Geçiyorsa ingest handler'ı güncelle (TASK-1.3) →
Re-ingest tetikle → E2E çalıştır
```
