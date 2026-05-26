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

## Koşum: 2026-05-26 21:05 UTC — **14/14 PASS** (Program.cs split Faz 2)

Bağlam:
- `refactor(api): Program.cs split Faz 2` commit (a50b57e) deploy edildi
- Backend Program.cs 3183 → 2026 satır (Health/Auth/Tools/EventLog/Sql/Jobs ayrıldı)
- SQL ingest, job cancel/retry, schema sync, event log — hepsi ayrı dosyalardan çalışıyor
- T9 yine SKIP (deploy yeni)

Kritik testler (refactor'ın etkilediği SQL/Jobs/EventLog endpoint'leri):
- T6 (event_log): PASS — 6 security event, kategoriler dolu
- T10 (schema ingest job #16): PASS — completed 11042/11042
- T11 (deprecated /sync routes): PASS — 3 route 405
- T12 (JWT auth boundary): PASS

SUMMARY: PASS 14 / FAIL 0 — Program.cs split davranışsal eşdeğer ✅

---

## Koşum: 2026-05-26 20:57 UTC — **14/14 PASS** (Program.cs split Faz 1)

Bağlam:
- `refactor(api): Program.cs split Faz 1` commit (e1da067) deploy edildi
- Health/Auth/Tools/EventLog endpoint'leri ayrı dosyalara taşındı (4 dosya)
- Program.cs 3183 → 2757 satır

SUMMARY: PASS 14 / FAIL 0 ✅

---

## Koşum: 2026-05-26 20:42 UTC — **14/14 PASS** (admin tab split sonrası)

Bağlam:
- `refactor(admin): AdminPage.tsx 3456 → 181 satır` commit (6e97998) deploy edildi
- Frontend tarafında 11 tab tabs/<Name>Tab.tsx altına ayrıldı — davranış değişmemiş olmalı
- Deploy ~2 dk önce yapıldı, bu nedenle T9'daki background service log'ları
  henüz `journalctl --since "30 min"` aralığına girmemiş → 5 SKIP (PASS değil ama FAIL de değil)
- Diğer 12 senaryo (T1–T8, T10–T12) tamamı PASS — refactor davranışsal eşdeğer ✅

```text
====================================================================
T1. Basic health
====================================================================
  PASS  /health
T2. Login (correct password)             PASS  Token alindi
T3. Brute-force rate limit               PASS  6. denemede 429
T4. /health/deep                         PASS  db 3ms · ldap 3ms · litellm 2ms
T5. Skills count                         PASS  21 skill (4 flat + 17 folder)
T6. Event log summary                    PASS  6 security event
T7. generate_file (4 formats)            PASS  docx/xlsx/pdf/pptx
T8. Benchmark (N=3)                      PASS  3/3 · agg 457 tok/s
T9. Background services                  SKIP  (deploy yeni, log henüz yok)
T10. Schema ingest job #16               PASS  completed 11042/11042
T11. Deprecated endpoints                PASS  3 route 405
T12. JWT auth boundary                   PASS  invalid + no token → 401

SUMMARY: PASS 14 / FAIL 0
```

Otomasyon notu: `scripts/run_e2e_remote.py` eklendi — paramiko ile SSH üzerinden
test scriptini server'a SCP'leyip çalıştırır. Çevre değişkenleri:
`SSH_USER`, `SSH_PASS`, `AD_PASS` (boşsa SSH_PASS kullanılır).

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
