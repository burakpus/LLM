import urllib.request
import urllib.error
import time
import json
import sys

def check_health(url, name):
    print(f"Checking health for {name} on {url}...")
    for i in range(120): # 120 * 5 = 600s (10 min)
        try:
            req = urllib.request.urlopen(f"{url}/health")
            if req.getcode() == 200:
                print(f"  [OK] {name} is healthy!")
                return True
        except Exception:
            pass
        if i % 6 == 0:
            print(f"  [WAITING] {name} is starting up...")
        time.sleep(5)
    print(f"  [TIMEOUT] {name} failed to become healthy.")
    return False

def test_model(url, model_name):
    print(f"Querying {model_name}...")
    data = {
        "model": model_name,
        "messages": [
            {"role": "system", "content": "You are a helpful assistant."},
            {"role": "user", "content": "Write a 3 sentence poem about a spaceship."}
        ],
        "max_tokens": 50
    }
    req = urllib.request.Request(
        f"{url}/v1/chat/completions",
        data=json.dumps(data).encode("utf-8"),
        headers={
            "Content-Type": "application/json",
            "Authorization": "Bearer 9cbef01e3edede04363783bbe87ed51fc178aa0a3586697cde4aa915ebcdaecf"
        }
    )
    try:
        response = urllib.request.urlopen(req)
        res_data = json.loads(response.read().decode("utf-8"))
        print(f"\nResponse from {model_name}:")
        print(res_data["choices"][0]["message"]["content"])
        print("-" * 50)
        return True
    except Exception as e:
        print(f"Error querying {model_name}: {e}")
        if hasattr(e, "read"):
            try:
                print(e.read().decode("utf-8"))
            except Exception:
                pass
        return False

# We check both endpoints
qwen_healthy = check_health("http://localhost:8002", "vllm-qwen3")
gemma_healthy = check_health("http://localhost:8000", "vllm-gemma")

if qwen_healthy:
    test_model("http://localhost:8002", "qwen3.6-27b")

if gemma_healthy:
    test_model("http://localhost:8000", "gemma4-26b")
