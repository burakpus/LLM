#!/usr/bin/env bash
# =============================================================================
# swap-to-reasoning.sh
# Transition: HOT (Gemma + Qwen)  →  COLD (GPT-OSS 120B)
# =============================================================================
set -Eeuo pipefail

# Resolve script dir for compose file path
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "${SCRIPT_DIR}")"
LOCK_FILE="/var/run/llm-swap.lock"
STATE_FILE="/var/run/llm-state"

# Load env
if [[ -f "${PROJECT_DIR}/.env" ]]; then
  set -a; source "${PROJECT_DIR}/.env"; set +a
fi

LITELLM_URL="${LITELLM_URL:-http://localhost:4000}"
LITELLM_KEY="${LITELLM_MASTER_KEY:?LITELLM_MASTER_KEY not set}"
DRAIN_TIMEOUT="${DRAIN_TIMEOUT:-60}"
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-600}"

log()  { printf "[%s] %s\n" "$(date +%H:%M:%S)" "$*"; }
fail() { log "ERROR: $*" >&2; exit 1; }

# ─── Lock to prevent concurrent swaps ───────────────────────────────────────
exec 200>"${LOCK_FILE}" || fail "Cannot create lock file"
flock -n 200 || fail "Another swap is in progress (lock held)"

# Idempotency check
if [[ -f "${STATE_FILE}" ]] && [[ "$(cat "${STATE_FILE}")" == "reasoning" ]]; then
  log "Already in REASONING state. Nothing to do."
  exit 0
fi

trap 'log "Swap aborted/failed. State may be inconsistent. Investigate."' ERR

# ─── STAGE 1: Mark hot models inactive in LiteLLM ──────────────────────────
log "STAGE 1/5: Marking chat/code endpoints as inactive in LiteLLM"
for model in chat code; do
  curl -fsS -X POST "${LITELLM_URL}/model/update" \
    -H "Authorization: Bearer ${LITELLM_KEY}" \
    -H "Content-Type: application/json" \
    -d "{\"model_name\":\"${model}\",\"litellm_params\":{\"is_active\":false}}" \
    >/dev/null || log "warn: could not deactivate ${model} (continuing)"
done

# ─── STAGE 2: Drain in-flight requests ──────────────────────────────────────
log "STAGE 2/5: Draining in-flight requests for ${DRAIN_TIMEOUT}s"
sleep "${DRAIN_TIMEOUT}"

# ─── STAGE 3: Stop hot containers ──────────────────────────────────────────
log "STAGE 3/5: Stopping Gemma and Qwen containers"
cd "${PROJECT_DIR}"
docker compose --profile default stop vllm-gemma vllm-qwen
docker compose --profile default rm -f vllm-gemma vllm-qwen

# Wait for VRAM to release
sleep 10
GPU_USED=$(nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits | head -1)
log "GPU memory after stop: ${GPU_USED} MiB"
if (( GPU_USED > 30000 )); then
  log "warn: VRAM still high (${GPU_USED} MiB). Waiting additional 15s..."
  sleep 15
fi

# ─── STAGE 4: Start GPT-OSS ────────────────────────────────────────────────
log "STAGE 4/5: Starting GPT-OSS 120B (warmup ~7-8 min)"
docker compose --profile reasoning up -d vllm-gptoss

# Poll health
log "Polling /health (timeout ${HEALTH_TIMEOUT}s)..."
elapsed=0
interval=15
until curl -fsS http://localhost:8002/health >/dev/null 2>&1; do
  if (( elapsed >= HEALTH_TIMEOUT )); then
    fail "GPT-OSS did not become healthy within ${HEALTH_TIMEOUT}s. Check logs: docker logs vllm-gptoss"
  fi
  sleep "${interval}"
  elapsed=$((elapsed + interval))
  log "  ...waiting (${elapsed}s elapsed)"
done
log "✓ GPT-OSS healthy after ${elapsed}s"

# ─── STAGE 5: Activate reasoning endpoint ──────────────────────────────────
log "STAGE 5/5: Activating /reason endpoint in LiteLLM"
curl -fsS -X POST "${LITELLM_URL}/model/update" \
  -H "Authorization: Bearer ${LITELLM_KEY}" \
  -H "Content-Type: application/json" \
  -d '{"model_name":"reason","litellm_params":{"is_active":true}}' \
  >/dev/null

# Persist state
echo "reasoning" > "${STATE_FILE}"
chmod 644 "${STATE_FILE}"

# ─── Notify ────────────────────────────────────────────────────────────────
if [[ -n "${SLACK_WEBHOOK_URL:-}" ]]; then
  curl -fsS -X POST "${SLACK_WEBHOOK_URL}" \
    -H "Content-Type: application/json" \
    -d '{"text":":arrows_counterclockwise: *DGX Spark swap complete*\nState: `REASONING` (GPT-OSS 120B active)\n`/chat` and `/code` endpoints will return 503"}' \
    >/dev/null 2>&1 || true
fi

log "════════════════════════════════════════════════════════"
log "✓ Swap complete. State: REASONING"
log "  Active model: gpt-oss-120b @ port 8002"
log "  /chat and /code endpoints will return 503 until swap-to-default.sh"
log "════════════════════════════════════════════════════════"
