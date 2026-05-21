# Secrets

This directory holds **plaintext** secret files mounted into containers as Docker secrets.
**NEVER** commit the actual files — only `.gitkeep` and this README are tracked.

## Required files

| File | Source | Permissions |
|---|---|---|
| `hf_token.txt` | https://huggingface.co/settings/tokens (read scope) | 600 |
| `vllm_key.txt` | `openssl rand -hex 32` | 600 |

## Generate

```bash
# vLLM internal API key
openssl rand -hex 32 > secrets/vllm_key.txt
chmod 600 secrets/vllm_key.txt

# Mirror the same value in .env (VLLM_KEY=...) so LiteLLM can authenticate
echo "VLLM_KEY=$(cat secrets/vllm_key.txt)" >> .env

# HuggingFace token
echo "hf_xxxxxxxxxxxxxxxxxxxx" > secrets/hf_token.txt
chmod 600 secrets/hf_token.txt
```

## Production hardening

For real production, replace this with HashiCorp Vault, AWS Secrets Manager, or sealed
secrets in K3s. Docker secrets here are file-based and only encrypt-at-rest if the
underlying volume is encrypted.
