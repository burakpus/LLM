# DGX Spark — Multi-Model LLM Stack (All-Hot 4-bit)

Production-ready deployment of three large language models on a single NVIDIA DGX Spark. All three models are co-resident and always available — no swap, no cold-start.

## Architecture Overview

| Endpoint | Model | Quantization | VRAM |
|---|---|---|---|
| `/chat` | Gemma 4 31B | FP4 (Blackwell native) | ~19 GB |
| `/code` | Qwen3-Coder-Next | — | — |
| `/reason` | GPT-OSS 120B | MXFP4 (native) | ~69 GB |
| | | **Total** | **~109 GB / 128 GB** |

```
┌──────────────────────────────────────────────────────────────────┐
│  Clients (.NET / OpenWebUI / IDE)                                │
│         │                                                         │
│         ▼  HTTPS + JWT                                            │
│  ┌─────────────┐                                                  │
│  │  Traefik    │ ─ TLS, rate limit, security headers              │
│  └──────┬──────┘                                                  │
│         ▼                                                         │
│  ┌─────────────┐                                                  │
│  │  LiteLLM    │ ─ routing, virtual keys, cost, fallback          │
│  └──────┬──────┘                                                  │
│         ▼                                                         │
│  ┌──────────────── DGX Spark (128 GB) ────────────────┐          │
│  │  vLLM Gemma FP4   (port 8000)                       │          │
│  │  vLLM Qwen  Int4  (port 8001)                       │          │
│  │  vLLM GPT-OSS MXFP4 (port 8002)                     │          │
│  └────────────────────────────────────────────────────┘          │
└──────────────────────────────────────────────────────────────────┘
```

## Why 4-bit All-Hot

Trade-off taken: **Lower per-model throughput vs zero swap latency + 100% endpoint availability**.

| Metric | Previous (BF16 swap) | This (4-bit all-hot) |
|---|---|---|
| Cold-start | 7-9 min on demand | **None** |
| Concurrent endpoints | 2 of 3 | **All 3** |
| Qwen Coder quality (HumanEval) | ~88 | ~85 (-3 pts) |
| Gemma per-model throughput | 80-100 tok/s | 50-70 tok/s (GPU shared) |
| GPT-OSS per-model throughput | 30-45 tok/s | 18-28 tok/s (GPU shared) |
| Operational complexity | Swap scripts, state mgmt | None |

**See `ARCHITECTURE.md` for full decision rationale.**

## Prerequisites

| Requirement | Notes |
|---|---|
| NVIDIA DGX Spark | GB10 Grace Blackwell, 128 GB unified memory |
| NVIDIA driver | >=550 (FP4 + Blackwell tensor cores) |
| NVIDIA Container Toolkit | for `--gpus all` |
| Docker Engine | 24.0+ with Compose v2 |
| vLLM | v0.7.3+ (FP4 support) |
| Disk space | ~150 GB for HF model cache (4-bit weights are smaller) |
| HuggingFace tokens | with read access for all 3 models |

## Quick Start

```bash
make bootstrap                  # creates .env, generates secrets
$EDITOR .env                    # set DOMAIN, passwords, HF_TOKEN
$EDITOR secrets/hf_token.txt
make pull-models                # pre-pull (~20 min, optional)
make up-default                 # all 3 models + infra
make state                      # readiness check
```

First boot: GPT-OSS 120B warmup is ~8-9 min, others ~5 min. After warmup, all endpoints serve concurrently.

## Repository Layout

```
dgx-spark-llm-stack/
├── docker-compose.yml          # default profile = all 3 models + infra
├── litellm-config.yaml         # 3 endpoints with cross-model fallback
├── .env.example
├── Makefile
├── README.md                   # ← you are here
├── ARCHITECTURE.md             # decision rationale + alternatives
├── OPERATIONS.md               # deploy, monitor, incident response
│
├── scripts/
│   ├── state.sh                # health + state inspector
│   ├── healthcheck.sh          # pre-flight diagnostics
│   ├── swap-to-reasoning.sh    # LEGACY: kept for emergency BF16 fallback
│   └── swap-to-default.sh      # LEGACY: kept for emergency BF16 fallback
│
├── traefik/                    # TLS + middleware
├── monitoring/                 # Prometheus + Grafana + DCGM + alerts
├── secrets/                    # gitignored Docker secrets
│
├── dotnet/                     # .NET 8 client library (ILlmClient + Polly)
└── slack/                      # /llm-state slash command bot (swap removed)
```

## Operational Commands

| Action | Command |
|---|---|
| Show state + container health | `make state` |
| Pre-flight diagnostics | `make healthcheck` |
| Tail LiteLLM logs | `make tail-litellm` |
| Tail per-model logs | `make tail-gemma` / `tail-qwen` / `tail-gptoss` |
| Open Grafana | http://localhost:3000 |
| Open LiteLLM admin | http://localhost:4000/ui |
| Open Traefik dashboard | http://localhost:8080 |

