import urllib.request
import urllib.error
import time
import json
import sys

print("Waiting for vllm-qwen3 to be healthy...", end="", flush=True)
for _ in range(120): # 120 * 5s = 600s
    try:
        req = urllib.request.urlopen("http://localhost:8002/health")
        if req.getcode() == 200:
            print(" Healthy!")
            break
    except Exception:
        print(".", end="", flush=True)
        time.sleep(5)
else:
    print(" Timeout.")
    sys.exit(1)

print("Testing chat completion...")
data = {
    "model": "qwen3.6-27b",
    "messages": [
        {"role": "system", "content": "You are a helpful assistant."},
        {"role": "user", "content": "Write a 3 sentence poem about a spaceship."}
    ],
    "max_tokens": 50
}
req = urllib.request.Request(
    "http://localhost:8002/v1/chat/completions",
    data=json.dumps(data).encode("utf-8"),
    headers={"Content-Type": "application/json"}
)
try:
    response = urllib.request.urlopen(req)
    res_data = json.loads(response.read().decode("utf-8"))
    print("\nResponse:")
    print(res_data["choices"][0]["message"]["content"])
except Exception as e:
    print("Error:", e)
    if hasattr(e, "read"):
        print(e.read().decode("utf-8"))
