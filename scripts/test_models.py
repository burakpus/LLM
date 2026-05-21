#!/usr/bin/env python3
"""
Remote test script for the DGX Spark LLM stack.

Default: tests all 3 models through the LiteLLM proxy on :4000
--direct: hits each vLLM container port directly (8000/8001/8002)

Usage:
  python test_models.py
  python test_models.py --host 172.16.1.123 --key sk-...
  python test_models.py --host llm.internal --tls
  python test_models.py --direct --vllm-key <key>
  python test_models.py --models chat code
  python test_models.py --stream
"""

import argparse
import os
import sys
import time

try:
    from openai import OpenAI
except ImportError:
    print("ERROR: openai package not found.  Run: pip install openai")
    sys.exit(1)

# ── ANSI colours ──────────────────────────────────────────────────────────────
GREEN  = "\033[92m"
RED    = "\033[91m"
YELLOW = "\033[93m"
CYAN   = "\033[96m"
BOLD   = "\033[1m"
RESET  = "\033[0m"

# ── Per-model config ──────────────────────────────────────────────────────────
#
# proxy_name  : model name as seen by LiteLLM
# direct_port : vLLM port when using --direct
# direct_name : model name served by vLLM directly
MODELS = {
    "chat": {
        "description": "Gemma 4 31B FP4 — general assistant",
        "proxy_name":  "chat",
        "direct_port": 8000,
        "direct_name": "gemma4-31b",
        "messages": [{"role": "user", "content": "What is the capital of France? Answer in one sentence."}],
        "max_tokens": 60,
    },
    "code": {
        "description": "Qwen3-Coder 30B FP4 — code generation",
        "proxy_name":  "code",
        "direct_port": 8001,
        "direct_name": "qwen-coder",
        "messages": [{"role": "user", "content": "Write a Python one-liner that returns the sum of squares of a list."}],
        "max_tokens": 80,
    },
    "reason": {
        "description": "GPT-OSS 120B MXFP4 — reasoning",
        "proxy_name":  "reason",
        "direct_port": 8002,
        "direct_name": "gpt-oss-120b",
        "messages": [{"role": "user", "content": "A bat and ball cost $1.10 total. The bat costs $1 more than the ball. How much does the ball cost? Show your reasoning."}],
        "max_tokens": 150,
    },
}

STREAM_PROMPT = [{"role": "user", "content": "Count to 5, one number per line."}]


def parse_args():
    p = argparse.ArgumentParser(description="Test all LLM models on the DGX Spark stack")

    p.add_argument("--host",      default=os.environ.get("LLM_HOST", "172.16.1.123"),
                   help="Server IP or hostname (default: 172.16.1.123)")
    p.add_argument("--port",      type=int, default=int(os.environ.get("LLM_PORT", 4000)),
                   help="LiteLLM proxy port (default: 4000, ignored with --direct)")
    p.add_argument("--key",       default=os.environ.get("LLM_KEY",
                   "sk-50eed655181eda682d8692cb8d8de756980a077bbfe7a3ba1901a7befb74954a"),
                   help="LiteLLM master key (or virtual key)")
    p.add_argument("--vllm-key",  default=os.environ.get("VLLM_KEY",
                   "9cbef01e3edede04363783bbe87ed51fc178aa0a3586697cde4aa915ebcdaecf"),
                   help="vLLM API key used with --direct")
    p.add_argument("--tls",       action="store_true",
                   help="Use HTTPS (only applies to proxy mode)")
    p.add_argument("--direct",    action="store_true",
                   help="Bypass LiteLLM — call each vLLM container port directly")
    p.add_argument("--models",    nargs="+", choices=list(MODELS.keys()),
                   default=list(MODELS.keys()),
                   help="Which models to test (default: all)")
    p.add_argument("--stream",    action="store_true",
                   help="Also run a streaming test for each model")
    p.add_argument("--timeout",   type=int, default=120,
                   help="Request timeout in seconds (default: 120)")
    return p.parse_args()


# ── Helpers ───────────────────────────────────────────────────────────────────

def hr(label=""):
    bar = "─" * 60
    print(f"\n{BOLD}{CYAN}{bar}{RESET}")
    if label:
        print(f"{BOLD}{CYAN}  {label}{RESET}")
        print(f"{BOLD}{CYAN}{bar}{RESET}")


def proxy_client(host, port, key, tls):
    scheme = "https" if tls else "http"
    return OpenAI(api_key=key, base_url=f"{scheme}://{host}:{port}/v1")


