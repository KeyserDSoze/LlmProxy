#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

PORT=3499
TMP_DIR="$(mktemp -d)"
MOCK_PID=""
cleanup() {
  if [[ -n "$MOCK_PID" ]]; then
    kill "$MOCK_PID" >/dev/null 2>&1 || true
    wait "$MOCK_PID" >/dev/null 2>&1 || true
  fi
  rm -rf "$TMP_DIR"
}
trap cleanup EXIT

# vLLM's canonical /health success response is bodyless. Exercise that exact
# contract so the acceptance harness cannot accidentally require JSON there.
python3 tests/backend/integration/mock_llm.py --port "$PORT" --name acceptance --empty-health >"$TMP_DIR/mock.log" 2>&1 &
MOCK_PID=$!

for attempt in {1..30}; do
  if curl --fail --silent "http://127.0.0.1:${PORT}/health" >/dev/null; then
    break
  fi
  if [[ "$attempt" -eq 30 ]]; then
    cat "$TMP_DIR/mock.log" >&2 || true
    echo "Acceptance mock did not become ready." >&2
    exit 1
  fi
  sleep 0.2
done

SECRET='ci-acceptance-secret-never-persist'
cat > "$TMP_DIR/acceptance.env" <<EOF
LLM_PROXY_API_KEY=$SECRET
LLMPROXY_PORT=$PORT
INFERENCE_NODE_BASE_ADDRESS=http://127.0.0.1:$PORT
PUBLIC_MODEL_NAME=bootstrap-model
PROVIDER_MODEL_NAME=bootstrap-model
EOF
chmod 600 "$TMP_DIR/acceptance.env"

LLMPROXY_ACCEPTANCE_API_KEY="$SECRET" \
  bash docker/scripts/environment-acceptance.sh \
    --env-file "$TMP_DIR/acceptance.env" \
    --gateway-url "http://127.0.0.1:$PORT" \
    --node-url "http://127.0.0.1:$PORT" \
    --evidence-dir "$TMP_DIR/evidence" \
    --skip-docker

test -s "$TMP_DIR/evidence/summary.md"
test -s "$TMP_DIR/evidence/checks.tsv"
grep -Fq -- '- Result: PASS' "$TMP_DIR/evidence/summary.md"
grep -Fq $'PASS\tinference-health' "$TMP_DIR/evidence/checks.tsv"
grep -Fq $'PASS\tinference-chat-stream' "$TMP_DIR/evidence/checks.tsv"
grep -Fq $'PASS\tinference-responses-stream' "$TMP_DIR/evidence/checks.tsv"
grep -Fq $'PASS\tgateway-chat-stream' "$TMP_DIR/evidence/checks.tsv"
grep -Fq $'PASS\tgateway-responses-stream' "$TMP_DIR/evidence/checks.tsv"
if grep -R -Fq "$SECRET" "$TMP_DIR/evidence"; then
  echo "Acceptance evidence leaked the API secret." >&2
  exit 1
fi
if grep -R -Fq 'Reply with exactly OK.' "$TMP_DIR/evidence"; then
  echo "Acceptance evidence persisted the synthetic prompt." >&2
  exit 1
fi

echo "Environment acceptance smoke passed."
