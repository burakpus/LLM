# SECURITY.md

> Güvenlik mimarisi: kimlik doğrulama, yetkilendirme, rate limits, OWASP event log.

## İçindekiler
- [Threat Model](#threat-model)
- [Kimlik Doğrulama](#kimlik-doğrulama)
- [Yetkilendirme](#yetkilendirme)
- [Brute-force Koruması](#brute-force-koruması)
- [Rate Limits](#rate-limits)
- [OWASP Event Log](#owasp-event-log)
- [CORS Politikası](#cors-politikası)
- [Şifreleme](#şifreleme)
- [Loglanan Olaylar Matrisi](#loglanan-olaylar-matrisi)
- [Incident Response](#incident-response)

## Threat Model

**Aktörler**:
- Düzenli kullanıcı (LDAP doğrulamalı çalışan) — sohbet + RAG + dosya üretimi
- Admin (`AdminUsers` config veya `setaiadmin`/`Set Management` AD grup üyesi) — yönetim panelinin tamamı
- Anonim (auth'suz) — sadece `/health`, `/login`, `/api/models/capabilities` görür

**Riskler**:
- Brute-force login (kısıtlı, Faz 1.1 sonrası)
- Token sızıntısı (JWT, 8 saat exp, refresh yok)
- SQL injection (parametrize sorgular, path-traversal guard'lar)
- Yetkisiz admin erişimi (`[Authorize("AdminOnly")]` middleware)
- Audit kayıt manipülasyonu (event_log immutable — yalnızca INSERT, DELETE retention için BackgroundService)

## Kimlik Doğrulama

**LDAP (Active Directory)**:
- DC: `setyazilim.com` → `172.16.0.170`
- Port: 389 (LDAP) veya 636 (LDAPS)
- 3 bind strategy fallback:
  1. Service account search → bind as found DN (önerilen, production)
  2. `NETBIOS\user` (örn: `setyazilim\burakpus`)
  3. `user@upn` (örn: `burakpus@setyazilim.com`)

**JWT**:
- HS256, 8 saat exp
- Claims: `unique_name`, `domain`, `isAdmin`, `groups` (semicolon-separated CN list)
- localStorage'da saklanır
- Refresh yok — süre dolunca yeniden login

**AdminUsers fallback**:
- `appsettings.json → Ldap:AdminUsers` listesindeki kullanıcılar grup üyeliğinden bağımsız admin
- Tipik kullanım: LDAP grup adının değiştiği durumda erişim korunur
- Şu an: `burakpus`

## Yetkilendirme

- Tüm `/api/admin/*` endpoint'leri `[Authorize("AdminOnly")]`
- Normal kullanıcı endpoint'leri `[Authorize]`
- Anonim: yalnızca `/health`, `/api/models/capabilities`, `/api/auth/login`, `/api/auth/domains`

## Brute-force Koruması

`/api/auth/login` endpoint'i `LoginRateLimit` static class'ı tarafından korunur.

| Özellik | Değer |
|---------|-------|
| Limit | **5 deneme** |
| Pencere | **1 dakika** (rolling) |
| Kapsam | **(IP, username) çifti başına** — aynı IP'den farklı kullanıcı denemeleri ayrı sayılır |
| Aşıldığında | HTTP **429** + `Çok fazla deneme. {retryAfter} saniye sonra tekrar deneyin.` |
| Sayaç sıfırlanır mı? | **Hayır**, doğru login bile sayacı sıfırlamaz — saldırgan başarılı deneme arasından zorlamayı sürdüremez |
| Audit | `event_log` → `category=Security`, `event_type=security.rate_limit`, `details={ip,username,retryAfter}` |

**IP tespiti**: Önce `X-Forwarded-For` header'ı (nginx arkasındayız), yoksa `RemoteIpAddress`.

**Limit özelleştirme**: Şu an kodda sabit. İleride `appsettings.json → Limits:Login` ile config'e taşınacak (Faz 3.3).

## Rate Limits

| Endpoint | Limit | Pencere | Kaynak |
|----------|-------|---------|--------|
| `/api/auth/login` | 5 deneme | 1 dk | (IP, username) çifti |
| `/api/admin/sql-connections/{id}/test` | 10 | 1 dk | username |
| `/api/admin/sql-connections/test-credentials` | 10 | 1 dk | username |

## OWASP Event Log

OWASP Logging Cheat Sheet ve ASVS L2 uyumlu.

**8 kategori**:
- `Auth` — login success/fail/bad_request
- `Authz` — 401/403, admin denied
- `Session` — (gelecek)
- `Input` — (gelecek: injection denemeleri)
- `Config` — ayar değişiklikleri
- `Data` — CRUD: skill/document/SQL connection
- `Security` — rate limit aşımı, brute-force
- `System` — startup/shutdown, job worker

**5 severity**: `Debug` · `Info` · `Warn` · `Error` · `Critical`

**Her olay** (event_log şeması):
- `ts` (UTC)
- `category`, `severity`, `event_type` (örn: `auth.login.fail`)
- `username`, `source_ip`, `user_agent`, `request_id`, `session_id`
- `endpoint`, `action`, `resource`
- `result` (`Success`/`Failure`/`Denied`/`Error`)
- `reason` (kısa metin)
- `details` (JSONB ek bilgi)

**Erişim**: Admin → 🛡 Güvenlik sekmesi. Filtre: kategori × severity × tip × kullanıcı × IP × sonuç + serbest arama.

## CORS Politikası

`appsettings.json → Cors:Origins` listesindeki origin'ler kabul edilir,
geri kalanı browser tarafından bloklanır.

```json
"Cors": {
  "Origins": [
    "http://172.16.1.123:5080",
    "http://localhost:5173",
    "http://localhost:5080"
  ]
}
```

- `AllowAnyHeader()` + `AllowAnyMethod()` + `AllowCredentials()` — sadece origin filtresi
- Yeni client domain ekleneceğinde listeye yaz + service restart
- Production'da `localhost*` girdileri kaldırılabilir (sadece UI host + LAN IP)

## Şifreleme

- **SQL şifreleri**: ASP.NET Core DataProtection (`SetYazilim.Llm.Api.SqlConnection.v1`)
- **JWT secret**: `appsettings.json → Jwt:Secret` (production'da env override önerilir)
- **PostgreSQL şifresi**: connection string'de, dosya izinleri 600
- **LDAP service account şifresi**: `appsettings.json` (production'da DataProtection ile şifrelenmesi düşünülebilir)

## Loglanan Olaylar Matrisi

| event_type | category | severity | Kaynak | Detay |
|------------|----------|----------|--------|-------|
| `auth.login.success` | Auth | Info | Login endpoint | username, isAdmin, groupCount |
| `auth.login.fail` | Auth | Warn | Login endpoint | reason=ldap_reject |
| `auth.login.bad_request` | Auth | Warn | Login endpoint | reason=missing credentials |
| `authz.unauthenticated` | Authz | Warn | 401 middleware | endpoint, action |
| `authz.forbidden` | Authz | Warn | 403 middleware | endpoint, action |
| `security.rate_limit` | Security | Warn | Login/SQL test | retry-after, IP, scope |
| `data.file.generate.{kind}` | Data | Info/Warn | generate-file tool | filename, sizeBytes |
| `job.cancel` | System | Info | LogActivity | jobId |
| `job.retry` | System | Info | LogActivity | oldId, newId |
| `sql.connection.create` | Config | Info | LogActivity (dual-write) | db_type, host |
| `sql.connection.update` | Config | Info | LogActivity (dual-write) | name |
| `sql.connection.delete` | Config | Info | LogActivity (dual-write) | id |
| `sql.ingest.schema` | Data | Info | LogActivity (dual-write) | collection, conn-{id} |
| `sql.sync.schema` | Data | Info | LogActivity (dual-write) | counts |
| `sql.ingest.data` | Data | Info | LogActivity (dual-write) | conn-{id} |
| `skill.upload` | Data | Info | LogActivity (dual-write) | skillId, size |
| `skill.delete` | Data | Info | LogActivity (dual-write) | skillId |
| `skill.order.update` | Data | Info | LogActivity (dual-write) | order |
| `skill.import` | Data | Info | LogActivity (dual-write) | Anthropic import |
| `template.create/update/delete` | Data | Info | LogActivity (dual-write) | templateId |
| `document.upload/delete` | Data | Info | LogActivity (dual-write) | collection |

**Doğrudan IEventLog kullanan kayıtlar** (HTTP context dahil: IP, UA, request_id):
- `auth.login.*` (login endpoint'i içinde — birden fazla event yazımı yok, tek)
- `authz.*` (401/403 middleware'inde)
- `security.rate_limit` (rate-limited endpoint'ler)
- `data.file.generate.*` (generate-file)

**Dual-write kayıtlar** (LogActivity helper'ı ile — HTTP context yok, sadece basic 4 alan):
- `sql.*`, `skill.*`, `template.*`, `document.*`, `job.*` — yukarıdaki tablo

## Incident Response

### Şüpheli aktivite tespiti
1. **Admin → 🛡 Güvenlik** son 24 saat → kategori/severity dağılımı
2. **Yüksek `Warn`/`Error`** sayısı normal değil → kategoriye göre filtrele
3. **IP filtresi** → bir kaynaktan çok sayıda 401/429
4. **Username filtresi** → bir hesaba yoğun saldırı

### Brute-force saldırısı
**Belirtiler**: aynı IP veya farklı IP'lerden bir username'e dakikada çok sayıda `auth.login.fail` + `security.rate_limit`

**Aksiyon**:
1. Saldırı süresince kullanıcının hesabı zaten korunuyor (rate limit + LDAP reject — gerçek saldırgan içeri giremez)
2. Hesap sahibini bilgilendir, şifre değişikliği öner
3. Firewall'da saldırgan IP'yi blokla (gerekirse — Traefik veya host-level)
4. Saldırı pattern'i sürerse `LoginRateLimitPerMinute` düşür (3 vb.)

### Yetkisiz erişim girişimi
**Belirti**: `authz.forbidden` çok sayıda — normal kullanıcı admin endpoint'lerine erişmeye çalışıyor

**Aksiyon**:
1. Username + endpoint detayına bak — UI bug mu yoksa malicious mi?
2. UI bug'sa düzelt (yanlış sekmede admin endpoint çağrısı vs.)
3. Malicious ise hesabı LDAP'tan kilitle (AD admin)

### Token sızıntısı şüphesi
JWT stateless — revoke imkanı yok. Çözüm:
1. **Şifre değiştir** (LDAP) — yeni login yapana kadar eski token kullanılabilir
2. `Jwt:Secret` rotasyonu → tüm token'lar invalid olur (acil durumda)
3. JWT'leri saldırgan kullanmaya devam ediyorsa: 8 saat dolması bekle veya secret rotate

### Audit log tampering şüphesi
- `event_log` append-only (DELETE sadece retention service'ten)
- Hash chain veya digital signature yok (gelecek — şu an yeterli)
- Postgres user (`setadmin`) read+write — DBA seviye erişim varsa manipule edilebilir
- Önlem: PostgreSQL audit log (pgAudit extension) açılabilir

### Veri kaybı
1. PostgreSQL günlük dump var mı? (OPERATIONS.md Backup bölümü)
2. PITR (point-in-time recovery) yapılandırılmış mı? — şu an basic dump, PITR yok
3. Skills/ git'te — `git log` ile bulunabilir
4. Generated files — geçici, 24h TTL — kaybı normal

### İletişim
- Kritik incident → IT yöneticisi + iş sahibi (admin'i yetkilendiren grup)
- Tüm aksiyonları event_log'a yansıt (Configuration changes manuel `LogActivity` çağrısı ile)
