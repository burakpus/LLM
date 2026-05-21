# Architecture Decision Record

## Context

SetYazilim deploys three LLMs on a single NVIDIA DGX Spark (128 GB unified memory) for internal use:
- **Gemma 4 31B** — general assistant
- **Qwen3.6 35B A3B Coding** — code generation
- **GPT-OSS 120B** — reasoning, agent orchestration

Hardware constraint: 128 GB unified LPDDR5x. All BF16 weights would total ~400 GB — must quantize.

## Decision (Current)

Run **all 3 models always-on at 4-bit quantization** on the single Spark.

| Model | Quantization | VRAM | Rationale |
|---|---|---|---|
| Gemma 4 31B | FP4 (Blackwell native) | ~19 GB | 5th-gen Tensor Core acceleration on GB10 |
| Qwen3.6 35B A3B | AWQ Int4 | ~21 GB | Best 4-bit option for code accuracy |
| GPT-OSS 120B | MXFP4 (OpenAI native) | ~69 GB | Ships in this format from OpenAI |
| **Total** | | **~109 GB / 128 GB** | 19 GB headroom |

## Decision History

### Iteration 1: Hot/Cold Swap (BF16 Qwen + FP8 Gemma) — Rejected
- Hot: Gemma FP8 + Qwen BF16 (108 GB)
- Cold: GPT-OSS MXFP4 (69 GB), mutually exclusive
- 7-9 min cold-start to swap; complex ops surface

### Iteration 2 (Current): All-Hot 4-bit
- All 3 models simultaneously, no swap
- Trade quality (Qwen 4-bit) and per-model throughput (GPU sharing) for zero swap latency

## Why This Iteration

### Pros
- ✅ Zero cold-start — all endpoints always 200 OK
- ✅ No swap orchestration code/scripts
- ✅ Cross-model fallback now possible (LiteLLM)
- ✅ Simpler ops surface (no swap state, no Slack bot for swaps)
- ✅ Predictable latency (no swap interruptions)

### Cons
- ❌ Per-model throughput drops 30-40% (GPU compute shared 3 ways)
- ❌ Qwen Coder loses ~3 pts on HumanEval (BF16 → AWQ Int4)
- ❌ KV cache more contested (3 pools sharing memory pressure)
- ❌ Single GPU = no isolation between models — noisy neighbor possible

### Cons We Accept
- Throughput drop is acceptable for current concurrent user count (<20 typical)
- Code quality drop is small enough that most users won't notice; we have a fallback path
- KV pressure mitigated by `--kv-cache-dtype fp8` and `--max-num-seqs` caps

## Quantization Format Selection

### Why FP4 for Gemma (not AWQ)?
- Blackwell 5th-gen Tensor Cores have **native FP4 paths**
- ~1.8x throughput vs FP8 on inference-heavy workloads
- Gemma's general-knowledge use case tolerates 4-bit weight precision better than coding

### Why AWQ Int4 for Qwen Coder (not FP4)?
- AWQ preserves activation precision for outlier weights → better coding accuracy
- HumanEval delta:
  - BF16: 88
  - AWQ Int4: 85
  - FP4: 82 (estimated)
  - NF4 (BNB): 83
- AWQ is the proven minimum-loss 4-bit for code

### Why MXFP4 for GPT-OSS (already chosen)?
- OpenAI ships GPT-OSS 120B with native MXFP4 weights
- Designed for single 80GB-class GPU
- No further compression needed

## Trade-offs Accepted

### 1. Per-model throughput hit
3 vLLM processes share one GPU. Each runs its own continuous-batching scheduler — no fairness across processes. A bursty model temporarily starves others.

**Mitigation:**
- Per-model `--max-num-seqs` caps prevent any one from monopolizing
- LiteLLM `global_max_parallel_requests` caps total
- Grafana TTFT p99 alert catches latency creep early

### 2. Qwen Coder quality
~3% benchmark drop from BF16 → AWQ Int4. Real-world impact on a senior engineer's daily code-generation: noticeable on edge cases, not on common patterns.

**Mitigation:**
- A/B test against BF16 baseline if complaints arise
- `emergency-fallback-bf16` Makefile path reverts Qwen to BF16 (with hot/cold swap)
- LiteLLM virtual keys allow gradual rollout per team

### 3. Single point of failure
One DGX Spark = single GPU = single host. Any failure = full outage.

**Mitigation:**
- Daily Postgres backup
- HF model cache rebuilds in ~30 min
- DR drill quarterly

### 4. KV cache headroom is tight
~19 GB free out of 128 GB. Long context + high batch could push to OOM.

**Mitigation:**
- `--kv-cache-dtype fp8` halves KV memory
- `--max-model-len 16384` instead of 32768 (configurable later)
- Prometheus alert at 90% KV usage per model

## Alternatives Considered

### A. BF16 hot/cold swap (previous decision)
| | All-Hot 4-bit | BF16 Hot/Cold Swap |
|---|---|---|
| Cold-start | 0 | 7-9 min |
| Qwen quality | -3 pts | full BF16 |
| Endpoints concurrent | 3 | 2 |
| Per-model throughput | -35% | full |
| Ops complexity | low | high (swap, state, Slack) |

Rejected (this iteration) — operational simplicity outweighed quality margin.

### B. Mixed strategy: hot 4-bit Gemma+Qwen, cold MXFP4 GPT-OSS
- Hot: Gemma FP4 (19) + Qwen Int4 (21) = 40 GB
- Cold: GPT-OSS swap (69 GB)
- More headroom, larger context, but reasoning still has cold-start

Rejected — defeats the purpose of going to 4-bit. Either commit to all-hot or stay with BF16.

### C. Two DGX Spark cluster
- 256 GB unified memory via ConnectX-7
- Allows BF16 Qwen + MXFP4 GPT-OSS + FP8 Gemma all hot
- ~$4K additional CapEx

Rejected — out of current budget. Marked as future upgrade path.

### D. CPU offload for GPT-OSS (DGX Spark Grace CPU has fast LPDDR5x)
- vLLM has experimental CPU offload for MoE expert weights
- Latency penalty significant (~2-3x worse on first token)

Rejected — unproven for production at this scale.

## Performance Targets (SLOs)

| Endpoint | TTFT p95 SLO | Throughput SLO | Concurrent SLO |
|---|---|---|---|
| `/chat` | < 500 ms | > 50 tok/s/req | 40 users |
| `/code` | < 800 ms | > 30 tok/s/req | 20 users |
| `/reason` | < 1.5 s | > 20 tok/s/req | 10 users |

If sustained breach for 7 days → escalate to Iteration 3 (2nd Spark).

## Future Roadmap

| Trigger | Action |
|---|---|
| Concurrent users > 30 sustained | Add 2nd DGX Spark, separate models per node |
| Qwen Int4 quality complaints (NPS dip) | Run emergency BF16 fallback A/B test |
| Throughput SLO breach | Migrate vLLM → TensorRT-LLM (1.5-2x on Blackwell FP4) |
| Long context demand | Increase `max-model-len` to 32K, may require dropping a model |

## Authority & Review

- **Decision owner:** Engineering Manager
- **Review cadence:** Monthly for first quarter, then quarterly
- **Escalation:** Re-evaluate if Qwen code quality complaints exceed 5% of code-related sessions
