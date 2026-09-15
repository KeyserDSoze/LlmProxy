#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE=(docker compose -f docker/docker-compose.full.yml)
PEER_NAME="llmproxy-maintenance-peer"
MOCK_PID=""
ACTIVE_PID=""

export POSTGRES_DB=llmproxy
export POSTGRES_USER=llmproxy
export POSTGRES_PASSWORD=maintenance-postgres
export REDIS_PASSWORD=maintenance-redis
export REDIS_KEY_PREFIX=llmproxy
export REDIS_RECONCILE_SECONDS=1
export REDIS_CAPACITY_LEASE_SECONDS=30
export REDIS_CAPACITY_RENEW_SECONDS=5
export LLM_PROXY_API_KEY=maintenance-api-key
export LLM_PROXY_API_KEY_PEPPER=maintenance-pepper
export GRAFANA_ADMIN_USER=admin
export GRAFANA_ADMIN_PASSWORD=maintenance-grafana
export GHCR_OWNER=keyserdsoze
export LLMPROXY_IMAGE_TAG=ci-full
export LLMPROXY_PULL_POLICY=never
export LLMPROXY_INSTANCE_ID=ci-maintenance-1
export DEPLOYMENT_ENVIRONMENT=ci
export ASPNETCORE_ENVIRONMENT=Development
export ENTRA_ENABLED=false
export ROUTING_STRATEGY=WeightedLeastLoaded
export HEALTH_INTERVAL_SECONDS=2
export HEALTH_HEALTHY_AFTER_SUCCESSES=1
export HEALTH_UNHEALTHY_AFTER_FAILURES=2
export RUNTIME_METRICS_ENABLED=false
export HARDWARE_METRICS_ENABLED=false
export RETENTION_ENABLED=false
export BOOTSTRAP_ENABLED=true
export DGX_NODE_NAME=dgx-maintenance
export DGX_NODE_BASE_ADDRESS=http://host.docker.internal:3495/maintenance
export DGX_NODE_WEIGHT=1
export DGX_NODE_MAX_CONCURRENCY=4
export PUBLIC_MODEL_NAME=agic-code-fast
export PROVIDER_MODEL_NAME=bootstrap-model

cleanup() {
  if [[ -n "$ACTIVE_PID" ]]; then
    kill "$ACTIVE_PID" >/dev/null 2>&1 || true
    wait "$ACTIVE_PID" >/dev/null 2>&1 || true
  fi
  docker rm -f "$PEER_NAME" >/dev/null 2>&1 || true
  "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
  if [[ -n "$MOCK_PID" ]]; then
    kill "$MOCK_PID" >/dev/null 2>&1 || true
    wait "$MOCK_PID" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

fail_with_diagnostics() {
  echo "$1" >&2
  docker logs "$PEER_NAME" >&2 2>/dev/null || true
  "${COMPOSE[@]}" ps -a >&2 || true
  "${COMPOSE[@]}" logs --no-color --tail=250 >&2 || true
  [[ -f /tmp/llmproxy-maintenance-mock.log ]] && cat /tmp/llmproxy-maintenance-mock.log >&2 || true
  for file in /tmp/maintenance-*.json /tmp/maintenance-*.txt; do
    [[ -f "$file" ]] && { echo "--- $file" >&2; cat "$file" >&2; }
  done
  exit 1
}

wait_http() {
  local url="$1"
  local attempts="${2:-60}"
  for ((i=1; i<=attempts; i++)); do
    if curl --fail --silent "$url" >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done
  return 1
}

call_model() {
  local port="$1"
  local output="$2"
  curl --silent --output "$output" --write-out '%{http_code}' \
    -H "Authorization: Bearer $LLM_PROXY_API_KEY" \
    -H 'Content-Type: application/json' \
    -d '{"model":"agic-code-fast","max_tokens":8,"messages":[{"role":"user","content":"maintenance verification"}]}' \
    "http://127.0.0.1:${port}/v1/chat/completions"
}

python3 tests/backend/integration/mock_llm.py --port 3495 --prefix /maintenance --name maintenance > /tmp/llmproxy-maintenance-mock.log 2>&1 &
MOCK_PID="$!"
sleep 1

if ! docker image inspect ghcr.io/keyserdsoze/llmproxy:ci-full >/dev/null 2>&1; then
  docker build -f docker/Dockerfile -t ghcr.io/keyserdsoze/llmproxy:ci-full .
fi

"${COMPOSE[@]}" up -d || fail_with_diagnostics "Maintenance smoke Compose failed to start."
wait_http http://127.0.0.1:8080/readyz 60 || fail_with_diagnostics "Primary maintenance gateway did not become ready."

healthy=false
for attempt in {1..40}; do
  nodes_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes || true)"
  if echo "$nodes_json" | jq -e 'map(select(.name == "dgx-maintenance" and .status == "Healthy")) | length == 1' >/dev/null 2>&1; then
    healthy=true
    break
  fi
  sleep 1
