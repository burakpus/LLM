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
│   │   ├── Program.cs                # Tek dosya minimal API (Faz 3'te split)
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
│           ├── Admin/AdminPage.tsx   # 11 sekme (Faz 3'te split)
│           └── Chat/                 # InputBar, MessageList, SettingsPanel, HelpModal, ...
├── monitoring/                       # Prometheus, Grafana, Loki configs
├── scripts/
│   ├── file-gen.py                   # Python: docx/xlsx/pdf/pptx üretici
│   ├── merge_appsettings.py          # Deploy: server config preserve
│   ├── load-test-llm.py              # Standalone benchmark
│   └── clean_excel_skill.py          # One-off (legacy skill temizleme)
└── docs/                             # Bu klasör
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

Şu an Program.cs tek dosya — Faz 3 sonrası modüler.

**Mevcut yöntem** (Program.cs içinde):
```csharp
app.MapPost("/api/admin/yeni-endpoint", [Authorize("AdminOnly")] async (
    [FromBody] YeniRequest req,
    NpgsqlDataSource ds,
    IEventLog evt,
    CancellationToken ct) =>
{
    // validate
    // do work
    // log: await evt.LogAsync(EventCategory.Data, EventSeverity.Info, "data.yeni", ...);
    return Results.Ok(...);
});

// DTO en altta:
public sealed record YeniRequest(string Field1, int Field2);
```

**Frontend tarafında**:
- `frontend/src/api/admin.ts`: `export async function yeniEndpoint(...)`
- AdminPage tab'inde çağır

## Frontend Yapısı

(Faz 3.2 sonrası tab dosyaları ayrı, şimdilik tek dosya)

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

### Program.cs split — Faz 1 + Faz 2 TAMAMLANDI

3183 satır tek dosyadan 2026 satıra düştü. 7 endpoint extension dosyası + 1
paylaşılan helper Endpoints/ altında:

```
dotnet/Api/
├── Program.cs                       # 2026 satır (orchestrator + DI + DTO + middleware)
└── Endpoints/
    ├── ActivityLogger.cs            #  92 — paylaşılan: LogAsync, SerializeJob, MapActionToEvent
    ├── MapHealth.cs                 # 120 — /health, /health/deep, /metrics
    ├── MapAuth.cs                   # 120 — /api/auth/*
    ├── MapTools.cs                  #  95 — /api/tools/*, /api/admin/benchmark[s]
    ├── MapEventLog.cs               # 168 — /api/admin/activity-log + event-log[/summary]
    ├── MapSql.cs                    # 619 — /api/admin/sql-connections/* (en büyük)
    └── MapJobs.cs                   #  75 — /api/jobs/*, /api/admin/jobs/*
```

Program.cs orchestrator çağrıları:
```csharp
app.MapHealth();
app.MapAuth();
app.MapTools();
app.MapEventLog();
app.MapSql();
app.MapJobs();
```

`LogActivity` (top-level local) ve `SerializeJob` (top-level static) Program.cs'te
forward wrapper olarak kaldı (16 call site değişmedi) — implementasyon
`ActivityLogger`'a taşındı.

Build doğrulaması: her ara adımda `dotnet build` ✓.
E2E doğrulaması (deploy + scripts/e2e-test.sh): **14/14 PASS** x 3 koşum ✅
(Faz 1, Faz 2 sonu, AdminPage tab split sonu).

Program.cs'te kalan ~2000 satır: DI/middleware config, DTO record'ları,
yardımcı static class'lar (RateLimit, LlmMetrics, SkillFrontmatter) ve henüz
çıkarılmamış endpoint grupları: ratings, templates, skills, chat (sse),
ingest/upload, usage, session, projects, files/extract, proxy, error log,
llm/completions. Bunlar daha küçük gruplar — ileride opsiyonel ek bölme yapılabilir.

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
