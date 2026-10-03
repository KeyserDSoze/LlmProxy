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
  local api_key="${4:-}"
  python3 tests/backend/integration/mock_llm.py --port "$port" --prefix "$prefix" --name "$name" --api-key "$api_key" >"/tmp/llmproxy-mock-${name}.log" 2>&1 &
  MOCK_PIDS+=("$!")
}

wait_ready() {
  local ready=false
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
}

wait_node_status() {
  local node_id="$1"
  local expected="$2"
  local attempts="${3:-20}"
  for ((attempt=1; attempt<=attempts; attempt++)); do
    status="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes | jq -r --arg id "$node_id" '.[] | select(.id == $id) | .status')"
    if [[ "$status" == "$expected" ]]; then
      return 0
    fi
    sleep 0.5
  done
  fail_with_diagnostics "Node ${node_id} did not reach ${expected}."
}

start_mock 3450 /primopath primary
start_mock 3451 /altropath alternate
start_mock 3452 /classifier classifier laya-upstream
sleep 1

export LLM_PROXY_API_KEY="dev-change-me"
export LLM_PROXY_API_KEY_PEPPER="ci-test-pepper"
export ENTRA_ENABLED="false"
export BOOTSTRAP_ENABLED="true"
export INFERENCE_NODE_NAME="inference-local-primary"
export INFERENCE_NODE_BASE_ADDRESS="http://host.docker.internal:3450/primopath"
export INFERENCE_NODE_WEIGHT="1"
export INFERENCE_NODE_MAX_CONCURRENCY="4"
export ROUTING_STRATEGY="WeightedRoundRobin"
export HEALTH_INTERVAL_SECONDS="1"
export HEALTH_HEALTHY_AFTER_SUCCESSES="2"
export HEALTH_UNHEALTHY_AFTER_FAILURES="3"
export RUNTIME_METRICS_ENABLED="true"
export RUNTIME_METRICS_INTERVAL_SECONDS="1"
export SYSTEM_ONE_ENABLED="true"
export SYSTEM_ONE_BASE_ADDRESS="http://host.docker.internal:3452/classifier"
export SYSTEM_ONE_API_KEY="laya-upstream"
export SYSTEM_ONE_TIMEOUT_SECONDS="5"

if ! "${COMPOSE[@]}" up -d --build; then
  fail_with_diagnostics "Docker Compose stack failed to start."
fi
wait_ready

curl --fail --silent http://127.0.0.1:8080/healthz | grep --quiet '"status":"ok"'
curl --fail --silent http://127.0.0.1:8080/healthz | grep --quiet 'WeightedRoundRobin'
curl --fail --silent http://127.0.0.1:8080/api/admin/routing | grep --quiet 'WeightedRoundRobin'

default_tuning="$(curl --fail --silent http://127.0.0.1:8080/api/admin/routing/tuning)"
echo "$default_tuning" | jq -e '.warmupSamples == 3 and .ttftTargetMilliseconds == 2000 and .kvCacheThreshold == 0.7 and .queuePenaltyWeight == 0.75' >/dev/null

updated_tuning="$(curl --fail --silent -X PUT -H 'Content-Type: application/json' -d '{"warmupSamples":4,"ttftTargetMilliseconds":1500,"ttftPenaltyWeight":0.3,"failurePenaltyWeight":1.7,"externalLoadPenaltyWeight":0.45,"queuePenaltyWeight":0.8,"kvCacheThreshold":0.75,"kvCachePenaltyWeight":0.65,"degradedNodePenalty":0.4,"unknownNodePenalty":0.12}' http://127.0.0.1:8080/api/admin/routing/tuning)"
echo "$updated_tuning" | jq -e '.warmupSamples == 4 and .ttftTargetMilliseconds == 1500 and .failurePenaltyWeight == 1.7 and .kvCacheThreshold == 0.75' >/dev/null

unauthorized_models_status="$(curl --silent --output /dev/null --write-out '%{http_code}' http://127.0.0.1:8080/v1/models)"
if [[ "$unauthorized_models_status" != "401" ]]; then
  fail_with_diagnostics "Expected /v1/models without bearer token to return 401, got ${unauthorized_models_status}."
fi

unauthorized_responses_status="$(curl --silent --output /dev/null --write-out '%{http_code}' -X POST -H 'Content-Type: application/json' -d '{"model":"agic-code-fast","input":"hello"}' http://127.0.0.1:8080/v1/responses)"
if [[ "$unauthorized_responses_status" != "401" ]]; then
  fail_with_diagnostics "Expected /v1/responses without bearer token to return 401, got ${unauthorized_responses_status}."
fi

unauthorized_systemone_status="$(curl --silent --output /dev/null --write-out '%{http_code}' -X POST -H 'Content-Type: application/json' -d '{"state":{"document":"duplicate charge"},"questions":{"billing":{"type":"noul","instructions":"Is this billing?"}}}' http://127.0.0.1:8080/v1/systemone)"
if [[ "$unauthorized_systemone_status" != "401" ]]; then
  fail_with_diagnostics "Expected /v1/systemone without bearer token to return 401, got ${unauthorized_systemone_status}."
