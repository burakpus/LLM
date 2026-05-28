# OPERATIONS.md

> Sistem yöneticisi için: deploy, backup, restore, log erişimi, troubleshooting.

## İçindekiler
- [Sunucu Bilgileri](#sunucu-bilgileri)
- [Deploy](#deploy)
- [Audit Log Yönetimi](#audit-log-yönetimi)
- [Dosya Yaşam Döngüsü](#dosya-yaşam-döngüsü)
- [Health Probes](#health-probes)
- [Backup & Restore](#backup--restore)
- [Troubleshooting Playbook](#troubleshooting-playbook)
- [Günlük Bakım Listesi](#günlük-bakım-listesi)

## Sunucu Bilgileri

- **Host**: `admin@172.16.1.123` (DGX Spark)
- **API workdir**: `/home/admin/setllm-api/`
- **Skills**: `/home/admin/setllm-api/Skills/`
- **Logs (journald)**: `journalctl -u setllm-api`
- **Generated files**: `/home/admin/setllm-api/generated/`
- **PostgreSQL**: `172.16.0.8:5432` db=`mydb` user=`setadmin`
- **LDAP DC**: `setyazilim.com` → `172.16.0.170`

## Deploy

Push to `main` → GitHub Actions `Deploy to DGX` workflow:
1. `git pull origin main` on DGX
2. vLLM model name validation (warns on mismatch)
3. `npm run build` (frontend → `dotnet/Api/wwwroot/`)
4. `dotnet publish -c Release -o /tmp/setllm-publish`
5. Copy `Skills/` + `scripts/file-gen.py`
6. Python venv check (`/home/admin/setllm-tools/venv`)
7. `merge_appsettings.py` (server secrets korunur, yeni keys eklenir)
8. `rm -rf ~/setllm-api && mv /tmp/setllm-publish ~/setllm-api`
9. `systemctl restart setllm-api`
10. `curl /health` → OK doğrulama

Tipik süre: **20-25 saniye**.

**Önemli**: `~/setllm-api/` her deploy'da silinip yeniden oluşturulur. Persistent veri için:
- DB: PostgreSQL (172.16.0.8) — deploy ile etkilenmez
- Skills/: Repodan deploy edilir (UI'dan eklenenler de repoya commit edilmeli)
- generated/: Geçici, 24h TTL (Faz 1.3)
- appsettings.json: server-side override key'ler `merge_appsettings.py` ile korunur

## Opsiyonel — LiteParse OCR (taranmış PDF + resim desteği)

`DocumentParser` text-based PDF'leri PdfPig ile parse eder; taranmış PDF veya resim
yüklemelerde (.jpg/.png/.tiff) [LiteParse](https://github.com/run-llama/liteparse)
CLI subprocess'i devreye girer. Binary yoksa kod sessizce devre dışı kalır
(`LiteParseInvoker.IsAvailableAsync` boot'ta probe yapar, fail → no-op).

### Sunucu kurulumu (~50 MB toplam, bir kerelik)

```bash
# 1) Tesseract OCR engine + dil paketleri
sudo apt-get update
sudo apt-get install -y tesseract-ocr tesseract-ocr-tur tesseract-ocr-eng

# 2) lit CLI binary (Rust, statik binary)
LIT_VERSION=$(curl -s https://api.github.com/repos/run-llama/liteparse/releases/latest \
              | grep tag_name | cut -d'"' -f4)
curl -L "https://github.com/run-llama/liteparse/releases/download/${LIT_VERSION}/lit-x86_64-unknown-linux-gnu.tar.gz" \
     -o /tmp/lit.tar.gz
sudo tar xzf /tmp/lit.tar.gz -C /usr/local/bin/
sudo chmod +x /usr/local/bin/lit
rm /tmp/lit.tar.gz

# 3) Doğrulama
lit --version
tesseract --list-langs | grep -E "(tur|eng)"

# 4) Service'i kullansın (otomatik probe yapar — restart yeterli, kurulum sonrası
#    yeni request'lerde LiteParse aktif olur)
sudo systemctl restart setllm-api
```

### Konfigürasyon (appsettings.json — Server tarafı override)

```json
"LiteParse": {
  "Enabled":      true,
  "BinaryPath":   "lit",
  "LangPack":     "tur+eng",
  "TessdataPath": null,
  "TimeoutSec":   30
}
```

- `Enabled=false` → tüm fallback'i kapat (mevcut PdfPig davranışı)
- `LangPack` → Tesseract dil kodu (örn. `tur+eng+ara` çok dilli)
- `TessdataPath` → tessdata-fast dizini kullanmak istersen (`/usr/share/tesseract-ocr/4.00/tessdata/`); null = default
- `TimeoutSec` → büyük tarama PDF'ler 30sn'yi aşarsa artırın

### Sağlık kontrolü

```bash
# Test: küçük taranmış PDF yükle (admin UI → Upload tab)
# Log'da görmeli:
sudo journalctl -u setllm-api -n 100 | grep -iE "(liteparse|lit parse)"
# Beklenen: "LiteParse available at lit" mesajı + parse OK
```

### Hata durumları

| Belirti | Sebep | Çözüm |
|---|---|---|
| Log: "LiteParse not available" | Binary `PATH`'te değil | `which lit` ile kontrol, `BinaryPath` mutlak yola çevir |
| OCR sonuç boş | tessdata-tur eksik | `tesseract --list-langs` kontrol |
| Timeout sık | Büyük taranmış PDF | `TimeoutSec` artır (60-120) |

## Audit Log Yönetimi

`event_log` tablosu OWASP-uyumlu denetim kayıtlarını saklar.

**Retention**: 90 gün (varsayılan). `EventLogRetentionService : BackgroundService`
servisi her 24 saatte bir 90 günden eski satırları siler. İlk tarama
servis açıldıktan 2 dakika sonra çalışır.

**Yapılandırma**: `appsettings.json` → `EventLog:RetentionDays` (7-3650 arası,
varsayılan 90).

**Manuel sorgu örnekleri**:
```sql
-- Son 24 saat kategori dağılımı
SELECT category, COUNT(*) FROM event_log
WHERE ts > NOW() - INTERVAL '1 day' GROUP BY 1;

-- Bir kullanıcının son giriş denemeleri
SELECT ts, event_type, result, source_ip, reason
FROM event_log
WHERE username = 'burakpus' AND category = 'Auth'
ORDER BY ts DESC LIMIT 50;

-- Brute-force şüphesi (1 saat içinde 3+ fail)
SELECT source_ip, COUNT(*) AS fails
FROM event_log
WHERE ts > NOW() - INTERVAL '1 hour'
  AND event_type IN ('auth.login.fail', 'security.rate_limit')
GROUP BY 1 HAVING COUNT(*) >= 3
ORDER BY fails DESC;
```

**UI**: Admin → 🛡 **Güvenlik** sekmesi — filtre/arama/expand-detail.

## Dosya Yaşam Döngüsü

`generated/{user}/{uuid}/{filename}` altında üretilen Word/Excel/PDF/PowerPoint
dosyaları **24 saat TTL** ile otomatik temizlenir.

**Servis**: `GeneratedFilesCleanupService : BackgroundService`
- Sunucu açılışından 3 dakika sonra başlar
- Saatte bir tarar
- TTL'i aşan dosyaları siler
- Boş kalan token klasörlerini siler

**Yapılandırma**: `appsettings.json` → `Tools:GeneratedTtlHours` (1-720, varsayılan 24).

**Manuel temizlik** (zorunlu kalmazsa kullanma):
```bash
# 1 günden eski tüm üretilmiş dosyaları sil
find /home/admin/setllm-api/generated -type f -mtime +1 -delete

# Tüm üretilmiş içeriği temizle (riskli!)
rm -rf /home/admin/setllm-api/generated/*
```

**Kullanıcı etkisi**: Kullanıcı tool sonucu chip'inden indirme bağlantısını
24 saat içinde tıklamalı. Geçince link 404 döner; aynı içeriği isterse model
`generate_file` aracını tekrar çağırır (saniyeler içinde yeni dosya).

## Health Probes

### `GET /health` (basit)
Auth gerekli değil. Hızlı liveness probe — yalnızca süreç ayakta mı.
```json
{ "status": "ok", "ts": "2026-05-26T..." }
```

### `GET /health/deep` (derin)
Auth gerekli değil ama dış erişime karşı internal kullanılmalı.
Tüm subsystem'lar OK → **200**; biri patladı → **503** + detay.
```json
{
  "status": "ok | degraded",
  "ts": "...",
  "probes": {
    "db":            { "ok": true,  "ms": 8 },
    "ldap.SETYAZILIM": { "ok": true, "ms": 32, "host": "setyazilim.com", "port": 389 },
    "litellm":       { "ok": true,  "ms": 47, "modelCount": 4 }
  }
}
```

**Kullanım**:
- Bakım kontrolü: `curl -s http://localhost:5080/health/deep | jq`
- Alerting: 503 dönüyorsa hangi probe'un patladığına bak
- LDAP probe: TCP connect denemesi (bind yapmıyor — şifre gerek yok)
- LiteLLM probe: `/v1/models` 200 + JSON içinde `data` array

## Backup & Restore

### Neyi yedeklemek lazım?

| Veri | Konum | Backup gerekli mi? |
|------|-------|---------------------|
| RAG dökümanları + embeddings | PostgreSQL `documents` | ✅ kritik |
| Audit / event log | PostgreSQL `event_log`, `activity_log` | ✅ kritik (uyum/forensik) |
| SQL bağlantı tanımları | PostgreSQL `sql_connections` (encrypted_password) | ✅ kritik (şifre encrypt'li) |
| Kullanıcı ayarları | localStorage (sohbet geçmişi) | ⚠️ kullanıcı tarafı, sunucu değil |
| Skill dosyaları | Repo (`dotnet/Api/Skills/`) | git remote (zaten korunuyor) |
| `appsettings.json` (server values) | `~/setllm-api/appsettings.json` | ✅ önemli (LDAP/JWT secret'leri) |
| Üretilmiş dosyalar | `~/setllm-api/generated/` | ❌ 24h TTL, geçici |
| Logs (journald) | systemd journal | ⚠️ rotation otomatik |

### Günlük dump

```bash
# Eklemek istediğin servere bir cron (örnek 03:00):
0 3 * * * /home/admin/scripts/backup-pg.sh

# scripts/backup-pg.sh
#!/bin/bash
set -e
DEST=/srv/backup/setllm
mkdir -p $DEST
DATE=$(date +%F)

# PostgreSQL dump (custom format — pg_restore --jobs ile paralel)
PGPASSWORD=Atlas_71 pg_dump -h 172.16.0.8 -U setadmin -d mydb -Fc \
    > $DEST/db-$DATE.dump

# appsettings.json snapshot
cp /home/admin/setllm-api/appsettings.json $DEST/appsettings-$DATE.json

# 30 günden eski yedekleri sil
find $DEST -name "db-*.dump" -mtime +30 -delete
find $DEST -name "appsettings-*.json" -mtime +30 -delete
```

### Restore

```bash
# PostgreSQL dump'tan geri yükleme
PGPASSWORD=Atlas_71 pg_restore -h 172.16.0.8 -U setadmin -d mydb \
    --clean --if-exists --jobs=4 db-2026-05-26.dump

# appsettings restore
sudo systemctl stop setllm-api
cp appsettings-2026-05-26.json /home/admin/setllm-api/appsettings.json
sudo systemctl start setllm-api
```

### Disaster recovery (DGX kaybı)
1. Yeni sunucuya `docker-compose up -d` (vLLM/LiteLLM/Prometheus/Grafana/Loki)
2. PostgreSQL'i restore (172.16.0.8 zaten ayrı sunucu, kayıp değilse paslıdır)
3. Repo clone + ilk deploy (`main` branch'i push → GitHub Actions)
4. `appsettings.json`'ı yedekten kopyala
5. LDAP bind testi (`/api/auth/debug-ldap`)
6. Health check (`/health/deep`)

## Troubleshooting Playbook

### "Invalid username or password" — LDAP problemi
1. `journalctl -u setllm-api | grep -i ldap | tail -20`
2. AD reachable mi: `nc -vz setyazilim.com 389`
3. Admin token al + diagnostic: `POST /api/auth/debug-ldap` (admin only) → adım adım rapor (config → connect → bind → search → group fetch)
4. Geçici çözüm: AdminUsers config'inde `burakpus` zaten var (LDAP arızasında bile admin erişimi)

### "Çok fazla deneme" (429) — login engellendi
- Sebep: brute-force koruması tetiklendi (5 deneme/dk per (IP, username))
- 1 dakika bekle → otomatik düşer
- Aynı kullanıcının çoklu sekmeden eş zamanlı denemesi de sayılır
- Admin → 🛡 Güvenlik sekmesinde `security.rate_limit` event'i görünür

### Schema ingest sıkıştı
1. **Admin → İşler** sekmesinde job durumu kontrol
2. `running` ama ilerlemiyor → service restart (zombi recovery: running → queued)
3. `failed` → JobProgressModal'da hata mesajı veya `jobs.result` JSON içinde `failures[]` dizisi
4. Yaygın hatalar:
   - `STRING_AGG WITHIN GROUP` — eski SQL Server (<2017); fix: commit `f28079c`
   - `Login failed for user` — SQL Auth şifresi yanlış; conn'ı sil yeniden kur
   - `pre-login handshake` — eski TLS; OpenSSL config zaten ayarlanmış
   - `timeout` — query_timeout_sec artır (UI'da 5..3600 sn)

### vLLM modeli yanıt vermiyor
1. `curl -s http://localhost:8000/v1/models | jq` (Gemma) / 8002 (Qwen) / 8003 (GPT-OSS)
2. `docker ps | grep vllm`
3. GPU bellek: `nvidia-smi` — fragmentation veya başka süreç GPU kullanıyor olabilir
4. Restart: `docker compose restart vllm-gemma`
5. Model warm-up genelde 5-10 dakika — `journalctl -u setllm-api | grep "warming"`

### Job worker çalışmıyor
- `journalctl -u setllm-api | grep -i jobworker | tail -10`
- Worker concurrency: `appsettings.json → Jobs:Workers` (1-8)
- Stranded "running" jobs startup'ta otomatik `queued`'a alınır
- `SKIP LOCKED` ile aynı job iki worker tarafından alınmaz

### Disk dolu
- `df -h /home/admin` — kontrol
- En yaygın suçlular:
  - `~/setllm-api/generated/` — 24h TTL ama çok dosya üretildiyse büyür → `find ... -mtime +0 -delete`
  - `journalctl` — `sudo journalctl --vacuum-time=7d`
  - PostgreSQL — `event_log` çok büyüdüyse retention ayarını düşür (90 → 30)
  - `~/Documents/MultiModel/dgx-spark-llm-stack/.git` — büyük binary commit'ler

### Auto-sync tetiklenmiyor
- Bağlantıda `auto_sync_interval_min > 0` mu?
- AutoSyncScheduler her dakika tarama yapar (logda `AutoSync enqueued` görmeli)
- Aktif `sql.sync-data` job zaten varsa atlanır
- `journalctl -u setllm-api | grep -i autosync | tail -20`

### 401 her isteğe dönüyor
- JWT süresi dolmuş (8h) — yeniden login
- Frontend auth interceptor otomatik `/login?expired=1`'e yönlendirmeli (Faz 1.4 sonrası)
- Token claim'leri değişmişse (admin/groups) → yeniden login zorunlu

## Günlük Bakım Listesi

### Sabah kontrolleri (~5 dakika)
- [ ] **Health**: `curl http://172.16.1.123:5080/health/deep` — tüm probe OK?
- [ ] **Güvenlik son 24h**: 🛡 Güvenlik sekmesi özet — anormal `Warn`/`Error` var mı?
- [ ] **İşler son 24h**: Admin → İşler — başarısız job sayısı?
- [ ] **Disk**: `df -h /home/admin` — %80 altında mı?
- [ ] **GPU**: `nvidia-smi` — modellerin VRAM kullanımı normal mi?

### Haftalık (~15 dk)
- [ ] **Backup**: cron çalışıyor mu, dump dosyaları taze mi?
- [ ] **Audit retention**: `SELECT MIN(ts), COUNT(*) FROM event_log` — 90 günden eski yok mu?
- [ ] **Skill'ler**: UI'dan eklenen yeni skill repoya commit edilmiş mi? (`git -C ~/.../Skills status`)
- [ ] **Job queue health**: `SELECT status, COUNT(*) FROM jobs GROUP BY 1` — stuck 'running' yok mu?
- [ ] **LDAP**: 1-2 test login (`debug-ldap`) — yanıt süreleri normal mi?

### Aylık
- [ ] **Deploy log gözden geçir**: `gh run list --limit 30` — başarısız var mı, neden?
- [ ] **vLLM model güncellemesi**: yeni sürümler/quantization?
- [ ] **Şifre rotasyonu**: `appsettings.json → Jwt:Secret` (zorunlu değil ama best practice)
- [ ] **Benchmark karşılaştırma**: Admin → 🧪 Benchmark — performans regresyonu var mı?

### Hızlı sorgular

```sql
-- Son 24h job sayıları
SELECT job_type, status, COUNT(*) FROM jobs
WHERE created_at > NOW() - INTERVAL '1 day' GROUP BY 1, 2;

-- En sık başarısız job tipi
SELECT job_type, error, COUNT(*) FROM jobs
WHERE status = 'failed' AND created_at > NOW() - INTERVAL '7 days'
GROUP BY 1, 2 ORDER BY 3 DESC LIMIT 10;

-- Disk şişirici sorgu — generated klasör boyutu
du -sh /home/admin/setllm-api/generated/*/ | sort -h | tail -10

-- Active kullanıcı (son 24h kim login oldu)
SELECT username, COUNT(*) AS logins, MAX(ts) AS last_seen
FROM event_log
WHERE event_type = 'auth.login.success'
  AND ts > NOW() - INTERVAL '1 day'
GROUP BY 1 ORDER BY 2 DESC;
```
