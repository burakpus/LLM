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

(Faz 4'te detay)

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

(Faz 1.1 sonrası dolacak)

- `/api/auth/login` per-IP + per-username 5 deneme / 1 dakika
- Limit aşıldığında HTTP 429, event_log'a `security.rate_limit` kaydı
- Doğru login sayacı sıfırlamaz (saldırgan aynı pencerede zorlamayı sürdüremez)

## Rate Limits

| Endpoint | Limit | Pencere | Kaynak |
|----------|-------|---------|--------|
| `/api/auth/login` | 5 deneme | 1 dk | IP + username (Faz 1.1) |
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

(Faz 2.4 sonrası dolacak)

- Şu an: `AllowAnyOrigin/AllowAnyHeader/AllowAnyMethod` (geliştirme rahatlığı)
- Plan: `Cors:AllowedOrigins` config listesi → sadece izinli origin'ler

## Şifreleme

- **SQL şifreleri**: ASP.NET Core DataProtection (`SetYazilim.Llm.Api.SqlConnection.v1`)
- **JWT secret**: `appsettings.json → Jwt:Secret` (production'da env override önerilir)
- **PostgreSQL şifresi**: connection string'de, dosya izinleri 600
- **LDAP service account şifresi**: `appsettings.json` (production'da DataProtection ile şifrelenmesi düşünülebilir)

## Loglanan Olaylar Matrisi

(Faz 2.5 sonrası dolacak — tablo: hangi işlem hangi event_type'a yazıyor)

## Incident Response

(Faz 4'te detay — playbook'lar)

**Şüpheli durumda**:
1. 🛡 Güvenlik sekmesinde son 24 saat filtrele
2. Hedef IP veya username için detay aç → request_id ile correlate
3. Şüpheli pattern: aynı IP'den çok sayıda `auth.login.fail` + sonrası `success`
4. Token revoke: bilinen aktif session yok (JWT stateless) → kullanıcı şifresini değiştir, AD admin'inden kilit