fi

systemone_response="$(curl --fail --silent -H 'Authorization: Bearer dev-change-me' -H 'Content-Type: application/json' -d '{"state":{"document":"duplicate charge"},"questions":{"billing":{"type":"noul","instructions":"Is this billing?"}}}' http://127.0.0.1:8080/v1/systemone)"
echo "$systemone_response" | jq -e '.served_by == "classifier" and .answers.billing.noul == 0.91 and .state.document == "duplicate charge"' >/dev/null

curl --fail --silent -H 'Authorization: Bearer dev-change-me' http://127.0.0.1:8080/v1/models | grep --quiet 'agic-code-fast'

primary_response="$(curl --fail --silent -H 'Authorization: Bearer dev-change-me' -H 'Content-Type: application/json' -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"hello"}]}' http://127.0.0.1:8080/v1/chat/completions)"
echo "$primary_response" | grep --quiet '"served_by":"primary"'
echo "$primary_response" | grep --quiet '"model":"bootstrap-model"'
echo "$primary_response" | jq -e '.usage.total_tokens == 18' >/dev/null

model_id="$(curl --fail --silent http://127.0.0.1:8080/api/admin/models | jq -r '.[0].id')"
node2_json="$(curl --fail --silent -H 'Content-Type: application/json' -d '{"name":"inference-local-alternate","baseAddress":"http://host.docker.internal:3451/altropath","weight":3,"maxConcurrency":4}' http://127.0.0.1:8080/api/admin/nodes)"
node2_id="$(echo "$node2_json" | jq -r '.id')"

curl --fail --silent -H 'Content-Type: application/json' -d "{\"nodeId\":\"${node2_id}\",\"modelId\":\"${model_id}\",\"weight\":1,\"maxConcurrency\":4}" http://127.0.0.1:8080/api/admin/deployments >/dev/null
wait_node_status "$node2_id" Healthy 30

node_health_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes | jq -c --arg id "$node2_id" '.[] | select(.id == $id)')"
echo "$node_health_json" | jq -e '.lastHealthLatencyMilliseconds != null and .consecutiveHealthSuccesses >= 2 and .lastHealthError == null' >/dev/null

runtime_ready=false
for attempt in {1..20}; do
  runtime_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/routing/runtime)"
  if echo "$runtime_json" | jq -e --arg id "$node2_id" 'map(select(.nodeId == $id and .available == true and .runningRequests == 2 and .waitingRequests == 1 and .kvCacheUsageRatio > 0.5 and .modelName == "bootstrap-model")) | length == 1' >/dev/null; then runtime_ready=true; break; fi
  sleep 0.25
done
if [[ "$runtime_ready" != "true" ]]; then fail_with_diagnostics "vLLM runtime metrics were not collected from the path-prefixed alternate node."; fi

connection_test="$(curl --fail --silent -X POST http://127.0.0.1:8080/api/admin/nodes/${node2_id}/test-connection)"
echo "$connection_test" | jq -e '.success == true and .health.statusCode == 200 and .openAi.statusCode == 200' >/dev/null
echo "$connection_test" | grep --quiet 'altropath/v1/chat/completions'
echo "$connection_test" | grep --quiet 'altropath/v1/responses'

primary_count=0
alternate_count=0
for attempt in {1..8}; do
  response="$(curl --fail --silent -H 'Authorization: Bearer dev-change-me' -H 'Content-Type: application/json' -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"weighted route"}]}' http://127.0.0.1:8080/v1/chat/completions)"
  if echo "$response" | grep --quiet '"served_by":"primary"'; then primary_count=$((primary_count + 1));
  elif echo "$response" | grep --quiet '"served_by":"alternate"'; then alternate_count=$((alternate_count + 1));
  else fail_with_diagnostics "Weighted routing returned an unknown backend response: $response"; fi
done
if [[ "$primary_count" -ne 2 || "$alternate_count" -ne 6 ]]; then fail_with_diagnostics "Expected weighted split primary=2 alternate=6, got primary=${primary_count} alternate=${alternate_count}."; fi

performance_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/routing/performance)"
echo "$performance_json" | jq -e 'map(select(.sampleCount > 0)) | length > 0' >/dev/null

curl --fail --silent -X PUT -H 'Content-Type: application/json' -d '{"strategy":"RoundRobin"}' http://127.0.0.1:8080/api/admin/routing | grep --quiet 'RoundRobin'
curl --fail --silent http://127.0.0.1:8080/healthz | grep --quiet 'RoundRobin'

rr_primary=0
rr_alternate=0
for attempt in {1..4}; do
  response="$(curl --fail --silent -H 'Authorization: Bearer dev-change-me' -H 'Content-Type: application/json' -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"round robin route"}]}' http://127.0.0.1:8080/v1/chat/completions)"
  if echo "$response" | grep --quiet '"served_by":"primary"'; then rr_primary=$((rr_primary + 1)); elif echo "$response" | grep --quiet '"served_by":"alternate"'; then rr_alternate=$((rr_alternate + 1)); fi