done
[[ "$healthy" == "true" ]] || fail_with_diagnostics "Maintenance node did not become Healthy."
node_id="$(echo "$nodes_json" | jq -r '.[] | select(.name == "dgx-maintenance") | .id')"
[[ "$node_id" =~ ^[0-9a-fA-F-]{36}$ ]] || fail_with_diagnostics "Maintenance node id was not resolved."
node_field="${node_id//-/}"

if ! docker run -d --name "$PEER_NAME" \
  --network llmproxy-full_default \
  --add-host host.docker.internal:host-gateway \
  -p 127.0.0.1:8081:8080 \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e "ConnectionStrings__Postgres=Host=postgres;Port=5432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}" \
  -e "Authentication__ApiKey=${LLM_PROXY_API_KEY}" \
  -e "Authentication__ApiKeyPepper=${LLM_PROXY_API_KEY_PEPPER}" \
  -e Redis__Enabled=true \
  -e "Redis__ConnectionString=redis:6379,password=${REDIS_PASSWORD},abortConnect=false" \
  -e "Redis__KeyPrefix=${REDIS_KEY_PREFIX}" \
  -e Redis__InstanceId=ci-maintenance-2 \
  -e Redis__ReconcileSeconds=1 \
  -e Redis__OutboxPollMilliseconds=250 \
  -e Redis__CapacityLeaseSeconds=30 \
  -e Redis__CapacityRenewSeconds=5 \
  -e OpenTelemetry__Enabled=false \
  -e EntraId__Enabled=false \
  -e Bootstrap__Enabled=false \
  -e Routing__Strategy=WeightedLeastLoaded \
  -e Health__IntervalSeconds=2 \
  -e Health__HealthyAfterSuccesses=1 \
  -e Health__UnhealthyAfterFailures=2 \
  -e RuntimeMetrics__Enabled=false \
  -e HardwareMetrics__Enabled=false \
  -e Retention__Enabled=false \
  ghcr.io/keyserdsoze/llmproxy:ci-full >/dev/null; then
  fail_with_diagnostics "Maintenance peer failed to start."
fi
wait_http http://127.0.0.1:8081/readyz 60 || fail_with_diagnostics "Maintenance peer did not become ready."

peer_healthy=false
for attempt in {1..40}; do
  peer_nodes="$(curl --fail --silent http://127.0.0.1:8081/api/admin/nodes || true)"
  if echo "$peer_nodes" | jq -e --arg id "$node_id" 'map(select(.id == $id and .status == "Healthy")) | length == 1' >/dev/null 2>&1; then
    peer_healthy=true
    break
  fi
  sleep 0.5
done
[[ "$peer_healthy" == "true" ]] || fail_with_diagnostics "Peer did not hydrate the maintenance node."

# Admit one long-running stream through the peer before draining. The Redis node lease must remain
# visible while the primary establishes the maintenance block.
curl --silent --no-buffer \
  -H "Authorization: Bearer $LLM_PROXY_API_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"model":"agic-code-fast","stream":true,"mock_stream_delay_seconds":4,"messages":[{"role":"user","content":"long maintenance request"}]}' \
  http://127.0.0.1:8081/v1/chat/completions > /tmp/maintenance-active-stream.txt &
ACTIVE_PID="$!"

