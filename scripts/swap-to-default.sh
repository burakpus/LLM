#!/usr/bin/env bash
# =============================================================================
# swap-to-default.sh
# Transition: COLD (GPT-OSS 120B)  →  HOT (Gemma + Qwen)
# =============================================================================
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "${SCRIPT_DIR}")"
LOCK_FILE="/var/run/llm-swap.lock"
STATE_FILE="/var/run/llm-state"

if [[ -f "${PROJECT_DIR}/.env" ]]; then
  set -a; source "${PROJECT_DIR}/.env"; set +a
fi

LITELLM_URL="${LITELLM_URL:-http://localhost:4000}"
LITELLM_KEY="${LITELLM_MASTER_KEY:?LITELLM_MASTER_KEY not set}"
DRAIN_TIMEOUT="${DRAIN_TIMEOUT:-60}"
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-600}"

log()  { printf "[%s] %s\n" "$(date +%H:%M:%S)" "$*"; }
fail() { log "ERROR: $*" >&2; exit 1; }

exec 200>"${LOCK_FILE}" || fail "Cannot create lock file"
flock -n 200 || fail "Another swap is in progress (lock held)"

if [[ -f "${STATE_FILE}" ]] && [[ "$(cat "${STATE_FILE}")" == "default" ]]; then
  log "Already in DEFAULT state. Nothing to do."
  exit 0
fi

trap 'log "Swap aborted/failed. State may be inconsistent. Investigate."' ERR

# ─── STAGE 1 ────────────────────────────────────────────────────────────────
log "STAGE 1/5: Marking /reason as inactive"
curl -fsS -X POST "${LITELLM_URL}/model/update" \
  -H "Authorization: Bearer ${LITELLM_KEY}" \
  -H "Content-Type: application/json" \
  -d '{"model_name":"reason","litellm_params":{"is_active":false}}' \
  >/dev/null || log "warn: could not deactivate reason"

# ─── STAGE 2 ────────────────────────────────────────────────────────────────
log "STAGE 2/5: Draining in-flight requests for ${DRAIN_TIMEOUT}s"
sleep "${DRAIN_TIMEOUT}"

# ─── STAGE 3 ────────────────────────────────────────────────────────────────
log "STAGE 3/5: Stopping GPT-OSS"
cd "${PROJECT_DIR}"
docker compose --profile reasoning stop vllm-gptoss
docker compose --profile reasoning rm -f vllm-gptoss

sleep 10
GPU_USED=$(nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits | head -1)
log "GPU memory after stop: ${GPU_USED} MiB"

# ─── STAGE 4 ────────────────────────────────────────────────────────────────
log "STAGE 4/5: Starting Gemma + Qwen in parallel (warmup ~5 min)"
docker compose --profile default up -d vllm-gemma vllm-qwen

log "Polling both /health endpoints (timeout ${HEALTH_TIMEOUT}s)..."
elapsed=0; interval=15
gemma_ok=false; qwen_ok=false
while (( elapsed < HEALTH_TIMEOUT )); do
  if ! ${gemma_ok}; then
    if curl -fsS http://localhost:8000/health >/dev/null 2>&1; then
      log "  ✓ Gemma healthy at ${elapsed}s"
      gemma_ok=true
    fi
  fi
  if ! ${qwen_ok}; then
    if curl -fsS http://localhost:8001/health >/dev/null 2>&1; then
      log "  ✓ Qwen healthy at ${elapsed}s"
      qwen_ok=true
    fi
  fi
  if ${gemma_ok} && ${qwen_ok}; then break; fi
  sleep "${interval}"
  elapsed=$((elapsed + interval))
done

${gemma_ok} || fail "Gemma did not become healthy. Check: docker logs vllm-gemma"
${qwen_ok}  || fail "Qwen did not become healthy. Check: docker logs vllm-qwen"

# ─── STAGE 5 ────────────────────────────────────────────────────────────────
log "STAGE 5/5: Activating chat + code endpoints"
for model in chat code; do
  curl -fsS -X POST "${LITELLM_URL}/model/update" \
    -H "Authorization: Bearer ${LITELLM_KEY}" \
    -H "Content-Type: application/json" \
    -d "{\"model_name\":\"${model}\",\"litellm_params\":{\"is_active\":true}}" \
    >/dev/null
done

echo "default" > "${STATE_FILE}"
chmod 644 "${STATE_FILE}"

if [[ -n "${SLACK_WEBHOOK_URL:-}" ]]; then
  curl -fsS -X POST "${SLACK_WEBHOOK_URL}" \
    -H "Content-Type: application/json" \
    -d '{"text":":arrows_counterclockwise: *DGX Spark swap complete*\nState: `DEFAULT` (Gemma + Qwen active)\n`/reason` endpoint will return 503"}' \
    >/dev/null 2>&1 || true
fi

log "════════════════════════════════════════════════════════"
log "✓ Swap complete. State: DEFAULT"
log "  Active models: gemma4-31b (8000), qwen-coder (8001)"
log "  /reason endpoint will return 503 until swap-to-reasoning.sh"
log "════════════════════════════════════════════════════════"