done
if [[ "$rr_primary" -ne 2 || "$rr_alternate" -ne 2 ]]; then fail_with_diagnostics "Expected live round-robin split 2/2, got primary=${rr_primary} alternate=${rr_alternate}."; fi

"${COMPOSE[@]}" restart llmproxy >/dev/null
wait_ready
curl --fail --silent http://127.0.0.1:8080/api/admin/routing | grep --quiet 'RoundRobin'
curl --fail --silent http://127.0.0.1:8080/healthz | grep --quiet 'RoundRobin'
persisted_tuning="$(curl --fail --silent http://127.0.0.1:8080/api/admin/routing/tuning)"
echo "$persisted_tuning" | jq -e '.warmupSamples == 4 and .ttftTargetMilliseconds == 1500 and .queuePenaltyWeight == 0.8 and .kvCacheThreshold == 0.75' >/dev/null

responses_payload="$(curl --fail --silent -H 'Authorization: Bearer dev-change-me' -H 'Content-Type: application/json' -d '{"model":"agic-code-fast","input":"hello responses"}' http://127.0.0.1:8080/v1/responses)"
echo "$responses_payload" | grep --quiet '"object":"response"'
echo "$responses_payload" | jq -e '.usage.input_tokens == 13 and .usage.output_tokens == 5' >/dev/null

python3 tests/backend/integration/assert_streaming.py http://127.0.0.1:8080/v1/chat/completions dev-change-me

observability_ready=false
metrics_json='[]'
for attempt in {1..30}; do
  metrics_json="$(curl --fail --silent 'http://127.0.0.1:8080/api/admin/metrics?take=200')"
  if echo "$metrics_json" | jq -e 'map(select(.isStreaming == true and .timeToFirstByteMilliseconds != null and .totalTokens == 23)) | length > 0' >/dev/null; then observability_ready=true; break; fi
  sleep 0.25
done
if [[ "$observability_ready" != "true" ]]; then fail_with_diagnostics "Inference observability metrics did not contain the completed SSE request."; fi

echo "$metrics_json" | jq -e 'map(select(.surface == "chat_completions" and .attemptCount >= 1 and .upstreamHeaderMilliseconds != null and .totalTokens != null)) | length > 0' >/dev/null
echo "$metrics_json" | jq -e 'map(select(.surface == "responses" and .inputTokens == 13 and .outputTokens == 5 and .totalTokens == 18)) | length > 0' >/dev/null
echo "$metrics_json" | jq -e 'map(select(.isStreaming == true and .inputTokens == 17 and .outputTokens == 6 and .totalTokens == 23)) | length > 0' >/dev/null

summary_json="$(curl --fail --silent 'http://127.0.0.1:8080/api/admin/metrics/summary?hours=24')"
echo "$summary_json" | jq -e '.windowHours == 24 and .requestCount > 0 and .successCount > 0 and .p50DurationMilliseconds != null and .p95DurationMilliseconds != null and .p50TimeToFirstByteMilliseconds != null and .p95TimeToFirstByteMilliseconds != null and .outputTokens > 0 and .tokenObservedRequests > 0 and (.byModel | length) > 0 and (.byNode | length) > 0' >/dev/null

curl --fail --silent -X POST http://127.0.0.1:3451/__control/health/500 >/dev/null
wait_node_status "$node2_id" Degraded 10
wait_node_status "$node2_id" Unhealthy 15
failed_health="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes | jq -c --arg id "$node2_id" '.[] | select(.id == $id)')"
echo "$failed_health" | jq -e '.consecutiveHealthFailures >= 3 and (.lastHealthError | contains("HTTP 500"))' >/dev/null

curl --fail --silent -X POST http://127.0.0.1:3451/__control/health/200 >/dev/null
wait_node_status "$node2_id" Degraded 10
wait_node_status "$node2_id" Healthy 15
recovered_health="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes | jq -c --arg id "$node2_id" '.[] | select(.id == $id)')"
echo "$recovered_health" | jq -e '.consecutiveHealthSuccesses >= 2 and .consecutiveHealthFailures == 0 and .lastHealthError == null' >/dev/null

audit_json="$(curl --fail --silent 'http://127.0.0.1:8080/api/admin/audit?take=100')"
echo "$audit_json" | jq -e 'map(.action) | index("node.create") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.action) | index("deployment.create") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.action) | index("node.test_connection") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.action) | index("routing.update") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.action) | index("routing.tuning.update") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.actor) | index("local-admin") != null' >/dev/null

curl --fail --silent http://127.0.0.1:8080/api/admin/overview | grep --quiet 'activeRequests'

echo "Backend integration smoke suite passed. Weighted=${primary_count}/${alternate_count}, round-robin=${rr_primary}/${rr_alternate}, System One proxy, live tuning, vLLM runtime telemetry, observability, health hysteresis and audit verified."
