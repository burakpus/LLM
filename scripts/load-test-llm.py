#!/usr/bin/env python3
"""Concurrent load test for /api/llm/completions (streaming SSE).

Usage: python3 load-test-llm.py <token> <n_concurrent> [model] [prompt]

Reports per-request: TTFT, total time, tokens produced, tok/s
Aggregate: p50/p95 TTFT, p50/p95 tok/s, total wall, total throughput
"""
from __future__ import annotations
import asyncio, json, statistics, sys, time
from urllib.request import Request, urlopen

API = "http://localhost:5080"


async def one_request(idx: int, token: str, model: str, prompt: str) -> dict:
    """Run a single streaming completion, capture timing."""
    payload = json.dumps({
        "messages": [{"role": "user", "content": prompt}],
        "model": model,
        "stream": True,
        "temperature": 0.3,
        "maxTokens": 150,
    }).encode()

    proc = await asyncio.create_subprocess_exec(
        "curl", "-sN", "-X", "POST",
        f"{API}/api/llm/completions",
        "-H", "Content-Type: application/json",
        "-H", f"Authorization: Bearer {token}",
        "-d", payload.decode(),
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.DEVNULL,
    )

    t_start = time.monotonic()
    t_first = None
    tokens = 0
    text_chars = 0
    assert proc.stdout
    async for raw in proc.stdout:
        line = raw.decode(errors="ignore").strip()
        if not line or not line.startswith("data:"):
            continue
        data = line[5:].strip()
        if data == "[DONE]":
            break
        try:
            ev = json.loads(data)
        except Exception:
            continue
        # OpenAI-compatible chunks
        chunk = (ev.get("choices") or [{}])[0].get("delta", {}).get("content") or ""
        if chunk:
            if t_first is None:
                t_first = time.monotonic()
            tokens += 1  # 1 chunk ~ 1 token approx
            text_chars += len(chunk)

    await proc.wait()
    t_end = time.monotonic()

    total = t_end - t_start
    ttft  = (t_first - t_start) if t_first else None
    gen_time = (t_end - t_first) if t_first else None
    toks_per_sec = (tokens / gen_time) if (gen_time and tokens > 0) else 0

    return dict(idx=idx, ok=tokens > 0, total=total, ttft=ttft, tokens=tokens,
                tps=toks_per_sec, chars=text_chars)


async def main():
    if len(sys.argv) < 3:
        print("usage: load-test-llm.py <token> <n_concurrent> [model] [prompt]")
        sys.exit(1)
    token  = sys.argv[1]
    n      = int(sys.argv[2])
    model  = sys.argv[3] if len(sys.argv) > 3 else "chat"
    prompt = sys.argv[4] if len(sys.argv) > 4 else "5 cümle ile yapay zeka nedir?"

    print(f"=== Load test: N={n} model={model} ===")
    t0 = time.monotonic()
    results = await asyncio.gather(*[
        one_request(i, token, model, prompt) for i in range(n)
    ])
    wall = time.monotonic() - t0

    oks  = [r for r in results if r["ok"]]
    fail = len(results) - len(oks)
    if not oks:
        print(f"All {n} failed")
        sys.exit(2)

    ttfts = sorted([r["ttft"] for r in oks if r["ttft"] is not None])
    tpss  = sorted([r["tps"]  for r in oks if r["tps"] > 0])
    totals = sorted([r["total"] for r in oks])
    tok_total = sum(r["tokens"] for r in oks)

    def p(arr, q): return arr[max(0, int(len(arr) * q) - 1)] if arr else 0

    print(f"  Success:        {len(oks)}/{n}  (fail={fail})")
    print(f"  Wall:           {wall:.2f}s  →  agg {tok_total / wall:.1f} tok/s overall")
    print(f"  TTFT  p50/p95:  {p(ttfts, 0.5):.2f}s / {p(ttfts, 0.95):.2f}s")
    print(f"  tok/s p50/p95:  {p(tpss, 0.5):.1f}   / {p(tpss, 0.95):.1f}   (per-stream)")
    print(f"  total p50/p95:  {p(totals, 0.5):.2f}s / {p(totals, 0.95):.2f}s")
    print(f"  Tokens total:   {tok_total}  (avg {tok_total/len(oks):.0f}/req)")


if __name__ == "__main__":
    asyncio.run(main())
