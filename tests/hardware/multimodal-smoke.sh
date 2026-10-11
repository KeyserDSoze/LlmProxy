#!/usr/bin/env bash
# Public Gateway acceptance probes (not a substitute for visual/audio review).
# Usage: LLMPROXY_GATEWAY_URL=https://... LLMPROXY_API_KEY=... \
#        LLMPROXY_MODEL_ALIAS=... bash tests/hardware/multimodal-smoke.sh images
# Modes: images, tts, stt, systemone. For stt set LLMPROXY_AUDIO_FILE.
set -euo pipefail
mode="${1:?Usage: bash tests/hardware/multimodal-smoke.sh images|tts|stt|systemone}"
: "${LLMPROXY_GATEWAY_URL:?Set LLMPROXY_GATEWAY_URL}"
: "${LLMPROXY_API_KEY:?Set LLMPROXY_API_KEY}"
: "${LLMPROXY_MODEL_ALIAS:?Set LLMPROXY_MODEL_ALIAS}"
gateway="${LLMPROXY_GATEWAY_URL%/}"
tmp="$(mktemp -d)"
chmod 0700 "$tmp"
trap 'rm -rf "$tmp"' EXIT
printf 'Authorization: Bearer %s\n' "$LLMPROXY_API_KEY" > "$tmp/auth"
chmod 0600 "$tmp/auth"
export LLMPROXY_MODEL_ALIAS

case "$mode" in
  images)
    python3 - "$tmp/request.json" <<'PY'
import json, os, sys
with open(sys.argv[1], "w", encoding="utf-8") as f:
    json.dump({"model": os.environ["LLMPROXY_MODEL_ALIAS"],
               "prompt": "A small blue square centered on a white background",
               "size": "512x512", "n": 1, "steps": 1,
               "response_format": "b64_json"}, f)
PY
    curl -fsS --max-time 600 "$gateway/v1/images/generations" \
      -H @"$tmp/auth" -H 'Content-Type: application/json' \
      --data-binary @"$tmp/request.json" > "$tmp/response.json"
    python3 - "$tmp/response.json" <<'PY'
import base64, json, struct, sys
with open(sys.argv[1], encoding="utf-8") as f: obj = json.load(f)
assert len(obj["data"]) == 1
png = base64.b64decode(obj["data"][0]["b64_json"], validate=True)
assert png[:8] == b"\x89PNG\r\n\x1a\n", "Invalid PNG signature"
w, h = struct.unpack(">II", png[16:24])
assert (w, h) == (512, 512), f"Wrong image dimensions: {(w,h)}"
print(f"Image API acceptance OK: PNG {w}x{h}, {len(png)} bytes")
PY
    ;;
  tts)
    python3 - "$tmp/request.json" <<'PY'
import json, os, sys
with open(sys.argv[1], "w", encoding="utf-8") as f:
    json.dump({"model": os.environ["LLMPROXY_MODEL_ALIAS"],
               "input": "Hello, this is the LlmProxy text to speech acceptance test.",
               "voice": "af_heart", "response_format": "wav"}, f)
PY
    curl -fsS --max-time 600 "$gateway/v1/audio/speech" \
      -H @"$tmp/auth" -H 'Content-Type: application/json' \
      --data-binary @"$tmp/request.json" > "$tmp/generated.wav"
    python3 - "$tmp/generated.wav" <<'PY'
import sys, wave
with wave.open(sys.argv[1], "rb") as f:
    frames, rate = f.getnframes(), f.getframerate()
    assert frames > rate // 2, "Speech audio shorter than half a second"
    print(f"Speech API acceptance OK: WAV {frames / rate:.2f}s, {rate} Hz")
PY
    ;;
  stt)
    : "${LLMPROXY_AUDIO_FILE:?Set LLMPROXY_AUDIO_FILE to a real speech WAV/MP3}"
    test -s "$LLMPROXY_AUDIO_FILE"
    curl -fsS --max-time 600 "$gateway/v1/audio/transcriptions" \
      -H @"$tmp/auth" -F "model=$LLMPROXY_MODEL_ALIAS" \
      -F "file=@$LLMPROXY_AUDIO_FILE" -F "response_format=json" > "$tmp/response.json"
    python3 - "$tmp/response.json" <<'PY'
import json, sys
with open(sys.argv[1], encoding="utf-8") as f: obj = json.load(f)
assert isinstance(obj.get("text"), str) and obj["text"].strip(), "Empty transcript"
print("Speech-to-text API acceptance OK: non-empty transcript returned")
PY
    ;;
  systemone)
    python3 - "$tmp/request.json" <<'PY'
import json, os, sys
with open(sys.argv[1], "w", encoding="utf-8") as f:
    json.dump({"model": os.environ["LLMPROXY_MODEL_ALIAS"],
               "state": "I was charged twice for the same purchase.",
               "questions": {"team": {"type":"choice",
                   "instructions":"Which support team should respond?",
                   "criteria":{"billing":"billing and refunds",
                               "general":"general product questions"}}}}, f)
PY
    curl -fsS --max-time 600 "$gateway/v1/systemone" -H @"$tmp/auth" \
      -H 'Content-Type: application/json' \
      --data-binary @"$tmp/request.json" > "$tmp/response.json"
    python3 - "$tmp/response.json" <<'PY'
import json, sys
with open(sys.argv[1], encoding="utf-8") as f: obj = json.load(f)
answer = obj["answers"]["team"]
probs = answer["probabilities"]
assert answer["choice"] in probs and abs(sum(probs.values())-1) < 0.02
print("System One API acceptance OK: typed choice and probabilities")
PY
    ;;
  *) echo "Unsupported mode: $mode" >&2; exit 2 ;;
esac
