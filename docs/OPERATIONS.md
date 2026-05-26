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

(Faz 4'te detay)

**Kısa öneri**:
```bash
# PostgreSQL günlük dump
pg_dump -h 172.16.0.8 -U setadmin -d mydb -Fc > backup/$(date +%F).dump

# Skills/ snapshot (zaten git'te)
git -C ~/Documents/MultiModel/dgx-spark-llm-stack log -1 --format="%H %s"
```

## Troubleshooting Playbook

### "Invalid username or password" — LDAP problemi
1. `journalctl -u setllm-api | grep -i ldap | tail -20`
2. Sunucudan AD reachable mi: `nc -vz setyazilim.com 389`
3. Admin token al + diagnostic: `POST /api/auth/debug-ldap` (admin only) → adım adım rapor
4. Geçici çözüm: AdminUsers config'inde `burakpus` zaten var (LDAP arızasında bile admin erişimi)

### Schema ingest sıkıştı
1. **🛡 Güvenlik / İşler** sekmesinde job durumu kontrol
2. `running` ama hiç ilerlemiyor → service restart (zombi recovery devreye girer, queued'a düşer)
3. `failed` → result.failures kontrol et — yaygın: `STRING_AGG WITHIN GROUP` (SQL <2017 uyumsuzluğu — fix: commit `f28079c`)

### vLLM modeli yanıt vermiyor
1. `curl -s http://localhost:8000/v1/models | jq` (Gemma) / 8002 (Qwen)
2. `docker ps | grep vllm`
3. GPU bellek: `nvidia-smi`
4. Restart: `docker compose restart vllm-gemma`

### Job worker çalışmıyor
- `journalctl -u setllm-api | grep -i jobworker | tail -10`
- Worker concurrency: `appsettings.json → Jobs:Workers` (1-8)
- Stranded "running" jobs startup'ta otomatik `queued`'a alınır

(Faz 4'te detaylar)

## Günlük Bakım Listesi

(Faz 4'te)

- [ ] Disk doluluk kontrol: `df -h`
- [ ] event_log satır sayısı: rete riasyon işe yarıyor mu?
- [ ] generated/ klasör boyutu cleanup ile makul
- [ ] Job queue stale değil
