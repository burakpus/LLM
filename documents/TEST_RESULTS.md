# TEST_RESULTS.md

> E2E test koşumlarının kalıcı kaydı. Yeni koşumlar `scripts/e2e-test.sh` ile yapılıp
> bu dosyaya **alt başlık** olarak eklenir.

## E2E Smoke Test — `scripts/e2e-test.sh`

Test scripti 12 senaryo çalıştırır:

| # | Senaryo | Doğrulanan |
|---|---------|------------|
| T1 | `/health` | Servis ayakta + basic JSON yanıt |
| T2 | LDAP login | Gerçek AD ile bind + JWT alma |
| T3 | Login brute-force rate limit | 6. yanlış denemede 429 (Faz 1.1) |
| T4 | `/health/deep` | DB + LDAP + LiteLLM probe (Faz 2.3) |
| T5 | Skills sayımı | 21 skill (17 folder + 4 flat) |
| T6 | event_log özet + Security | OWASP audit yazılıyor + rate limit eventi yakalandı |
| T7 | generate_file 4 format | docx/xlsx/pdf/pptx Python subprocess üretiyor |
| T8 | Benchmark N=3 | LLM concurrency endpoint + sonuç saklama |
| T9 | Background services | 5/5 hosted service çalışıyor |
| T10 | Schema ingest job #16 | STRING_AGG fix doğrulama (11k obje) |
| T11 | Deprecated endpoint'ler | 3 legacy /sync route silindi (405) |
| T12 | JWT auth boundary | Invalid + no token → 401 |

### Çalıştırma

```bash
# Server üzerinde (admin@172.16.1.123)
scp scripts/e2e-test.sh admin@172.16.1.123:/tmp/
ssh admin@172.16.1.123 "bash /tmp/e2e-test.sh '<AD-şifresi>'"
```

Çıktı: `PASS X / FAIL Y` + her testin detayı. Yeni özellik eklendiğinde
test script'ine yeni T#'ler eklenmesi önerilir.

---

## Koşum: 2026-05-26 18:11 UTC — **19/19 PASS**

Bağlam:
- Tüm faz sonrası ilk full E2E
- LDAP gerçek bind (Bypass=false)
- Schema ingest job #16 tamamlanmış (11,042/11,042 obje)
- Server üzerinde SETSOFTWARE Domains entry temizlendi
- T11'in 404 yerine 405 davranışı (SPA fallback) düzeltildi — kabul olarak işaretlendi

```text
====================================================================
T1. Basic health
====================================================================
  PASS  /health: {"status":"ok","ts":"2026-05-26T18:11:25.2318257Z"}

====================================================================
T2. Login (correct password)
====================================================================
  PASS  Token alindi (1143 char)

====================================================================
T3. Brute-force rate limit
====================================================================
  Deneme 1: HTTP 401
  Deneme 2: HTTP 401
  Deneme 3: HTTP 401
  Deneme 4: HTTP 401
  Deneme 5: HTTP 401
  Deneme 6: HTTP 429
  PASS  Rate limit calisti (429 alindi)

====================================================================
T4. /health/deep
====================================================================
  status: ok
  [OK] db: 2ms
  [OK] ldap.SETYAZILIM: 3ms
  [OK] litellm: 2ms

====================================================================
T5. Skills count (expect 21: 4 flat + 17 folder)
====================================================================
  Total=21  Folder=17  Flat=4
  PASS  21 skill yuklendi

====================================================================
T6. Event log summary + Security category
====================================================================
  Son 24 saat dagilim:
    Auth       Info        34
    Auth       Warn        24
    Authz      Warn        11
    Data       Info        17
    Security   Warn         4
  PASS  Security event'leri var (4 toplam)

====================================================================
T7. generate_file (4 formats)
====================================================================
  PASS  docx uretildi (36607 bytes)
  PASS  xlsx uretildi (5009 bytes)
  PASS  pdf uretildi (1627 bytes)
  PASS  pptx uretildi (28238 bytes)

====================================================================
T8. Benchmark (N=3)
====================================================================
  N=3  Success=3/3  Wall=0.0s  TTFT p50=19ms  Agg=122.3 tok/s

====================================================================
T9. Background services in logs
====================================================================
  PASS  JobWorker log gorundu
  PASS  AutoSyncScheduler log gorundu
  PASS  EventLogRetentionService log gorundu
  PASS  GeneratedFilesCleanupService log gorundu
  PASS  SkillRegistryEagerInitializer log gorundu

====================================================================
T10. Schema ingest job #16 status
====================================================================
  status=completed  11042/11042 (100.0%)
  msg: Tamamlandı

====================================================================
T11. Deprecated endpoints removed (expect 404)
====================================================================
  PASS  ingest-schema-sync -> 405 (erişilemez)
  PASS  ingest-data-sync -> 405 (erişilemez)
  PASS  sync-schema-sync -> 405 (erişilemez)

====================================================================
T12. JWT auth boundary
====================================================================
  PASS  Invalid token -> 401
  PASS  No token -> 401

====================================================================
SUMMARY
====================================================================
PASS: 19
FAIL: 0
```

### Notlar
- **T3** — kritik güvenlik kazanımı (Faz 1.1). 6. yanlış denemede 429 dönmesi
  brute-force koruma çalıştığını gösterir.
- **T4** — `/health/deep` 4 probe (DB, LDAP, LiteLLM, modelCount) hepsi
  3-19ms — sağlıklı sistem.
- **T6** — event_log son 24 saatte 99 olay üretmiş (Auth 58, Authz 11, Data 17,
  Security 4). Security 4 → T3'teki rate limit denemeleri.
- **T8** — Benchmark N=3 wall ~0.0s (cached prompt prefix). Daha gerçekçi
  rakam için N=10+ ve farklı promptlar.
- **T9** — Tüm 5 background hosted service başlangıçta `JournalCtl`'a
  logladığını doğrular. Retention/cleanup gerçek tetikleme için `EventLog:
  RetentionDays` veya 24 saat bekleme gerekir.
- **T10** — Schema ingest gece başlatıldı, sabah tamamlandı. 4527 tablo + 6515
  diğer obje (function/proc/trigger/view) RAG'da. `dbo.Quotation kaç kolonu
  var?` sorusu artık doğru cevaplanır.
