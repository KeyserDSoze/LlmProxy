#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE=(docker compose -f docker/docker-compose.yml)
MOCK_PID=""

cleanup() {
  "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
  if [[ -n "$MOCK_PID" ]]; then
    kill "$MOCK_PID" >/dev/null 2>&1 || true
    wait "$MOCK_PID" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

fail_with_diagnostics() {
  echo "$1" >&2
  "${COMPOSE[@]}" ps -a >&2 || true
  "${COMPOSE[@]}" logs --no-color >&2 || true
  [[ -f /tmp/llmproxy-capacity-mock.log ]] && cat /tmp/llmproxy-capacity-mock.log >&2 || true
  exit 1
}

wait_ready() {
  for attempt in {1..30}; do
    if curl --fail --silent http://127.0.0.1:8080/readyz >/dev/null; then
      return 0
    fi
    sleep 2
  done
  fail_with_diagnostics "Gateway did not become ready for capacity smoke test."
}

python3 tests/backend/integration/mock_llm.py --port 3460 --prefix /capacity --name capacity > /tmp/llmproxy-capacity-mock.log 2>&1 &
MOCK_PID="$!"
sleep 1

export LLM_PROXY_API_KEY="dev-change-me"
export LLM_PROXY_API_KEY_PEPPER="capacity-test-pepper"
export ENTRA_ENABLED="false"
export BOOTSTRAP_ENABLED="true"
export INFERENCE_NODE_NAME="inference-capacity"
export INFERENCE_NODE_BASE_ADDRESS="http://host.docker.internal:3460/capacity"
export INFERENCE_NODE_WEIGHT="1"
export INFERENCE_NODE_MAX_CONCURRENCY="1"
export ROUTING_STRATEGY="WeightedLeastLoaded"
export HEALTH_INTERVAL_SECONDS="1"
export HEALTH_HEALTHY_AFTER_SUCCESSES="1"
export HEALTH_UNHEALTHY_AFTER_FAILURES="2"
export RUNTIME_METRICS_ENABLED="false"
export HARDWARE_METRICS_ENABLED="false"

if ! "${COMPOSE[@]}" up -d --build; then
  fail_with_diagnostics "Docker Compose stack failed to start for capacity smoke test."
fi
wait_ready

deployment_id="$(curl --fail --silent http://127.0.0.1:8080/api/admin/deployments | jq -r '.[0].id')"
if [[ -z "$deployment_id" || "$deployment_id" == "null" ]]; then
  fail_with_diagnostics "Bootstrap deployment was not created."
fi

initial_capacity="$(curl --fail --silent http://127.0.0.1:8080/api/admin/capacity)"
echo "$initial_capacity" | jq -e '.nodes | length == 1 and .[0].maxConcurrency == 1 and .[0].activeRequests == 0 and .[0].remaining == 1' >/dev/null
echo "$initial_capacity" | jq -e '.deployments | length == 1 and .[0].effectiveMaxConcurrency == 1 and .[0].recommendedMaxConcurrency == null' >/dev/null

profile_payload='{"recommendedMaxConcurrency":1,"p95TtftMilliseconds":420,"p95DurationMilliseconds":4800,"sustainableOutputTokensPerSecond":92,"benchmarkSource":"benchmark-results/capacity-smoke.json","measuredAtUtc":"2026-09-13T07:00:00Z"}'
profile_response="$(curl --fail --silent -X PUT -H 'Content-Type: application/json' -d "$profile_payload" "http://127.0.0.1:8080/api/admin/deployments/${deployment_id}/capacity-profile")"
echo "$profile_response" | jq -e '.recommendedMaxConcurrency == 1 and .maxConcurrency == null and .benchmarkP95TtftMilliseconds == 420 and .sustainableOutputTokensPerSecond == 92' >/dev/null

# Saving benchmark evidence must not change the active deployment limit. Applying is a separate audited action.
curl --fail --silent -X POST "http://127.0.0.1:8080/api/admin/deployments/${deployment_id}/capacity-profile/apply" >/dev/null
applied_capacity="$(curl --fail --silent http://127.0.0.1:8080/api/admin/capacity)"
echo "$applied_capacity" | jq -e '.deployments[0].maxConcurrency == 1 and .deployments[0].recommendedMaxConcurrency == 1' >/dev/null

# A recommendation can be stored as evidence even when it is above the physical node ceiling,
# but explicit apply must refuse to overcommit the inference node.
curl --fail --silent -X PUT -H 'Content-Type: application/json' -d '{"recommendedMaxConcurrency":2,"p95TtftMilliseconds":300,"p95DurationMilliseconds":4000,"sustainableOutputTokensPerSecond":100,"benchmarkSource":"benchmark-results/too-high.json","measuredAtUtc":"2026-09-13T07:05:00Z"}' "http://127.0.0.1:8080/api/admin/deployments/${deployment_id}/capacity-profile" >/dev/null
apply_too_high_status="$(curl --silent --output /tmp/capacity-apply-too-high.json --write-out '%{http_code}' -X POST "http://127.0.0.1:8080/api/admin/deployments/${deployment_id}/capacity-profile/apply")"
if [[ "$apply_too_high_status" != "400" ]]; then
  fail_with_diagnostics "Expected over-limit capacity apply to return 400, got ${apply_too_high_status}."
fi
grep --quiet 'exceeds node' /tmp/capacity-apply-too-high.json

# Restore the valid profile and verify it survives a process restart.
curl --fail --silent -X PUT -H 'Content-Type: application/json' -d "$profile_payload" "http://127.0.0.1:8080/api/admin/deployments/${deployment_id}/capacity-profile" >/dev/null
"${COMPOSE[@]}" restart llmproxy >/dev/null
wait_ready
persisted_capacity="$(curl --fail --silent http://127.0.0.1:8080/api/admin/capacity)"
echo "$persisted_capacity" | jq -e '.deployments[0].recommendedMaxConcurrency == 1 and .deployments[0].benchmarkSource == "benchmark-results/capacity-smoke.json" and .deployments[0].benchmarkP95TtftMilliseconds == 420' >/dev/null

# Hold the only physical node slot with a real SSE request. A concurrent request must be rejected
# locally by LlmProxy instead of being forwarded to the already-saturated inference node.
curl --silent --no-buffer \
  -H 'Authorization: Bearer dev-change-me' \
  -H 'Content-Type: application/json' \
  -d '{"model":"agic-code-fast","stream":true,"messages":[{"role":"user","content":"hold capacity"}]}' \
  http://127.0.0.1:8080/v1/chat/completions > /tmp/capacity-first-stream.txt &
first_pid="$!"
sleep 0.15

second_status="$(curl --silent --dump-header /tmp/capacity-second.headers --output /tmp/capacity-second.json --write-out '%{http_code}' \
  -H 'Authorization: Bearer dev-change-me' \
  -H 'Content-Type: application/json' \
  -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"should backpressure"}]}' \
  http://127.0.0.1:8080/v1/chat/completions)"
if [[ "$second_status" != "429" ]]; then
  wait "$first_pid" || true
  fail_with_diagnostics "Expected saturated request to return 429, got ${second_status}."
fi
jq -e '.error.type == "rate_limit_error" and .error.code == "capacity_exhausted"' /tmp/capacity-second.json >/dev/null
grep -i --quiet '^Retry-After: 1' /tmp/capacity-second.headers
wait "$first_pid"
grep --quiet 'data: \[DONE\]' /tmp/capacity-first-stream.txt

# Once the lease is released, traffic must be accepted again.
recovered_status="$(curl --silent --output /tmp/capacity-recovered.json --write-out '%{http_code}' \
  -H 'Authorization: Bearer dev-change-me' \
  -H 'Content-Type: application/json' \
  -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"capacity recovered"}]}' \
  http://127.0.0.1:8080/v1/chat/completions)"
if [[ "$recovered_status" != "200" ]]; then
  fail_with_diagnostics "Expected request after lease release to return 200, got ${recovered_status}."
fi

audit_json="$(curl --fail --silent 'http://127.0.0.1:8080/api/admin/audit?take=100')"
echo "$audit_json" | jq -e 'map(.action) | index("deployment.capacity_profile.update") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.action) | index("deployment.capacity_profile.apply") != null' >/dev/null

echo "Capacity smoke suite passed: persisted benchmark profile, guarded explicit apply, atomic node-wide lease and HTTP 429 backpressure verified."
