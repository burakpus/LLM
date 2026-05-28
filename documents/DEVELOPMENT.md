# DEVELOPMENT.md

> Geliştirici rehberi: repo yapısı, local kurulum, build/test, code conventions.

## İçindekiler
- [Repo Yapısı](#repo-yapısı)
- [Local Geliştirme](#local-geliştirme)
- [Build Komutları](#build-komutları)
- [Code Conventions](#code-conventions)
- [Yeni Endpoint Ekleme](#yeni-endpoint-ekleme)
- [Frontend Yapısı](#frontend-yapısı)
- [Yapılandırma Referansı](#yapılandırma-referansı)
- [Deploy Pipeline](#deploy-pipeline)

## Repo Yapısı

```
dgx-spark-llm-stack/
├── .github/workflows/deploy.yml      # CI/CD
├── docker-compose.yml                # vLLM + LiteLLM + Prometheus + Grafana + Loki
├── dotnet/
│   ├── Api/
│   │   ├── Program.cs                # 671 satır orchestrator (DI + middleware + DTO + helpers)
│   │   ├── Endpoints/                # 18 endpoint extension method dosyası + ActivityLogger
│   │   │   ├── ActivityLogger.cs     # Paylaşılan: LogAsync, SerializeJob, MapActionToEvent
│   │   │   ├── MapHealth.cs   MapAuth.cs   MapFiles.cs    MapRatings.cs
│   │   │   ├── MapTemplates.cs  MapSkills.cs  MapChat.cs  MapDocuments.cs
│   │   │   ├── MapEventLog.cs   MapSession.cs MapTools.cs MapProxy.cs
│   │   │   ├── MapUsage.cs      MapLlm.cs     MapErrorLog.cs  MapProjects.cs
│   │   │   └── MapSql.cs   MapJobs.cs
│   │   ├── appsettings.json
│   │   ├── Auth/                     # LDAP + JWT + EventLog
│   │   ├── Jobs/                     # Background job queue
│   │   ├── Sql/                      # SQL provider'lar + delta sync
│   │   ├── Tools/                    # FileGenerator + Benchmark
│   │   └── Skills/                   # ⚠ Repo'da tutulan skill dosyaları
│   └── (shared lib: Context/, Retrieval/, ServiceCollectionExtensions.cs)
├── frontend/
│   └── src/
│       ├── App.tsx
│       ├── store/index.ts            # Zustand + persist
│       ├── api/                      # admin.ts, llm.ts, proxy
│       ├── hooks/useGeneration.ts    # Chat stream + tool dispatch
│       └── components/
│           ├── Admin/
│           │   ├── AdminPage.tsx     # 181 satır orchestrator + AdminGate
│           │   └── tabs/             # 11 tab dosyası + _shared.ts (formatBytes, formatDate)
│           │       ├── UploadTab.tsx  DocumentsTab.tsx  SkillsTab.tsx
│           │       ├── TemplatesTab.tsx  SqlConnectionsTab.tsx  JobsTab.tsx
│           │       ├── UsageTab.tsx  ActivityTab.tsx  SecurityTab.tsx
│           │       └── BenchmarkTab.tsx  SettingsTab.tsx
│           └── Chat/                 # InputBar, MessageList, SettingsPanel, HelpModal, ...
├── monitoring/                       # Prometheus, Grafana, Loki configs
├── scripts/
│   ├── file-gen.py                   # Python: docx/xlsx/pdf/pptx üretici
│   ├── merge_appsettings.py          # Deploy: server config preserve
│   ├── load-test-llm.py              # Standalone benchmark
│   ├── e2e-test.sh                   # End-to-end smoke test (12 senaryo)
│   ├── run_e2e_remote.py             # paramiko ile SSH üzerinden e2e tetikleyici
│   └── clean_excel_skill.py          # One-off (legacy skill temizleme)
└── documents/                        # Bu klasör
```

## Local Geliştirme

### Backend
```bash
cd dotnet/Api
dotnet restore
dotnet run
# → http://localhost:5080
```

PostgreSQL gerekli (`docker run postgres:16` veya production DB'yi tunnel et).

### Frontend
```bash
cd frontend
npm install
npm run dev   # Vite dev server, HMR
# → http://localhost:5173
```

`vite.config.ts` `/api/*` istekleri `localhost:5080`'e proxy'ler.

## Build Komutları

```bash
# Frontend build
cd frontend && npm run build
# → output: dotnet/Api/wwwroot/

# Backend build
cd dotnet/Api && dotnet build       # debug
cd dotnet/Api && dotnet publish -c Release -o /tmp/setllm-publish

# Type check (frontend, no emit)
cd frontend && npx tsc --noEmit
```

## Code Conventions

- **C#**: `dotnet/.editorconfig` kuralları, async hep `Async` suffix, `Result<T>` yerine `Results.Ok()/BadRequest()/NotFound()`
- **TypeScript**: `frontend/tsconfig.json`, strict mode, no implicit any
- **Commit message**: Türkçe + İngilizce karışık OK, prefix: `feat:`, `fix:`, `refactor:`, `docs:`, `perf:`, `chore:`
- **Co-author**: Claude assist eden commit'lerde `Co-Authored-By: Claude ...` satırı

## Yeni Endpoint Ekleme

Backend artık modüler — her endpoint grubu `dotnet/Api/Endpoints/Map*.cs` altında
extension method olarak. Yeni endpoint eklerken:

1. **Uygun Map dosyasını bul** (yoksa yeni `MapXxx.cs` oluştur):
   ```csharp
   namespace SetYazilim.Llm.Api.Endpoints;

   public static class XxxEndpoints
   {
       public static IEndpointRouteBuilder MapXxx(this IEndpointRouteBuilder app)
       {
           app.MapPost("/api/admin/yeni-endpoint", [Authorize("AdminOnly")] async (
               [FromBody] YeniRequest req,
               NpgsqlDataSource ds,
               IEventLog evt,
               CancellationToken ct) =>
           {
               // validate
               // do work
               // OWASP audit (zenginleştirilmiş):
               //   await evt.LogAsync(EventCategory.Data, EventSeverity.Info, "data.yeni", ...);
               // Veya activity_log + event_log dual-write için:
               //   _ = ActivityLogger.LogAsync(ds, username, "yeni.action", target, details);
               return Results.Ok(...);
           });

           return app;
       }
   }
   ```

2. **Program.cs orchestrator'a ekle**: `app.MapXxx();`

3. **DTO** — küçükse Program.cs en altına public record olarak, büyükse
   ayrı bir dosyaya (örn. `Dtos/YeniRequest.cs`).

4. **Frontend tarafında**:
   - `frontend/src/api/admin.ts` (admin endpoint'i ise) veya `llm.ts`:
     `export async function yeniEndpoint(...)`
   - İlgili tab dosyasında çağır: `frontend/src/components/Admin/tabs/XxxTab.tsx`

## RAG — Collection Settings (Öncelik + Etiket)

Her ingest edilmiş RAG collection için DB'de **`collection_settings`** tablosu:

| Kolon | Açıklama |
|-------|----------|
| `collection` (PK) | Collection adı |
| `priority` | `high` (×2.0) · `normal` (×1.0, default) · `low` (×0.5) · `hidden` (retrieval'dan dış) |
| `data_type` | Serbest etiket (örn. `schema`, `data-dictionary`, `document`) |
| `description` | Kısa açıklama |

`HybridSearch` SQL'i `LEFT JOIN collection_settings`:
- `priority='hidden'` candidate set'inden filtrelenir
- Final `hybrid_score` priority multiplier ile çarpılır

**Admin UI**: Documents tab → "Collection Ayarları" açılır panel — her satır inline edit (onBlur auto-save).

**Use case**: Aynı tablo hem ham CREATE TABLE chunks (`sql-schema`), hem düzenli data dictionary chunks (`sql-data-dictionary`) olarak ingest edildiğinde, ikinciye `high` öncelik vererek RAG'ı doğru bilgiye yönlendir.

**Endpoint'ler**:
- `GET /api/admin/collections` — listele (`priority`/`dataType`/`description` left-join)
- `PUT /api/admin/collections/{name}/settings` — upsert `{ priority?, dataType?, description? }`
- `DELETE /api/admin/collections/{name}` — bulk delete (collection'daki tüm kb_documents satırları)

## Frontend Yapısı

**State**: Zustand (`store/index.ts`) — persist middleware ile localStorage'a yazar.
**Önemli store alanları**:
- `conversations` — sohbet listesi
- `currentConvId` — aktif konuşma
- `project` — proje modu state
- `customTools` — kullanıcı-tanımlı tool'lar (gelişmiş)
- `agenticEnabled` (settings) — otonom mod toggle

## Yapılandırma Referansı

`appsettings.json` tüm bölümler:

| Bölüm | Anahtar | Varsayılan | Açıklama |
|-------|---------|-----------|----------|
| `Cors` | `Origins[]` | localhost + 172.16.1.123 | İzinli CORS origin listesi |
| `Jwt` | `Secret` | placeholder | HS256 imzalama anahtarı (≥32 char) |
| | `Issuer` | `set-llm-api` | JWT iss claim |
| | `Audience` | `set-llm-ui` | JWT aud claim |
| | `ExpiryHours` | 8 | Token ömrü |
| `Ldap` | `DomainNames` | `SETYAZILIM` | UI dropdown için domain listesi |
| | `AdminGroups` | `setaiadmin,Set Management` | Admin yetkisi veren AD grup CN'leri |
| | `AdminUsers` | `burakpus` | LDAP'tan bağımsız admin override |
| | `Domains.{NAME}.Host` | — | LDAP host FQDN/IP |
| | `Domains.{NAME}.Port` | 0 (auto 389/636) | LDAP/LDAPS port |
| | `Domains.{NAME}.ServiceAccountDn` | (boş) | İsteğe bağlı service account |
| `LiteLLM` | `BaseUrl` | `http://172.16.1.123:4000` | Gateway endpoint |
| | `ApiKey` | placeholder | LiteLLM master key |
| `VectorStore` | `ConnectionString` | PG | Embed + RAG için PostgreSQL |
| | `EmbedModel` | `nomic-embed-text` | vLLM embed model adı |
| `SessionMemory` | `SessionTtlHours` | 24 | Sohbet hafıza TTL |
| `Agent` | `SkillsDirectory` | `Skills` | Skill dosyaları kök klasörü |
| `Tools` | `PythonExe` | venv python | file-gen subprocess |
| | `FileGenScript` | `scripts/file-gen.py` | Üretici script yolu |
| | `GeneratedRoot` | `generated` | Üretilmiş dosya klasörü |
| | `GeneratedTtlHours` | 24 | Otomatik temizlik TTL |
| `EventLog` | `RetentionDays` | 90 | event_log silinme yaşı |
| `Jobs` | `Workers` | 2 | Paralel job worker sayısı (1-8) |
| `Limits` | `MaxRequestBodyMB` | 100 | Kestrel max body |
| | `MaxLlmContentChars` | 16000 | Tek mesaj max karakter |
| | `SqlTestRateLimitPerMinute` | 10 | SQL conn test rate limit |
| | `LoginRateLimitPerMinute` | 5 | Login brute-force limit |

**Server override**: Production'da `~/setllm-api/appsettings.json` server'a özgü değerler içerir (şifreler, internal IP'ler). Deploy `merge_appsettings.py` ile "server wins" stratejisi kullanır.

## ✅ Tamamlanan Refactor'ler

### Program.cs split — Faz 1-4 TAMAMLANDI

3183 satır tek dosyadan **671 satır**a düştü (-2512, -%79). 18 endpoint extension
dosyası + 1 paylaşılan helper `Endpoints/` altında:

```
dotnet/Api/
├── Program.cs                       # 671 satır (orchestrator + DI + DTO + middleware + static helpers)
└── Endpoints/
    ├── ActivityLogger.cs            #  92 — paylaşılan: LogAsync, SerializeJob, MapActionToEvent
    ├── MapHealth.cs                 # 120 — /health, /health/deep, /metrics
    ├── MapAuth.cs                   # 120 — /api/auth/*
    ├── MapFiles.cs                  #  47 — /api/files/extract
    ├── MapRatings.cs                #  88 — /api/ratings + /api/admin/ratings/stats
    ├── MapTemplates.cs              # 118 — /api/templates + /api/admin/templates/*
    ├── MapSkills.cs                 # 500 — /api/skills + admin variants + import-anthropic + /api/models/capabilities
    ├── MapChat.cs                   #  83 — /api/chat + /api/chat/stream (SSE)
    ├── MapDocuments.cs              # 191 — /api/ingest + /api/admin/upload + documents + collections
    ├── MapEventLog.cs               # 168 — /api/admin/activity-log + event-log[/summary]
    ├── MapSession.cs                #  36 — /api/session/*
    ├── MapTools.cs                  #  95 — /api/tools/*, /api/admin/benchmark[s]
    ├── MapProxy.cs                  #  48 — /api/proxy
    ├── MapUsage.cs                  # 164 — /api/admin/usage/* (LiteLLM spend proxy)
    ├── MapLlm.cs                    # 168 — /api/llm/completions (warming detection + metrics)
    ├── MapErrorLog.cs               #  29 — /api/log/error
    ├── MapProjects.cs               # 102 — /api/projects/*
    ├── MapSql.cs                    # 619 — /api/admin/sql-connections/* (en büyük)
    └── MapJobs.cs                   #  75 — /api/jobs/*, /api/admin/jobs/*
```

Program.cs orchestrator çağrıları (DI + middleware setup sonrası):
```csharp
app.MapHealth();      app.MapAuth();      app.MapFiles();
app.MapSql();         app.MapJobs();      app.MapEventLog();
app.MapRatings();     app.MapTemplates(); app.MapSkills();
app.MapChat();        app.MapDocuments(); app.MapUsage();
app.MapSession();     app.MapTools();     app.MapProxy();
app.MapLlm();         app.MapErrorLog();  app.MapProjects();
```

`LogActivity` / `SerializeJob` / `MapActionToEvent` paylaşılan `ActivityLogger`
sınıfına taşındı. Eski Program.cs'teki local wrapper fonksiyonlar da kaldırıldı —
tüm endpoint dosyaları doğrudan `ActivityLogger.LogAsync` çağırıyor.

Program.cs'te kalan (intentionally — bunlar "orchestrator" rolü):
- `builder` setup, DI service registration, EF/Npgsql/HttpClient yapılandırması
- Authentication/JWT/Authorization policy
- CORS, Kestrel limits, OpenAPI/Swagger
- Middleware pipeline (UseAuthentication, UseAuthorization, EventLog middleware)
- Top-level static helper sınıfları: `LlmMetrics`, `SlidingRateLimit`,
  `LoginRateLimit`, `SqlConnTestRateLimit`, `SkillFrontmatter`,
  `GithubTreeResponse`/`GithubTreeItem`
- Public DTO record'ları (`RatingRequest`, `LoginRequest`, `TemplateUpsertRequest`,
  `SkillExampleRequest`, `BulkAssignGroupRequest`, `AnthropicSkillImportRequest`,
  `SkillOrderRequest`, `ApiChatRequest`/`ApiChatResponse`, `ApiIngestRequest`,
  `ProxyRequest`, `ErrorLogRequest`, vb.)

Build doğrulaması: her ara adımda `dotnet build` ✓ (toplam 4 ara build).
E2E doğrulaması (deploy + `scripts/e2e-test.sh`): **14/14 PASS** x 5 koşum ✅
(AdminPage tab split, Faz 1, Faz 2, Faz 3, Faz 4 sonları).

### ✅ AdminPage.tsx split — TAMAMLANDI

3456 satır tek dosyadan 181 satır orchestrator + 11 izole tab dosyasına ayrıldı:

```
frontend/src/components/Admin/
├── AdminPage.tsx                    # 181 satır (tab orchestrator + AdminGate)
└── tabs/
    ├── _shared.ts                   #  17 — formatBytes, formatDate
    ├── UploadTab.tsx                # 168
    ├── DocumentsTab.tsx             # 207
    ├── SkillsTab.tsx                # 535 — SkillOrderInput + ExampleForm da burada
    ├── TemplatesTab.tsx             # 234 — TemplateRow + extractVars
    ├── SqlConnectionsTab.tsx        # 793 — ingest/sync dialog'ları dahil
    ├── JobsTab.tsx                  # 232 — jobTypeLabel + fmtDuration
    ├── UsageTab.tsx                 # 263
    ├── ActivityTab.tsx              # 129
    ├── SecurityTab.tsx              # 252 — OWASP event log UI
    ├── BenchmarkTab.tsx             # 270 — Metric + DiffChip
    └── SettingsTab.tsx              # 169 — pingProxy lokal
```

`activeJob` state SqlConnectionsTab içinde local kaldı — JobProgressModal her tab'ın
kendisinde import ediliyor. Cross-tab paylaşıma ihtiyaç olmadı.

Build doğrulaması: `npx tsc --noEmit` ✓ ve `npm run build` ✓ (553 modül, 9.97s).

## Deploy Pipeline

Detay: [OPERATIONS.md → Deploy](OPERATIONS.md#deploy)

**Tetikleyici**: `main` branch'e push.
**Süre**: ~20-25 saniye.
**Self-hosted runner**: DGX Spark üzerinde (`runs-on: self-hosted`).

**Önemli**: Skills/ klasörü deploy'da git'ten gelir (Faz önceki commit'lerde repoya commit'lendi). Sunucuya UI'dan eklenen skill'ler **deploy'da kaybolur** — repoya commit gerekli.

### ⚠ Uzun süreli job'lar sırasında deploy yapma

`setllm-api.service` systemd ile `Restart=always` çalışıyor. Deploy = service restart. Eğer bir job (örn. `sql.ingest-schema` ile 11k obje işleyen ~45 dk'lık ingest) çalışırken deploy yaparsanız:

- Service restart olur, .NET process biter
- JobWorker (BackgroundService) yeniden başlatılır
- "running" durumundaki job'ı bulup **döngünün başından** yeniden işlemeye başlar
- Per-object hash tracking (`sql_ingested_objects.ddl_hash`) idempotent olduğu için **veri kaybı yok** — ama daha önce işlenen objeler tekrar ele alınır, ciddi zaman kaybı

**Pratik kural**: Uzun job tetiklemeden önce admin paneli → İşler sekmesi → çalışan iş var mı kontrol et. Veya CI/CD'den deploy etmeden önce:
```bash
curl -s -H "Authorization: Bearer $TOKEN" \
  "http://localhost:5080/api/admin/jobs?status=running" | jq '.total'
```
Sıfırdan farklıysa job bitene kadar bekle.

İleride (TODO): JobWorker'a per-object checkpoint eklenebilir — restart sonrası kaldığı yerden devam etmesi için `sql_ingested_objects` tablosundaki son işlenmiş key'i okur.
