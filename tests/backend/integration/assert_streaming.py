#!/usr/bin/env python3
import json
import sys
import time
import urllib.request


url = sys.argv[1]
api_key = sys.argv[2]
payload = json.dumps({
    "model": "agic-code-fast",
    "messages": [{"role": "user", "content": "stream please"}],
    "stream": True,
}).encode("utf-8")
request = urllib.request.Request(
    url,
    data=payload,
    method="POST",
    headers={
        "Authorization": f"Bearer {api_key}",
        "Content-Type": "application/json",
    },
)

started = time.monotonic()
first_data_at = None
done_at = None
with urllib.request.urlopen(request, timeout=10) as response:
    content_type = response.headers.get("Content-Type", "")
    if "text/event-stream" not in content_type:
        raise SystemExit(f"Expected text/event-stream, got {content_type!r}")

    while True:
        line = response.readline()
        if not line:
            break
        decoded = line.decode("utf-8").strip()
        if decoded.startswith("data:") and first_data_at is None:
            first_data_at = time.monotonic() - started
        if decoded == "data: [DONE]":
            done_at = time.monotonic() - started
            break

if first_data_at is None or done_at is None:
    raise SystemExit("Did not receive a complete SSE stream.")

# The mock sends its first event immediately, then intentionally takes about one second
# to complete. If the gateway buffered the full upstream body, first_data_at would be
# close to done_at instead of arriving early.
if first_data_at >= 0.7:
    raise SystemExit(f"First SSE event arrived too late ({first_data_at:.3f}s); response may be buffered.")
if done_at < 0.8:
    raise SystemExit(f"Mock stream completed unexpectedly quickly ({done_at:.3f}s).")

print(f"Streaming verified: first event {first_data_at:.3f}s, complete {done_at:.3f}s")
