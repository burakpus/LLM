# =============================================================================
# DGX Spark LLM Stack — Convenience Makefile
# Usage: make <target>
# =============================================================================

SHELL := /bin/bash
.DEFAULT_GOAL := help

# Load .env if present
ifneq (,$(wildcard .env))
  include .env
  export
endif

.PHONY: help bootstrap up down up-default up-infra logs ps state \
        healthcheck pull-models clean nuke \
        build-dotnet build-slack tail-litellm tail-gemma tail-qwen tail-gptoss \
        emergency-fallback-bf16

help: ## Show this help
	@awk 'BEGIN {FS = ":.*?## "} /^[a-zA-Z_-]+:.*?## / {printf "  \033[36m%-20s\033[0m %s\n", $$1, $$2}' $(MAKEFILE_LIST)

bootstrap: ## First-time setup: copy .env, generate secrets
	@if [ ! -f .env ]; then cp .env.example .env; echo "→ .env created (edit it!)"; fi
	@mkdir -p secrets
	@if [ ! -f secrets/vllm_key.txt ]; then \
		openssl rand -hex 32 > secrets/vllm_key.txt; \
		echo "→ Generated secrets/vllm_key.txt"; \
	fi
	@if [ ! -f secrets/hf_token.txt ]; then \
		echo "hf_REPLACE_ME" > secrets/hf_token.txt; \
		echo "→ Created secrets/hf_token.txt (REPLACE WITH REAL TOKEN)"; \
	fi
	@chmod 600 secrets/*.txt
	@chmod +x scripts/*.sh
	@echo ""
	@echo "Now edit .env and secrets/hf_token.txt, then run:"
	@echo "  make up-default"

# ─── Lifecycle ──────────────────────────────────────────────────────────────
up-infra: ## Start infra only (Traefik, Redis, Postgres, LiteLLM, Grafana)
	docker compose --profile infra up -d

up-default: ## Start all 3 models (Gemma FP4 + Qwen Int4 + GPT-OSS MXFP4) + infra
	docker compose --profile default up -d
	@echo "default" | sudo tee /var/run/llm-state >/dev/null
	@echo ""
	@echo "All 3 models warming up. First boot ~9 min for largest model."
	@echo "Run 'make state' to check readiness."

up: up-default ## Alias for up-default

down: ## Stop everything
	docker compose --profile default --profile reasoning --profile infra down

# ─── Emergency Fallback (legacy swap tier — only if 4-bit causes issues) ───
emergency-fallback-bf16: ## DEPRECATED: revert to BF16 hot/cold tier (see ARCHITECTURE.md)
	@echo "Emergency fallback: BF16 hot tier (Gemma+Qwen) only."
	@echo "Edit docker-compose.yml to remove --quantization flags before running."
	@echo "GPT-OSS must be moved to 'reasoning' profile manually."
	@echo "See git history for previous BF16 hot/cold configuration."

# ─── Inspection ─────────────────────────────────────────────────────────────
state: ## Show current state + container health
	./scripts/state.sh

ps: ## docker compose ps
	docker compose --profile default --profile reasoning --profile infra ps

healthcheck: ## Run pre-flight diagnostics
	./scripts/healthcheck.sh

logs: ## Tail logs of all services
	docker compose logs -f --tail=100

tail-litellm: ## Tail LiteLLM logs
	docker logs -f --tail=200 litellm

tail-gemma: ## Tail Gemma logs
	docker logs -f --tail=200 vllm-gemma

tail-qwen: ## Tail Qwen logs
	docker logs -f --tail=200 vllm-qwen

tail-gptoss: ## Tail GPT-OSS logs
	docker logs -f --tail=200 vllm-gptoss

# ─── Maintenance ────────────────────────────────────────────────────────────
pull-models: ## Pre-pull all models into HF cache (avoids first-boot delays)
	docker run --rm --gpus all \
		-v vllm-cache:/root/.cache/huggingface \
		-e HF_TOKEN=$$(cat secrets/hf_token.txt) \
		python:3.11-slim bash -c "\
			pip install huggingface_hub && \
			python -c 'from huggingface_hub import snapshot_download; \
				snapshot_download(\"google/gemma-4-31b-it\"); \
				snapshot_download(\"saricles/Qwen3-Coder-Next-NVFP4-GB10\"); \
				snapshot_download(\"openai/gpt-oss-120b\")'"

clean: ## Remove containers but keep volumes (model cache preserved)
	docker compose --profile default --profile reasoning --profile infra down

nuke: ## DESTRUCTIVE: remove containers AND volumes (model cache lost!)
	@read -p "This will delete model cache (~200GB to re-download). Proceed? [y/N] " ans && [ "$$ans" = "y" ]
	docker compose --profile default --profile reasoning --profile infra down -v

# ─── Build (.NET artifacts) ─────────────────────────────────────────────────
build-dotnet: ## Build .NET client library
	cd dotnet && dotnet build -c Release

build-slack: ## Build Slack bot
	cd slack && dotnet publish -c Release -o ../bin/slackbot
