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

## Planlı Refactor'ler (Şu Anda Beklemede)

### Program.cs split (büyük dosya)
**Hedef**: 3,000+ satır tek dosyadan endpoint mapping extension method'larına ayır.

**Önerilen yapı**:
```
dotnet/Api/
├── Program.cs                       # ~150 satır (orchestrator)
└── Endpoints/
    ├── MapAuth.cs                   # /api/auth/* (login, debug-ldap, me, groups)
    ├── MapAdmin.cs                  # /api/admin/skills, /api/admin/templates, /api/admin/documents
    ├── MapSql.cs                    # /api/admin/sql-connections/*
    ├── MapJobs.cs                   # /api/jobs/*, /api/admin/jobs/*
    ├── MapEventLog.cs               # /api/admin/event-log*
    ├── MapTools.cs                  # /api/tools/* (generate-file, benchmark)
    └── MapHealth.cs                 # /health, /health/deep, /metrics
```

Her dosyada bir static extension method:
```csharp
public static class AuthEndpoints {
    public static IEndpointRouteBuilder MapAuth(this IEndpointRouteBuilder app) {
        app.MapPost("/api/auth/login", ...);
        app.MapPost("/api/auth/debug-ldap", ...);
        // ...
        return app;
    }
}
```

Program.cs orchestrator:
```csharp
app.MapHealth();
app.MapAuth();
app.MapAdmin();
app.MapSql();
app.MapJobs();
app.MapEventLog();
app.MapTools();
```

**Yapılma stratejisi** (tek seferde değil, kademeli):
1. Önce `MapHealth.cs` — küçük, risk yok
2. `MapAuth.cs` — auth endpoint'leri
3. `MapTools.cs` — generate-file + benchmark
4. `MapEventLog.cs` — yeni event log endpoint'leri
5. `MapSql.cs` — büyük, en risk; sona bırak
6. Her adım sonrası `dotnet build` + smoke test (admin panelden bir aksiyon)

**Risk**: DTO'lar `Program.cs`'in altında scoped — bunlar `dotnet/Api/Dtos/` altına taşınmalı.

### AdminPage.tsx split (3,000+ satır)
**Hedef**: 11 sekmeyi ayrı dosyalara böl.

**Önerilen yapı**:
```
frontend/src/components/Admin/
├── AdminPage.tsx                    # ~200 satır (tab orchestrator + AdminGate)
└── tabs/
    ├── UploadTab.tsx
    ├── DocumentsTab.tsx
    ├── SkillsTab.tsx                # SkillOrderInput + ExampleForm da burada
    ├── TemplatesTab.tsx
    ├── SqlConnectionsTab.tsx        # + TableConfigEditor (SqlDataDialog ayrı)
    ├── JobsTab.tsx
    ├── UsageTab.tsx
    ├── ActivityTab.tsx
    ├── SecurityTab.tsx              # OWASP event log UI
    ├── BenchmarkTab.tsx             # Metric + DiffChip helper'ları
    └── SettingsTab.tsx
```

**Önemli**: Tab'lar tek bir AdminPage yardımcı state'ine bağımlı değil — her biri kendi state'ini yönetiyor. Migration mekanik.

**Tek karmaşık nokta**: `activeJob` state'i SqlConnectionsTab ve JobsTab arasında paylaşılıyor (SQL'den başlatılan job → JobProgressModal'da gösteriliyor). Bunu paylaşmak için ya Zustand store'a `adminActiveJob` ekle ya da context kullan.

**Yapılma stratejisi**:
1. `tabs/` klasörü oluştur
2. En basit tab ile başla: `UsageTab` veya `ActivityTab` (state izole)
3. Sırayla diğerleri
4. Son: SqlConnectionsTab + activeJob paylaşımı için store hookup
5. Her adım sonrası `npx tsc --noEmit` + ilgili sekme açma testi

## Deploy Pipeline

Detay: [OPERATIONS.md → Deploy](OPERATIONS.md#deploy)

**Tetikleyici**: `main` branch'e push.
**Süre**: ~20-25 saniye.
**Self-hosted runner**: DGX Spark üzerinde (`runs-on: self-hosted`).

**Önemli**: Skills/ klasörü deploy'da git'ten gelir (Faz önceki commit'lerde repoya commit'lendi). Sunucuya UI'dan eklenen skill'ler **deploy'da kaybolur** — repoya commit gerekli.