lease_visible=false
for attempt in {1..40}; do
  active="$("${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" ZCARD "${REDIS_KEY_PREFIX}:capacity:node:${node_field}" 2>/dev/null | tr -d '\r')"
  if [[ "$active" -ge 1 ]]; then
    lease_visible=true
    break
  fi
  sleep 0.2
done
[[ "$lease_visible" == "true" ]] || fail_with_diagnostics "Long-running request never acquired a shared node capacity lease."

drain_status="$(curl --silent --output /tmp/maintenance-drain.json --write-out '%{http_code}' -X POST \
  "http://127.0.0.1:8080/api/admin/nodes/${node_id}/maintenance/drain")"
[[ "$drain_status" == "202" ]] || fail_with_diagnostics "Safe maintenance drain returned ${drain_status}."
jq -e '.status.nodeStatus == "Draining" and .status.admissionBlocked == true and .status.activeRequests >= 1' /tmp/maintenance-drain.json >/dev/null \
  || fail_with_diagnostics "Drain did not establish a block while preserving the active request."

redis_block="$("${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" HEXISTS "${REDIS_KEY_PREFIX}:maintenance:nodes" "$node_field" 2>/dev/null | tr -d '\r')"
[[ "$redis_block" == "1" ]] || fail_with_diagnostics "Durable Redis maintenance marker was not established."

# A second gateway must not admit new inference after drain begins. Depending on whether its L1 has
# already consumed the node event, it may reject at routing or at the Redis capacity gate; it must never be 200.
blocked_status="$(call_model 8081 /tmp/maintenance-blocked.json)"
[[ "$blocked_status" != "200" ]] || fail_with_diagnostics "Peer admitted new inference after distributed drain started."

resume_early="$(curl --silent --output /tmp/maintenance-resume-early.json --write-out '%{http_code}' -X POST \
  "http://127.0.0.1:8080/api/admin/nodes/${node_id}/maintenance/resume")"
[[ "$resume_early" == "409" ]] || fail_with_diagnostics "Resume should fail while an inference lease is active; got ${resume_early}."
jq -e '.code == "node_still_draining" and .status.activeRequests >= 1' /tmp/maintenance-resume-early.json >/dev/null

wait "$ACTIVE_PID" || fail_with_diagnostics "Pre-drain streaming request did not finish cleanly."
ACTIVE_PID=""

drained=false
for attempt in {1..40}; do
  curl --fail --silent "http://127.0.0.1:8080/api/admin/nodes/${node_id}/maintenance" > /tmp/maintenance-status.json || true
  if jq -e '.nodeStatus == "Draining" and .admissionBlocked == true and .activeRequests == 0 and .drained == true' /tmp/maintenance-status.json >/dev/null 2>&1; then
    drained=true
    break
  fi
  sleep 0.25
done
[[ "$drained" == "true" ]] || fail_with_diagnostics "Maintenance status never reached zero active requests."

# Simulate an upgrade/restart that is not yet healthy. Resume must validate the runtime and leave the
# node draining when health/model/warm-up validation is not safe.
curl --fail --silent -X POST http://127.0.0.1:3495/__control/health/503 >/dev/null
failed_resume="$(curl --silent --output /tmp/maintenance-resume-failed.json --write-out '%{http_code}' -X POST \
  "http://127.0.0.1:8080/api/admin/nodes/${node_id}/maintenance/resume")"
[[ "$failed_resume" == "503" ]] || fail_with_diagnostics "Unhealthy runtime resume should fail with 503; got ${failed_resume}."
jq -e '.code == "node_validation_failed" and .health.success == false' /tmp/maintenance-resume-failed.json >/dev/null

curl --fail --silent "http://127.0.0.1:8080/api/admin/nodes/${node_id}/maintenance" > /tmp/maintenance-still-draining.json
jq -e '.nodeStatus == "Draining" and .admissionBlocked == true' /tmp/maintenance-still-draining.json >/dev/null \
  || fail_with_diagnostics "Failed validation incorrectly returned the node to service."

# Runtime is healthy again. Resume performs /health + /v1/models + one minimal inference warm-up per
# enabled provider model before publishing Healthy and clearing the shared admission block.
curl --fail --silent -X POST http://127.0.0.1:3495/__control/health/200 >/dev/null
resume_status="$(curl --silent --output /tmp/maintenance-resume.json --write-out '%{http_code}' -X POST \
  "http://127.0.0.1:8080/api/admin/nodes/${node_id}/maintenance/resume")"
[[ "$resume_status" == "200" ]] || fail_with_diagnostics "Validated maintenance resume returned ${resume_status}."
jq -e '.status.nodeStatus == "Healthy" and .status.admissionBlocked == false and .health.success == true and .models.success == true and (.warmups | length) == 1 and .warmups[0].success == true' /tmp/maintenance-resume.json >/dev/null \
  || fail_with_diagnostics "Resume did not complete full health/model/inference warm-up validation."

redis_block_after="$("${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" HEXISTS "${REDIS_KEY_PREFIX}:maintenance:nodes" "$node_field" 2>/dev/null | tr -d '\r')"
[[ "$redis_block_after" == "0" ]] || fail_with_diagnostics "Redis maintenance marker remained after validated resume."

peer_resumed=false
for attempt in {1..40}; do
  peer_nodes="$(curl --fail --silent http://127.0.0.1:8081/api/admin/nodes || true)"
  if echo "$peer_nodes" | jq -e --arg id "$node_id" 'map(select(.id == $id and .status == "Healthy")) | length == 1' >/dev/null 2>&1; then
    peer_resumed=true
    break
  fi
  sleep 0.5
done
[[ "$peer_resumed" == "true" ]] || fail_with_diagnostics "Peer did not observe validated node resume."

primary_ok="$(call_model 8080 /tmp/maintenance-primary-after.json)"
peer_ok="$(call_model 8081 /tmp/maintenance-peer-after.json)"
[[ "$primary_ok" == "200" && "$peer_ok" == "200" ]] || fail_with_diagnostics "Inference did not recover on both gateways after maintenance resume."

audit_json="$(curl --fail --silent 'http://127.0.0.1:8080/api/admin/audit?take=100')"
echo "$audit_json" | jq -e --arg id "$node_id" 'map(select(.entityId == $id and .action == "node.maintenance.drain")) | length >= 1' >/dev/null
echo "$audit_json" | jq -e --arg id "$node_id" 'map(select(.entityId == $id and .action == "node.maintenance.resume_failed")) | length >= 1' >/dev/null
echo "$audit_json" | jq -e --arg id "$node_id" 'map(select(.entityId == $id and .action == "node.maintenance.resume")) | length >= 1' >/dev/null

echo "Node maintenance smoke passed: distributed pre-drain block, in-flight drain accounting, fail-closed resume, health/model/inference warm-up validation and cross-replica re-entry verified."
