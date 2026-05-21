#!/usr/bin/env bash
# =============================================================================
# healthcheck.sh — Pre-swap diagnostics + smoke test
# Exit 0 = healthy, 1 = degraded, 2 = critical
# =============================================================================
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "${SCRIPT_DIR}")"

if [[ -f "${PROJECT_DIR}/.env" ]]; then
  set -a; source "${PROJECT_DIR}/.env"; set +a
fi

LITELLM_URL="${LITELLM_URL:-http://localhost:4000}"
LITELLM_KEY="${LITELLM_MASTER_KEY:-}"

errors=0; warnings=0

check() {
  local desc="$1"; local cmd="$2"; local severity="${3:-error}"
  if eval "$cmd" >/dev/null 2>&1; then
    echo "✓ ${desc}"
  else
    if [[ "$severity" == "warn" ]]; then
      echo "⚠ ${desc}"; warnings=$((warnings+1))
    else
      echo "✗ ${desc}"; errors=$((errors+1))
    fi
  fi
}

echo "── System Prerequisites ────────────────────────────"
check "docker installed"        "command -v docker"
check "docker compose plugin"   "docker compose version"
check "nvidia-smi available"    "command -v nvidia-smi"
check "GPU detected"            "nvidia-smi --query-gpu=name --format=csv,noheader | grep -qi spark || nvidia-smi --query-gpu=name --format=csv,noheader | grep -qi blackwell || nvidia-smi --query-gpu=name --format=csv,noheader | grep -q ."
check "curl available"          "command -v curl"
check "jq available (optional)" "command -v jq" warn

echo ""
echo "── Project Configuration ───────────────────────────"
check ".env exists"                     "[[ -f ${PROJECT_DIR}/.env ]]"
check "secrets/hf_token.txt exists"     "[[ -s ${PROJECT_DIR}/secrets/hf_token.txt ]]"
check "secrets/vllm_key.txt exists"     "[[ -s ${PROJECT_DIR}/secrets/vllm_key.txt ]]"
check "litellm-config.yaml exists"      "[[ -f ${PROJECT_DIR}/litellm-config.yaml ]]"
check "scripts executable"              "[[ -x ${SCRIPT_DIR}/swap-to-reasoning.sh ]] && [[ -x ${SCRIPT_DIR}/swap-to-default.sh ]]"

echo ""
echo "── Disk Space ──────────────────────────────────────"
avail_gb=$(df -BG "${PROJECT_DIR}" | awk 'NR==2 {gsub("G","",$4); print $4}')
if (( avail_gb < 200 )); then
  echo "⚠ Low disk space (${avail_gb}G). Models need ~150GB cache."
  warnings=$((warnings+1))
else
  echo "✓ Disk space OK (${avail_gb}G available)"
fi

echo ""
echo "── Container Runtime Health ────────────────────────"
for c in litellm redis postgres traefik; do
  if docker inspect -f '{{.State.Health.Status}}' "$c" 2>/dev/null | grep -q healthy; then
    echo "✓ ${c} healthy"
  elif docker inspect -f '{{.State.Status}}' "$c" 2>/dev/null | grep -q running; then
    echo "⚠ ${c} running but health unknown"; warnings=$((warnings+1))
  else
    echo "✗ ${c} not running"; errors=$((errors+1))
  fi
done

echo ""
echo "── LiteLLM Smoke Test ──────────────────────────────"
if [[ -n "${LITELLM_KEY}" ]]; then
  resp=$(curl -fsS --max-time 5 "${LITELLM_URL}/health/readiness" \
    -H "Authorization: Bearer ${LITELLM_KEY}" 2>/dev/null || echo "")
  if [[ -n "$resp" ]]; then
    echo "✓ LiteLLM readiness OK"
  else
    echo "✗ LiteLLM not ready"; errors=$((errors+1))
  fi
else
  echo "⚠ LITELLM_MASTER_KEY not set, skipping deep check"
  warnings=$((warnings+1))
fi

echo ""
echo "═══════════════════════════════════════════════════"
echo "Summary: ${errors} error(s), ${warnings} warning(s)"
echo "═══════════════════════════════════════════════════"

if (( errors > 0 )); then exit 2
elif (( warnings > 0 )); then exit 1
else exit 0; fi
