# Operations Runbook — DGX Spark LLM Stack

## Initial Deployment Checklist

### Phase 1: Host Preparation

- [ ] DGX Spark powered on, network configured
- [ ] NVIDIA driver installed (`nvidia-smi` shows GPU + driver >= 550)
- [ ] Docker Engine 24+ + Compose v2 installed
- [ ] NVIDIA Container Toolkit installed:
      ```bash
      curl -fsSL https://nvidia.github.io/libnvidia-container/gpgkey | sudo gpg --dearmor -o /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg
      curl -s -L https://nvidia.github.io/libnvidia-container/stable/deb/nvidia-container-toolkit.list \
        | sed 's#deb https://#deb [signed-by=/usr/share/keyrings/nvidia-container-toolkit-keyring.gpg] https://#' \
        | sudo tee /etc/apt/sources.list.d/nvidia-container-toolkit.list
      sudo apt update && sudo apt install -y nvidia-container-toolkit
      sudo nvidia-ctk runtime configure --runtime=docker
      sudo systemctl restart docker
      ```
- [ ] Verify: `docker run --rm --gpus all nvidia/cuda:12.4.1-base-ubuntu22.04 nvidia-smi`
- [ ] Disk: at least 250 GB free at `/var/lib/docker`
- [ ] DNS: `llm.internal` resolves to Spark host IP

### Phase 2: Stack Deploy

```bash
git clone <your-repo> /opt/dgx-spark-llm-stack
cd /opt/dgx-spark-llm-stack

make bootstrap
$EDITOR .env                    # set DOMAIN, passwords
$EDITOR secrets/hf_token.txt    # paste real HF token

# TLS certs: drop into traefik/certs/llm.internal.{crt,key}
# Or enable Let's Encrypt in traefik/traefik.yml

# Pre-pull models (~30 min, optional but recommended)
make pull-models

# Boot
make up-default
make state                      # verify
```

### Phase 3: Smoke Tests

```bash
# Liveness
curl -fsS https://llm.internal/health/liveliness

# List models (using master key)
curl -fsS https://llm.internal/model/info -H "Authorization: Bearer $LITELLM_MASTER_KEY"

# Chat completion
curl -X POST https://llm.internal/v1/chat/completions \
  -H "Authorization: Bearer $LITELLM_MASTER_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "chat",
    "messages": [{"role":"user","content":"Merhaba, kendini tanıt"}],
    "max_tokens": 256
  }'

# Code generation
curl -X POST https://llm.internal/v1/chat/completions \
  -H "Authorization: Bearer $LITELLM_MASTER_KEY" \
  -d '{"model":"code","messages":[{"role":"user","content":"Generate C# DTO for a User with Id, Name, Email"}]}'
```

### Phase 4: Create Virtual Keys for Apps

Open https://llm.internal/ui (LiteLLM admin), log in with `LITELLM_MASTER_KEY`.

For each consuming app/team:
1. **Teams → New Team** → name, monthly budget, allowed models
2. **Virtual Keys → New Key** → assign to team, copy `sk-...` into app config
3. Apps use the virtual key, NOT the master key

## Common Operations

### Daily Health Check

```bash
make state
make healthcheck
docker stats --no-stream
```

### View Cost Per Team

```bash
curl -fsS https://llm.internal/spend/teams \
  -H "Authorization: Bearer $LITELLM_MASTER_KEY" | jq
```

Or in Grafana: `Cost Tracking ($)` panel.

### Rotate vLLM Internal Key

```bash
# 1. Generate new key
NEW_KEY=$(openssl rand -hex 32)

# 2. Update secret file
echo "$NEW_KEY" > secrets/vllm_key.txt
chmod 600 secrets/vllm_key.txt

# 3. Update .env
sed -i "s/^VLLM_KEY=.*/VLLM_KEY=$NEW_KEY/" .env

# 4. Restart vLLM containers + LiteLLM (zero-downtime not guaranteed)
docker compose --profile default restart vllm-gemma vllm-qwen litellm
```

### Update Model Version

```bash
# 1. Edit docker-compose.yml: change --model path or --quantization
$EDITOR docker-compose.yml

# 2. Recreate just that model
docker compose --profile default up -d --force-recreate vllm-gemma

# 3. Watch warmup
make tail-gemma
```

## Incident Response

### Symptom: High latency

