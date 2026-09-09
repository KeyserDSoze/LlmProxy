#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE=(docker compose -f docker/docker-compose.yml)
MOCK_PIDS=()

cleanup() {
  "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
  for pid in "${MOCK_PIDS[@]:-}"; do
    kill "$pid" >/dev/null 2>&1 || true
    wait "$pid" >/dev/null 2>&1 || true
  done
}
trap cleanup EXIT

fail_with_diagnostics() {
  local message="$1"
  echo "$message" >&2
  "${COMPOSE[@]}" ps -a >&2 || true
  "${COMPOSE[@]}" logs --no-color >&2 || true
  exit 1
}

start_mock() {
  local port="$1"
  local prefix="$2"
  local name="$3"
  python3 tests/backend/integration/mock_llm.py --port "$port" --prefix "$prefix" --name "$name" >"/tmp/llmproxy-mock-${name}.log" 2>&1 &
  MOCK_PIDS+=("$!")
}

start_mock 3450 /primopath primary
start_mock 3451 /altropath alternate
sleep 1

export LLM_PROXY_API_KEY="dev-change-me"
export LLM_PROXY_API_KEY_PEPPER="ci-test-pepper"
export ENTRA_ENABLED="false"
export BOOTSTRAP_ENABLED="true"
export DGX_NODE_NAME="dgx-local-primary"
export DGX_NODE_BASE_ADDRESS="http://host.docker.internal:3450/primopath"
export DGX_NODE_WEIGHT="1"
export DGX_NODE_MAX_CONCURRENCY="4"
export ROUTING_STRATEGY="WeightedRoundRobin"

if ! "${COMPOSE[@]}" up -d --build; then
  fail_with_diagnostics "Docker Compose stack failed to start."
fi

ready=false
for attempt in {1..30}; do
  if curl --fail --silent http://127.0.0.1:8080/readyz >/dev/null; then
    ready=true
    break
  fi
  sleep 2
done

if [[ "$ready" != "true" ]]; then
  fail_with_diagnostics "Gateway did not become ready."
fi

curl --fail --silent http://127.0.0.1:8080/healthz | grep --quiet '"status":"ok"'
curl --fail --silent http://127.0.0.1:8080/healthz | grep --quiet 'WeightedRoundRobin'

unauthorized_models_status="$(curl --silent --output /dev/null --write-out '%{http_code}' http://127.0.0.1:8080/v1/models)"
if [[ "$unauthorized_models_status" != "401" ]]; then
  fail_with_diagnostics "Expected /v1/models without bearer token to return 401, got ${unauthorized_models_status}."
fi

unauthorized_responses_status="$(curl --silent --output /dev/null --write-out '%{http_code}' -X POST -H 'Content-Type: application/json' -d '{"model":"agic-code-fast","input":"hello"}' http://127.0.0.1:8080/v1/responses)"
if [[ "$unauthorized_responses_status" != "401" ]]; then
  fail_with_diagnostics "Expected /v1/responses without bearer token to return 401, got ${unauthorized_responses_status}."
fi

curl --fail --silent \
  -H 'Authorization: Bearer dev-change-me' \
  http://127.0.0.1:8080/v1/models \
  | grep --quiet 'agic-code-fast'

# A path-prefixed local runtime must resolve to /primopath/v1/chat/completions.
primary_response="$(curl --fail --silent \
  -H 'Authorization: Bearer dev-change-me' \
  -H 'Content-Type: application/json' \
  -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"hello"}]}' \
  http://127.0.0.1:8080/v1/chat/completions)"
echo "$primary_response" | grep --quiet '"served_by":"primary"'
echo "$primary_response" | grep --quiet '"model":"bootstrap-model"'

model_id="$(curl --fail --silent http://127.0.0.1:8080/api/admin/models | jq -r '.[0].id')"
node2_json="$(curl --fail --silent \
  -H 'Content-Type: application/json' \
  -d '{"name":"dgx-local-alternate","baseAddress":"http://host.docker.internal:3451/altropath","weight":3,"maxConcurrency":4}' \
  http://127.0.0.1:8080/api/admin/nodes)"
node2_id="$(echo "$node2_json" | jq -r '.id')"

curl --fail --silent \
  -H 'Content-Type: application/json' \
  -d "{\"nodeId\":\"${node2_id}\",\"modelId\":\"${model_id}\",\"weight\":1,\"maxConcurrency\":4}" \
  http://127.0.0.1:8080/api/admin/deployments >/dev/null

healthy=false
for attempt in {1..20}; do
  status="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes | jq -r --arg id "$node2_id" '.[] | select(.id == $id) | .status')"
  if [[ "$status" == "Healthy" ]]; then
    healthy=true
    break
  fi
  sleep 1
done
if [[ "$healthy" != "true" ]]; then
  fail_with_diagnostics "Path-prefixed alternate mock runtime did not become Healthy."
fi

# Verify weighted round robin over two complete service-root URLs. Node weights are
# multiplied by deployment weights, so alternate weight 3 vs primary weight 1 should
# produce a 6/2 split across eight sequential requests regardless of starting slot.
primary_count=0
alternate_count=0
for attempt in {1..8}; do
  response="$(curl --fail --silent \
    -H 'Authorization: Bearer dev-change-me' \
    -H 'Content-Type: application/json' \
    -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"weighted route"}]}' \
    http://127.0.0.1:8080/v1/chat/completions)"
  if echo "$response" | grep --quiet '"served_by":"primary"'; then
    primary_count=$((primary_count + 1))
  elif echo "$response" | grep --quiet '"served_by":"alternate"'; then
    alternate_count=$((alternate_count + 1))
  else
    fail_with_diagnostics "Weighted routing returned an unknown backend response: $response"
  fi
done

if [[ "$primary_count" -ne 2 || "$alternate_count" -ne 6 ]]; then
  fail_with_diagnostics "Expected weighted split primary=2 alternate=6, got primary=${primary_count} alternate=${alternate_count}."
fi

# Responses API uses the same endpoint-composition and routing pipeline.
curl --fail --silent \
  -H 'Authorization: Bearer dev-change-me' \
  -H 'Content-Type: application/json' \
  -d '{"model":"agic-code-fast","input":"hello responses"}' \
  http://127.0.0.1:8080/v1/responses \
  | grep --quiet '"object":"response"'

# Verify actual SSE behavior instead of merely checking the Content-Type header.
python3 tests/backend/integration/assert_streaming.py \
  http://127.0.0.1:8080/v1/chat/completions \
  dev-change-me

curl --fail --silent http://127.0.0.1:8080/api/admin/overview | grep --quiet 'activeRequests'

echo "Backend integration smoke suite passed. Routing: primary=${primary_count}, alternate=${alternate_count}."