## .NET Client Usage

```csharp
builder.Services.AddLiteLLMClient();

public class FinanceService(ILlmClient llm)
{
    public async Task<string> SummarizeAsync(string doc, CancellationToken ct)
        => await llm.AskAsync(LlmModel.Chat, $"Özetle: {doc}", cancellationToken: ct);

    public async Task<string> GenerateSqlAsync(string nl, CancellationToken ct)
        => await llm.AskAsync(LlmModel.Code, $"Generate PostgreSQL: {nl}",
            systemPrompt: "Return only SQL.", cancellationToken: ct);

    public async Task<string> ReasonAsync(string complex, CancellationToken ct)
        => await llm.AskAsync(LlmModel.Reason, complex, cancellationToken: ct);
        // No more LlmModelUnavailableException handling — endpoint is always live
}
```

`appsettings.json`:
```json
{
  "LiteLLM": {
    "BaseUrl": "https://llm.internal",
    "ApiKey": "sk-virtual-key-from-litellm-ui"
  }
}
```

Polly retry pipeline + circuit breaker + streaming via `IAsyncEnumerable<StreamingChunk>` are pre-wired.

## Performance Expectations (All 3 Hot, GPU Shared)

| Model | TTFT p50 | TTFT p95 | Throughput (single req) | Concurrent users |
|---|---|---|---|---|
| Gemma 4 31B FP4 | 200 ms | 450 ms | 50-70 tok/s | 40-50 |
| Qwen3.6 35B Int4 | 350 ms | 700 ms | 25-40 tok/s | 20-30 |
| GPT-OSS 120B MXFP4 | 700 ms | 1.4 s | 18-28 tok/s | 10-15 |

These are **shared-GPU** numbers. If only one model is taking traffic, throughput rises ~30-40% as the others sit idle.

## Quality Trade-offs (4-bit Quantization)

| Model | Benchmark | BF16 Baseline | 4-bit Estimate | Δ |
|---|---|---|---|---|
| Gemma 4 31B (FP4) | MMLU | ~75 | ~73 | -2 pts |
| Qwen Coder (Int4) | HumanEval | ~88 | ~85 | -3 pts |
| Qwen Coder (Int4) | MBPP | ~82 | ~79 | -3 pts |
| GPT-OSS 120B (MXFP4) | MMLU-Pro | native | native | 0 (designed for MXFP4) |

If users notice degraded code quality, run `emergency-fallback-bf16` (see `ARCHITECTURE.md`) to revert Qwen to BF16 with hot/cold swap.

## Monitoring

| Endpoint | URL |
|---|---|
| Grafana | http://localhost:3000 |
| Prometheus | http://localhost:9090 |
| LiteLLM admin | http://localhost:4000/ui |
| DCGM metrics | http://localhost:9400/metrics |

**Key alerts (preconfigured):**
- `VRAMNearOOM` — GPU > 95% for 2m
- `BothStatesLoaded` removed (no longer applicable)
- `ModelDown` — vLLM `up == 0` for 2m
- `SlowTTFT` — TTFT p99 > 3s for 5m
- `LiteLLMDown` — gateway down for 1m
- `BudgetExhausted` — team < 5% remaining

## Security

- ✅ vLLM API keys (header `X-API-Key`) — only LiteLLM holds them
- ✅ TLS 1.2+ at Traefik with HSTS + strict ciphers
- ✅ Rate limiting (100 req/s per source IP)
- ✅ IP allow-list middleware (corp CIDR)
- ✅ Audit log → Postgres (no raw prompts)
- ✅ Slack signature verification (HMAC SHA-256)

## Risk Register

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| FP4 release for Gemma not on HF | medium | boot fail | Fall back to AWQ Int4 (proven) |
| Qwen Int4 quality complaints | medium | UX regression | `emergency-fallback-bf16` swap config |
| VRAM oversubscribe under spike | medium | 5xx | KV cache `fp8` + `max-num-seqs` cap + alert |
| GPU compute contention | high | latency variance | Monitor TTFT p99; rate limit if needed |
| Single Spark = SPOF | low | outage | Daily Postgres backup, DR rebuild ~30 min |

## Roadmap

| Phase | Trigger | Action |
|---|---|---|
| Now | All-hot 4-bit | This deployment |
| Q+1 | Code quality drops noticeably | A/B test BF16 Qwen via emergency fallback |
| Q+2 | Concurrent users > 50 | Add 2nd DGX Spark, partition models |
| Q+3 | RAG demand | Add bge-m3 + Qdrant on 2nd Spark |
| Q+4 | Throughput cap | Migrate vLLM → TensorRT-LLM (1.5-2x on Blackwell) |

## License

Internal use. Adapt freely within your organization.