```bash
# 1. Check GPU utilization + queue depth
nvidia-smi
make state

# 2. Check Grafana TTFT panel
open http://localhost:3000

# 3. Look for KV cache pressure
curl -s http://localhost:8000/metrics | grep gpu_cache_usage

# Mitigation:
# - If KV > 90%: temporarily reduce --max-num-seqs
# - If queue > 20: clients are over-subscribing, raise concurrency limits
# - If model loading: check warmup time (first 5 min normal)
```

### Symptom: 503 from `/chat` or `/code`

```bash
# Most likely: swap-to-reasoning was triggered
cat /var/run/llm-state    # if "reasoning", that's why

# To restore default:
make swap-default
```

### Symptom: 503 from `/reason`

Expected when in default state. Either:
- Trigger swap: `make swap-reasoning` (~7 min wait)
- Or queue request async via .NET background worker (see Examples.ReasoningWithFallback)

### Symptom: Both states loaded (alert: BothStatesLoaded)

**Critical** — VRAM oversubscribe imminent.

```bash
docker ps --filter "label=llm.tier"
# If you see vllm-gemma + vllm-qwen + vllm-gptoss all running, swap script failed mid-way

# Force to known state:
docker compose --profile reasoning down -v vllm-gptoss
echo "default" | sudo tee /var/run/llm-state
```

### Symptom: vLLM container OOM

```bash
docker logs vllm-gemma --tail 200 | grep -i "out of memory\|oom"

# Mitigations (in order):
# 1. Reduce --max-num-seqs (fewer concurrent requests = smaller KV pool)
# 2. Reduce --max-model-len (shorter context = smaller KV per request)
# 3. Enable --kv-cache-dtype fp8 if not already
# 4. Use AWQ Int4 quantization for that model
```

### Symptom: Cold-start fails (model not loading)

```bash
docker logs vllm-gptoss --tail 500

# Common causes:
# - HF token expired or wrong: regen at huggingface.co
# - HF Hub rate limit: wait 15 min, retry
# - Disk full: df -h /var/lib/docker
# - Model path typo: verify on huggingface.co first
```

## Backup & DR

### What to Back Up

| Item | Frequency | Location |
|---|---|---|
| `.env` (secrets) | on change | encrypted password manager |
| `secrets/*.txt` | on change | encrypted vault |
| LiteLLM Postgres (audit, virtual keys) | daily | external S3-compatible bucket |
| `litellm-config.yaml` | on change | git |
| Grafana dashboards | weekly | git (export via UI) |
| `vllm-cache` Docker volume | NOT needed | re-downloadable from HF |

Daily Postgres backup script:
```bash
#!/usr/bin/env bash
docker exec postgres pg_dump -U litellm litellm | gzip > /backups/litellm-$(date +%F).sql.gz
# Sync to S3, MinIO, etc.
```

### DR Recovery

```bash
# 1. Provision new DGX Spark
# 2. Run Phase 1+2 from Initial Deployment
# 3. Restore Postgres dump
gunzip -c /backups/litellm-LATEST.sql.gz | docker exec -i postgres psql -U litellm
# 4. Restart LiteLLM
docker compose restart litellm
# 5. Verify virtual keys work
```

## Capacity Planning

### Adding a 4th Model

If a fourth model is needed always-on, options:
1. **Quantize existing models more aggressively** (AWQ Int4) to free VRAM
2. **Add a second DGX Spark** (cluster via ConnectX-7) — doubles VRAM
3. **Move new model to cold tier** with shared swap (only one cold model at a time)

### When to Upgrade

| Trigger | Upgrade |
|---|---|
| Hot tier queue > 20 sustained | Add second Spark |
| TTFT p99 > 5s sustained | Investigate prefix cache hit rate; consider AWQ for code model |
| KV cache > 95% during peak | Cap `max-num-seqs` or reduce `max-model-len` |
| User base grew 3x | Capacity plan + load test |

## Compliance & Audit

LiteLLM Postgres tables hold:
- `litellm_spendlogs` — every request, tokens, cost, model, team, user
- `litellm_verificationtoken` — virtual keys + scopes
- `litellm_teamtable` — team budgets

Forward via Logstash/Vector to your SIEM:
```bash
docker exec postgres psql -U litellm -c "SELECT * FROM litellm_spendlogs WHERE startTime > now() - interval '24h'" > audit-daily.csv
```

For GDPR/KVKK: prompts are NOT logged (`store_prompts_in_spend_logs: false`). Only metadata.
