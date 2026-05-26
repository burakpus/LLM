# API.md

> Endpoint referansı — auth, request/response şemaları.

## İçindekiler
- [Genel](#genel)
- [Auth](#auth)
- [Models](#models)
- [LLM Completions](#llm-completions)
- [Skills](#skills)
- [Documents (RAG)](#documents-rag)
- [Templates](#templates)
- [SQL Connections](#sql-connections)
- [Jobs](#jobs)
- [Event Log](#event-log)
- [Admin Tools](#admin-tools)
- [Benchmark](#benchmark)
- [Deprecated](#deprecated)

## Genel

- **Base URL**: `http://172.16.1.123:5080` (production) / `http://localhost:5080` (dev)
- **Auth**: `Authorization: Bearer <JWT>` veya query string `?token=<JWT>` (SSE için)
- **Content-Type**: `application/json` (POST/PUT)
- **Hata**: `{ error: "..." }` + uygun HTTP status

## Auth

| Method | Path | Auth | Açıklama |
|--------|------|------|----------|
| GET | `/api/auth/domains` | — | LDAP domain listesi |
| POST | `/api/auth/login` | — | `{ username, password, domain }` → `{ token, isAdmin, groups }` |
| GET | `/api/auth/me` | ✅ | Mevcut user bilgisi |
| GET | `/api/auth/groups` | ✅ | Kullanıcının AD grupları + admin config |
| POST | `/api/auth/debug-ldap` | ⭐ | Adım adım LDAP diagnostic (admin) |

## Models

| Method | Path | Auth | Açıklama |
|--------|------|------|----------|
| GET | `/api/models/capabilities` | ✅ | `{ chat, code, reason, embed }` — model feature matrix |

## LLM Completions

| Method | Path | Auth | Açıklama |
|--------|------|------|----------|
| POST | `/api/llm/completions` | ✅ | OpenAI-uyumlu chat completion (stream destekli) |

(Body şeması: messages, model, stream, temperature, maxTokens, tools, toolChoice — Faz 4 detay)

## Skills

| Method | Path | Auth | Açıklama |
|--------|------|------|----------|
| GET | `/api/skills` | ✅ | Listele (order sıralı) |
| GET | `/api/skills/{id}` | ✅ | Tek skill içeriği |
| GET | `/api/skills/{id}/examples` | ✅ | Few-shot örnekleri |
| GET | `/api/admin/skills` | ⭐ | Admin: tüm metadata |
| GET | `/api/admin/skills/{id}` | ⭐ | Ham içerik |
| POST | `/api/admin/skills` | ⭐ | Yükle (.md veya .zip multipart) |
| DELETE | `/api/admin/skills/{id}` | ⭐ | Sil (folder veya flat) |
| PUT | `/api/admin/skills/{id}/order` | ⭐ | Sıra değiştir (DB-stored) |
| POST | `/api/admin/skills/import-anthropic` | ⭐ | GitHub anthropics/skills import |
| POST | `/api/admin/skills/{id}/examples` | ⭐ | Few-shot örnek ekle |
| PUT | `/api/admin/skills/{id}/examples/{exId}` | ⭐ | Düzenle |
| DELETE | `/api/admin/skills/{id}/examples/{exId}` | ⭐ | Sil |

## Documents (RAG)

| Method | Path | Auth | Açıklama |
|--------|------|------|----------|
| GET | `/api/admin/documents` | ⭐ | Sayfalı liste |
| GET | `/api/admin/collections` | ⭐ | Koleksiyon listesi |
| POST | `/api/admin/upload` | ⭐ | Dosya yükleme (PDF/DOCX/XLSX/TXT/MD) |
| DELETE | `/api/admin/documents/{id}` | ⭐ | Sil (RAG'dan da çıkar) |

## Templates

| Method | Path | Auth | Açıklama |
|--------|------|------|----------|
| GET | `/api/templates` | ✅ | Prompt şablonları (slash command picker) |
| POST | `/api/admin/templates` | ⭐ | Yeni şablon |
| PUT | `/api/admin/templates/{id}` | ⭐ | Düzenle |
| DELETE | `/api/admin/templates/{id}` | ⭐ | Sil |

## SQL Connections

| Method | Path | Auth | Açıklama |
|--------|------|------|----------|
| GET | `/api/admin/sql-connections` | ⭐ | Liste |
| GET | `/api/admin/sql-connections/{id}` | ⭐ | Tek bağlantı |
| POST | `/api/admin/sql-connections` | ⭐ | Yeni (şifre DataProtection ile şifrelenir) |
| PUT | `/api/admin/sql-connections/{id}` | ⭐ | Düzenle |
| DELETE | `/api/admin/sql-connections/{id}` | ⭐ | Sil |
| POST | `/api/admin/sql-connections/{id}/test` | ⭐ | Bağlantı testi (10/dk rate limit) |
| POST | `/api/admin/sql-connections/test-credentials` | ⭐ | Ad-hoc test (kaydetmeden) |
| GET | `/api/admin/sql-connections/{id}/objects` | ⭐ | Şema obje listesi (önizleme) |
| POST | `/api/admin/sql-connections/{id}/ingest-schema` | ⭐ | Async schema ingest (job) |
| POST | `/api/admin/sql-connections/{id}/sync-schema` | ⭐ | Async schema sync (job) |
| GET | `/api/admin/sql-connections/{id}/tables` | ⭐ | Tablo + kolon listesi |
| POST | `/api/admin/sql-connections/{id}/sync-data` | ⭐ | Async delta data sync (job) |
| GET | `/api/admin/sql-connections/{id}/table-configs` | ⭐ | Tablo config listesi |
| POST | `/api/admin/sql-connections/{id}/table-configs` | ⭐ | Upsert (PK, updated_col, kolon seçimi, vb.) |
| DELETE | `/api/admin/sql-connections/{id}/table-configs/{tid}` | ⭐ | Sil |
| POST | `/api/admin/sql-connections/{id}/table-configs/bulk-assign-group` | ⭐ | Toplu grup atama |
| GET | `/api/admin/sql-connections/{id}/table-groups` | ⭐ | Grup listesi |
| POST | `/api/admin/sql-connections/{id}/table-groups` | ⭐ | Grup oluştur |
| PUT | `/api/admin/sql-connections/{id}/table-groups/{gid}` | ⭐ | Düzenle |
| DELETE | `/api/admin/sql-connections/{id}/table-groups/{gid}` | ⭐ | Sil |
| GET | `/api/admin/sql-connections/{id}/ingested-stats` | ⭐ | RAG'daki mevcut istatistik |
| GET | `/api/admin/sql-connections/{id}/latest-job` | ⭐ | Son job durumu (sync pattern için) |

## Jobs

| Method | Path | Auth | Açıklama |
|--------|------|------|----------|
| GET | `/api/jobs/{id}` | ✅ | Tek job (sahibi veya admin) |
| GET | `/api/jobs?limit=N&status=...` | ⭐ | Liste (legacy) |
| GET | `/api/admin/jobs` | ⭐ | Sayfalı + filtre (tip/durum) |
| POST | `/api/admin/jobs/{id}/cancel` | ⭐ | İptal (yalnız queued) |
| POST | `/api/admin/jobs/{id}/retry` | ⭐ | Tekrar dene (failed/cancelled) |

## Event Log

| Method | Path | Auth | Açıklama |
|--------|------|------|----------|
| GET | `/api/admin/event-log` | ⭐ | Filtreli liste (kategori, severity, eventType, user, IP, result, free-text, tarih aralığı) |
| GET | `/api/admin/event-log/summary` | ⭐ | Son 24 saat kategori × severity dağılımı |

## Admin Tools

### Dosya Üretimi (generate_file)

| Method | Path | Auth | Açıklama |
|--------|------|------|----------|
| POST | `/api/tools/generate-file` | ✅ | `{ kind, filename, spec }` → docx/xlsx/pdf/pptx üret, `{ downloadUrl }` |
| GET | `/api/tools/generated/{token}/{filename}` | ✅ | Binary download (user-scoped, JWT korumalı) |

### Proxy

| Method | Path | Auth | Açıklama |
|--------|------|------|----------|
| POST | `/api/proxy` | ✅ | Tool HTTP proxy (CORS bypass — agentic tools için) |

## Benchmark

| Method | Path | Auth | Açıklama |
|--------|------|------|----------|
| POST | `/api/admin/benchmark` | ⭐ | N paralel LLM isteği, p50/p95 metrikleri |
| GET | `/api/admin/benchmarks` | ⭐ | Geçmiş ölçümler |

## Deprecated

Aşağıdaki senkron endpoint'ler `b29e6a4` öncesi vardı, sonra **silindi**.
Tüm bu işlemler artık asenkron job kuyruğu üzerinden yürütülür.

| Eski endpoint | Yerine geçen | Açıklama |
|---------------|--------------|----------|
| `POST /api/admin/sql-connections/{id}/ingest-schema-sync` | `POST /…/ingest-schema` | İlk şema çıkarımı — JobService kuyruğa atar, progress `/api/jobs/{id}` ile |
| `POST /api/admin/sql-connections/{id}/ingest-data-sync` | `POST /…/sync-data` | Tablo verisi — artık `sql_table_configs`'a göre delta sync |
| `POST /api/admin/sql-connections/{id}/sync-schema-sync` | `POST /…/sync-schema` | Artımlı şema sync — async |

---

**Legend**: ⭐ = admin only · ✅ = authenticated user · — = public
