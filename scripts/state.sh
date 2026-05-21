#!/usr/bin/env bash
# =============================================================================
# state.sh — Inspect current swap state + model health
# =============================================================================
set -euo pipefail

STATE_FILE="/var/run/llm-state"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "${SCRIPT_DIR}")"

if [[ -f "${PROJECT_DIR}/.env" ]]; then
  set -a; source "${PROJECT_DIR}/.env"; set +a
fi

LITELLM_URL="${LITELLM_URL:-http://localhost:4000}"

# Colors
G='\033[0;32m'; R='\033[0;31m'; Y='\033[1;33m'; B='\033[1;34m'; N='\033[0m'

current_state="unknown"
[[ -f "${STATE_FILE}" ]] && current_state="$(cat "${STATE_FILE}")"

echo -e "${B}═════════════════════════════════════════════════════${N}"
echo -e "${B} DGX Spark LLM Stack — State Inspector${N}"
echo -e "${B}═════════════════════════════════════════════════════${N}"
echo -e " Persisted state : ${Y}${current_state}${N}"
echo -e " Time            : $(date)"
echo ""

# ─── GPU ─────────────────────────────────────────────────────────────────────
echo -e "${B}── GPU ──────────────────────────────────────────────${N}"
if command -v nvidia-smi >/dev/null 2>&1; then
  nvidia-smi --query-gpu=name,memory.used,memory.total,utilization.gpu,temperature.gpu \
    --format=csv,noheader
else
  echo "nvidia-smi not available"
fi
echo ""

# ─── Containers ──────────────────────────────────────────────────────────────
echo -e "${B}── Containers ───────────────────────────────────────${N}"
printf "%-20s %-10s %-10s %s\n" "NAME" "STATUS" "HEALTH" "PORTS"
for c in vllm-gemma vllm-qwen vllm-gptoss litellm traefik redis postgres prometheus grafana; do
  status=$(docker inspect -f '{{.State.Status}}' "$c" 2>/dev/null || echo "absent")
  health=$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}n/a{{end}}' "$c" 2>/dev/null || echo "-")
  ports=$(docker inspect -f '{{range $p, $conf := .NetworkSettings.Ports}}{{if $conf}}{{$p}} {{end}}{{end}}' "$c" 2>/dev/null || echo "-")

  case "$status" in
    running) color="$G" ;;
    absent)  color="$Y" ;;
    *)       color="$R" ;;
  esac
  printf "%-20s ${color}%-10s${N} %-10s %s\n" "$c" "$status" "$health" "$ports"
done
echo ""

# ─── vLLM Endpoints ──────────────────────────────────────────────────────────
echo -e "${B}── vLLM Health ──────────────────────────────────────${N}"
for entry in "Gemma:8000" "Qwen:8001" "GPT-OSS:8002"; do
  name="${entry%%:*}"; port="${entry##*:}"
  if curl -fsS --max-time 3 "http://localhost:${port}/health" >/dev/null 2>&1; then
    echo -e " ${name} (${port}): ${G}✓ healthy${N}"
  else
    echo -e " ${name} (${port}): ${R}✗ unreachable${N}"
  fi
done
echo ""

# ─── LiteLLM ─────────────────────────────────────────────────────────────────
echo -e "${B}── LiteLLM Gateway ──────────────────────────────────${N}"
if curl -fsS --max-time 3 "${LITELLM_URL}/health/liveliness" >/dev/null 2>&1; then
  echo -e " Gateway: ${G}✓ alive${N}"
  if [[ -n "${LITELLM_MASTER_KEY:-}" ]]; then
    echo " Models registered:"
    curl -fsS "${LITELLM_URL}/model/info" \
      -H "Authorization: Bearer ${LITELLM_MASTER_KEY}" 2>/dev/null \
      | grep -oE '"model_name":"[^"]+"' | sort -u | sed 's/^/  /' || echo "  (could not query)"
  fi
else
  echo -e " Gateway: ${R}✗ down${N}"
fi
echo ""

echo -e "${B}═════════════════════════════════════════════════════${N}"
