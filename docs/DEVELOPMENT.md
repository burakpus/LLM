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

(Faz 3.3 sonrası magic numbers config'e taşınınca tablo dolacak)

`appsettings.json` ana bölümler:
- `ConnectionString` — PostgreSQL
- `Jwt` — secret, issuer, audience
- `Ldap` — Domains, AdminUsers, AdminGroups
- `Agent` — SkillsDirectory, SessionTtlHours
- `Tools` — PythonExe, FileGenScript, GeneratedRoot
- `Jobs` — Workers (concurrency)
- `Limits` (Faz 3.3) — MaxRequestBodySize, MaxChars, MaxPerMinute, RetentionDays
- `Cors` (Faz 2.4) — AllowedOrigins

## Deploy Pipeline

Detay: [OPERATIONS.md → Deploy](OPERATIONS.md#deploy)

**Tetikleyici**: `main` branch'e push.
**Süre**: ~20-25 saniye.
**Self-hosted runner**: DGX Spark üzerinde (`runs-on: self-hosted`).

**Önemli**: Skills/ klasörü deploy'da git'ten gelir (Faz önceki commit'lerde repoya commit'lendi). Sunucuya UI'dan eklenen skill'ler **deploy'da kaybolur** — repoya commit gerekli.