def direct_client(host, port, key):
    return OpenAI(api_key=key, base_url=f"http://{host}:{port}/v1")


def check_health(base_url: str, timeout: int = 5) -> bool:
    import urllib.request
    health_url = base_url.rstrip("/").removesuffix("/v1") + "/health/liveliness"
    try:
        urllib.request.urlopen(health_url, timeout=timeout)
        print(f"  {GREEN}✓ reachable{RESET}  ({health_url})")
        return True
    except Exception as e:
        print(f"  {YELLOW}⚠ health check skipped: {e}{RESET}")
        return False


def list_models(client):
    try:
        names = sorted(m.id for m in client.models.list().data)
        if names:
            print(f"  Models registered: {', '.join(names)}")
        else:
            print(f"  {YELLOW}No models listed (may still work){RESET}")
    except Exception as e:
        print(f"  {YELLOW}Could not list models: {e}{RESET}")


def run_test(client, model_name: str, cfg: dict, timeout: int, stream: bool) -> bool:
    print(f"\n  {BOLD}{model_name}{RESET}  —  {cfg['description']}")

    # Non-streaming ────────────────────────────────────────────────────────────
    ok = False
    try:
        t0 = time.perf_counter()
        resp = client.chat.completions.create(
            model=model_name,
            messages=cfg["messages"],
            max_tokens=cfg["max_tokens"],
            temperature=0,
            timeout=timeout,
        )
        elapsed = time.perf_counter() - t0
        usage   = resp.usage
        text    = (resp.choices[0].message.content or "").strip()
        tps     = usage.completion_tokens / elapsed if elapsed > 0 else 0

        print(f"  {GREEN}✓ OK{RESET}  {elapsed:.2f}s  "
              f"prompt={usage.prompt_tokens} compl={usage.completion_tokens} "
              f"({tps:.1f} tok/s)")
        print(f"  {YELLOW}{text[:200]}{RESET}")
        ok = True
    except Exception as e:
        print(f"  {RED}✗ FAIL{RESET}  {e}")

    # Streaming (optional) ─────────────────────────────────────────────────────
    if stream:
        print(f"  [stream] ", end="", flush=True)
        try:
            t0 = time.perf_counter()
            chunks = client.chat.completions.create(
                model=model_name,
                messages=STREAM_PROMPT,
                max_tokens=40,
                temperature=0,
                stream=True,
                timeout=timeout,
            )
            n = 0
            for chunk in chunks:
                delta = chunk.choices[0].delta.content or ""
                if delta:
                    print(delta, end="", flush=True)
                    n += 1
            elapsed = time.perf_counter() - t0
            print(f"\n  {GREEN}✓ stream OK{RESET}  {elapsed:.2f}s  ~{n} chunks")
        except Exception as e:
            print(f"\n  {RED}✗ stream FAIL{RESET}  {e}")

    return ok


# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    args = parse_args()

    hr("DGX Spark LLM Stack — Remote Test")
    mode = "direct (per-port vLLM)" if args.direct else f"proxy (LiteLLM :{args.port})"
    print(f"  Host   : {args.host}")
    print(f"  Mode   : {mode}")
    print(f"  Models : {', '.join(args.models)}")
    print(f"  Stream : {args.stream}")

    results = {}

    if args.direct:
        # ── Direct mode — separate client per model ────────────────────────────
        hr("Direct vLLM Tests")
        for key in args.models:
            cfg    = MODELS[key]
            port   = cfg["direct_port"]
            name   = cfg["direct_name"]
            client = direct_client(args.host, port, args.vllm_key)
            results[key] = run_test(client, name, cfg, args.timeout, args.stream)

    else:
        # ── Proxy mode — single LiteLLM client ────────────────────────────────
        client = proxy_client(args.host, args.port, args.key, args.tls)

        hr("Proxy Health")
        check_health(str(client.base_url))
        list_models(client)

        hr("Model Tests")
        for key in args.models:
            cfg  = MODELS[key]
            name = cfg["proxy_name"]
            results[key] = run_test(client, name, cfg, args.timeout, args.stream)

    # ── Summary ───────────────────────────────────────────────────────────────
    hr("Summary")
    all_ok = True
    for key, ok in results.items():
        status = f"{GREEN}PASS{RESET}" if ok else f"{RED}FAIL{RESET}"
        print(f"  {key:<10} {status}")
        if not ok:
            all_ok = False
    print()
    sys.exit(0 if all_ok else 1)


if __name__ == "__main__":
    main()
