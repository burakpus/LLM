# ARCHITECTURE.md

> SET LLM platformunun bileşenleri, veri akışı ve mimari kararları.

## İçindekiler
- [Bileşen Diyagramı](#bileşen-diyagramı)
- [Veri Akışı](#veri-akışı)
- [Bileşenler](#bileşenler)
- [Database Şeması](#database-şeması)
- [Deploy Pipeline](#deploy-pipeline)
- [Frontend Mimarisi](#frontend-mimarisi)
- [Skills Mimarisi](#skills-mimarisi)
- [Audit Logging](#audit-logging)
- [Frontend Auth Flow](#frontend-auth-flow)

## Bileşen Diyagramı

(Faz 4'te detay diyagram eklenecek — mermaid)

## Veri Akışı

### Chat completion
1. Browser → POST `/api/llm/completions` (JWT)
2. .NET → LiteLLM → vLLM → token stream
3. .NET → SSE → browser

### RAG sorgu
1. Browser → POST `/api/llm/completions` (mode=rag)
2. .NET → embed → pgvector hibrit arama → top-N chunk
3. .NET → context'i system prompt'a ekle → LLM call
4. SSE → browser

### SQL Data Sync
1. Admin → "🔄 Sync" → POST `/api/admin/sql-connections/{id}/sync-data`
2. JobService → "sql.sync-data" kuyruğa atar
3. JobWorker → SqlSyncDataJobHandler → tablo başına delta query → row hash → değişen satırlar RAG'a ingest
4. Progress: `/api/jobs/{id}` polling

## Bileşenler

(Faz 4'te detay)

## Database Şeması

Ana tablolar:
- `documents` — RAG vektörlü dökümanlar
- `sql_connections` — SQL veri kaynakları (şifreli)
- `sql_table_configs` — tablo bazlı sync ayarları (PK, updated_col, grup, kolon seçimi)
- `sql_ingested_objects` — şema objeleri (hash-tracked)
- `sql_ingested_rows` — satır bazlı ingest tracking
- `sql_table_groups` — gruplandırma
- `jobs` — background job queue
- `activity_log` — eski denetim kaydı (read-only deprecated, Faz 2 sonrası)
- `event_log` — OWASP uyumlu yeni denetim kaydı
- `prompt_templates` — slash-command şablonları
- `skill_examples` — few-shot örnekler (skill başına)
- `skill_settings` — skill order overrides (UI ile yönetilir, deploy-survived)
- `message_ratings` — 👍/👎 geri bildirim
- `benchmark_results` — benchmark history
- `usage_logs` — token kullanım/maliyet

Tüm tablolar startup'ta `CREATE TABLE IF NOT EXISTS` ile garantili.

(Faz 4'te ER diyagramı + sütun detayları)

## Deploy Pipeline

GitHub Actions → self-hosted runner (DGX) → `git pull` + `npm run build` + `dotnet publish` →
`rsync`-benzeri kopyalama + appsettings merge → `systemctl restart setllm-api`.

Detaylı adımlar: `.github/workflows/deploy.yml` ve `scripts/merge_appsettings.py`.

(Faz 4'te tam adım açıklaması)

## Frontend Mimarisi

(Faz 3'te modülerleştirme sonrası tamamlanacak)

## Skills Mimarisi

İki mod destekleniyor:
- **Flat**: `Skills/{name}.md` — tek dosya skill (legacy + custom)
- **Folder**: `Skills/{name}/SKILL.md` + opsiyonel referans `.md`'leri — Anthropic skills

`SkillRegistry` startup'ta eager-load (HostedService) — 86 dosya ~saniye altında.

(Faz 4'te detay)

## Audit Logging

İki sistem paralel:
- `activity_log` (eski) — kısıtlı şema
- `event_log` (yeni) — OWASP uyumlu (kategori, severity, IP, UA, request_id, JSONB details)

**Migrasyon yolu** (Faz 2):
- Yeni kayıtlar her ikisine — eski UI'lar bozulmaz
- Sonraki sürümde `activity_log` salt-okunur, en sonunda silinir

## Frontend Auth Flow

1. Kullanıcı `/login` → POST `/api/auth/login` → JWT
2. localStorage'a `setllm-token` + `setllm-user`
3. Tüm istekler `Authorization: Bearer ...`
4. **401 yanıt → otomatik logout + `/login`'e yönlendir** — `frontend/src/api/auth-interceptor.ts`
   window.fetch'i wrap'liyor; bir kez `installAuthInterceptor()` `main.tsx`'te çağrılıyor.
   `/api/auth/login` endpoint'i hariç tutulur (orada 401 = yanlış şifre, oturum süresi değil).
   Login sayfasında `?expired=1` query'si "Oturumunuz sona erdi" uyarısı gösterir.
5. JWT exp ~8 saat, refresh yok (yeniden login)
