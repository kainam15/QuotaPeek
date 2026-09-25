"""Read Codex quota via the official app-server; never reads or prints tokens."""
import argparse
import json
import queue
import subprocess
import threading
import time


def probe(executable):
    process = subprocess.Popen(
        [executable, "app-server", "--listen", "stdio://"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        text=True, encoding="utf-8", creationflags=subprocess.CREATE_NO_WINDOW,
    )
    lines = queue.Queue()

    def receive():
        for line in process.stdout:
            lines.put(line)
        lines.put(None)

    threading.Thread(target=receive, daemon=True).start()

    def send(payload):
        process.stdin.write(json.dumps(payload) + "\n")
        process.stdin.flush()

    def response(request_id):
        deadline = time.monotonic() + 40
        while time.monotonic() < deadline:
            line = lines.get(timeout=max(0.1, deadline-time.monotonic()))
            if line is None:
                raise RuntimeError("Codex app-server exited before responding")
            obj = json.loads(line)
            if obj.get("id") == request_id and "method" not in obj:
                if "error" in obj:
                    error = obj["error"]
                    # Protocol errors contain no request headers; still redact URLs/tokens.
                    message = str(error.get("message", "error"))
                    if any(s in message.lower() for s in ("token", "bearer", "http")):
                        message = "Quota request failed; check Codex login/network."
                    raise RuntimeError(f"RPC {error.get('code')}: {message[:240]}")
                return obj["result"]
        raise TimeoutError("Codex quota timed out")

    try:
        send({"id": 1, "method": "initialize", "params": {
            "clientInfo": {"name": "quotapeek_probe", "version": "0.1.0"}}})
        response(1)
        send({"method": "initialized", "params": {}})
        send({"id": 2, "method": "account/rateLimits/read"})
        result = response(2)
        buckets = result.get("rateLimitsByLimitId") or {"codex": result.get("rateLimits")}
        return {"buckets": {
            name: {key: value.get(key) for key in ("limitName", "planType", "primary", "secondary")}
            for name, value in buckets.items() if value
        }, "availableResets": (result.get("rateLimitResetCredits") or {}).get("availableCount")}
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            process.kill()  # Only this probe's child process.
            process.wait(timeout=3)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True, help="Absolute path to codex.exe")
    args = parser.parse_args()
    try:
        print(json.dumps(probe(args.exe), ensure_ascii=False, indent=2))
    except Exception as error:
        print(str(error))
        raise SystemExit(1)
