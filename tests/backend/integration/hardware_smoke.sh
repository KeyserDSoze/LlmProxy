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
  cat /tmp/llmproxy-mock-dcgm.log >&2 2>/dev/null || true
  exit 1
}

python3 tests/backend/integration/mock_llm.py --port 3450 --prefix /primopath --name primary >/tmp/llmproxy-mock-primary.log 2>&1 &
MOCK_PIDS+=("$!")
python3 tests/backend/integration/mock_dcgm.py --port 3452 --prefix /dcgm >/tmp/llmproxy-mock-dcgm.log 2>&1 &
MOCK_PIDS+=("$!")
sleep 1

export LLM_PROXY_API_KEY="dev-change-me"
export LLM_PROXY_API_KEY_PEPPER="ci-test-pepper"
export ENTRA_ENABLED="false"
export BOOTSTRAP_ENABLED="true"
export INFERENCE_NODE_NAME="inference-hardware-test"
export INFERENCE_NODE_BASE_ADDRESS="http://host.docker.internal:3450/primopath"
export INFERENCE_NODE_HARDWARE_METRICS_BASE_ADDRESS=""
export HEALTH_INTERVAL_SECONDS="1"
export HEALTH_HEALTHY_AFTER_SUCCESSES="1"
export HEALTH_UNHEALTHY_AFTER_FAILURES="2"
export RUNTIME_METRICS_ENABLED="true"
export RUNTIME_METRICS_INTERVAL_SECONDS="1"
export HARDWARE_METRICS_ENABLED="true"
export HARDWARE_METRICS_INTERVAL_SECONDS="1"

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
[[ "$ready" == "true" ]] || fail_with_diagnostics "Gateway did not become ready."

node_id="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes | jq -r '.[0].id')"
[[ -n "$node_id" && "$node_id" != "null" ]] || fail_with_diagnostics "Bootstrap node was not created."

# Hardware telemetry is opt-in per node and can use a separate, path-prefixed service root.
configured="$(curl --fail --silent \
  -X PUT \
  -H 'Content-Type: application/json' \
  -d '{"baseAddress":"http://host.docker.internal:3452/dcgm/"}' \
  "http://127.0.0.1:8080/api/admin/nodes/${node_id}/hardware-metrics")"
echo "$configured" | jq -e '.hardwareMetricsBaseAddress == "http://host.docker.internal:3452/dcgm"' >/dev/null

hardware_ready=false
hardware_json='[]'
for attempt in {1..30}; do
  hardware_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/hardware)"
  if echo "$hardware_json" | jq -e --arg id "$node_id" '
      map(select(
        .nodeId == $id and
        .available == true and
        .gpuCount == 2 and
        .averageGpuUtilizationPercent == 60 and
        .maxGpuUtilizationPercent == 80 and
        .framebufferUsedMiB == 4000 and
        .framebufferFreeMiB == 12000 and
        .framebufferUsageRatio == 0.25 and
        .maxTemperatureCelsius == 67 and
        .totalPowerUsageWatts == 261
      )) | length == 1' >/dev/null; then
    hardware_ready=true
    break
  fi
  sleep 0.25
done
[[ "$hardware_ready" == "true" ]] || fail_with_diagnostics "DCGM hardware telemetry was not collected. Last payload: ${hardware_json}"

# The inference health monitor must independently reach Healthy.
node_healthy=false
for attempt in {1..20}; do
  node_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes | jq -c --arg id "$node_id" '.[] | select(.id == $id)')"
  if echo "$node_json" | jq -e '.status == "Healthy"' >/dev/null; then
    node_healthy=true
    break
  fi
  sleep 0.25
done
[[ "$node_healthy" == "true" ]] || fail_with_diagnostics "Inference node never became Healthy."

# Simulate DCGM exporter failure. Only the hardware snapshot may degrade; inference health must stay Healthy.
curl --fail --silent -X POST http://127.0.0.1:3452/__control/status/503 >/dev/null
hardware_failed=false
for attempt in {1..30}; do
  hardware_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/hardware)"
  if echo "$hardware_json" | jq -e --arg id "$node_id" 'map(select(.nodeId == $id and .available == false and (.error | contains("HTTP 503")))) | length == 1' >/dev/null; then
    hardware_failed=true
    break
  fi
  sleep 0.25
done
[[ "$hardware_failed" == "true" ]] || fail_with_diagnostics "Hardware snapshot did not report the DCGM failure."

node_after_dcgm_failure="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes | jq -c --arg id "$node_id" '.[] | select(.id == $id)')"
echo "$node_after_dcgm_failure" | jq -e '.status == "Healthy"' >/dev/null || fail_with_diagnostics "DCGM failure incorrectly changed inference node health."

# Last successful hardware values are intentionally retained for diagnostics during a temporary exporter failure.
echo "$hardware_json" | jq -e --arg id "$node_id" 'map(select(.nodeId == $id and .averageGpuUtilizationPercent == 60 and .maxTemperatureCelsius == 67)) | length == 1' >/dev/null

audit_json="$(curl --fail --silent 'http://127.0.0.1:8080/api/admin/audit?take=100')"
echo "$audit_json" | jq -e 'map(.action) | index("node.hardware_metrics.update") != null' >/dev/null

# Explicitly clearing the endpoint removes the stale runtime snapshot as well as the persisted configuration.
curl --fail --silent \
  -X PUT \
  -H 'Content-Type: application/json' \
  -d '{"baseAddress":null}' \
  "http://127.0.0.1:8080/api/admin/nodes/${node_id}/hardware-metrics" \
  | jq -e '.hardwareMetricsBaseAddress == null' >/dev/null

node_cleared="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes | jq -c --arg id "$node_id" '.[] | select(.id == $id)')"
echo "$node_cleared" | jq -e '.hardwareMetricsBaseAddress == null' >/dev/null
curl --fail --silent http://127.0.0.1:8080/api/admin/hardware | jq -e --arg id "$node_id" 'map(select(.nodeId == $id)) | length == 0' >/dev/null

echo "inference hardware telemetry smoke suite passed: path-prefixed DCGM metrics, aggregation, audit, health isolation and explicit disable verified."
