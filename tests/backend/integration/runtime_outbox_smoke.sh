#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE=(docker compose -f docker/docker-compose.full.yml)
PEER_NAME="llmproxy-outbox-peer"
MOCK_PID=""

export POSTGRES_DB=llmproxy
export POSTGRES_USER=llmproxy
export POSTGRES_PASSWORD=outbox-postgres
export REDIS_PASSWORD=outbox-redis
export REDIS_KEY_PREFIX=llmproxy
export REDIS_RECONCILE_SECONDS=1
export REDIS_OUTBOX_BATCH_SIZE=17
export REDIS_OUTBOX_POLL_MILLISECONDS=250
export REDIS_CAPACITY_LEASE_SECONDS=30
export REDIS_CAPACITY_RENEW_SECONDS=5
export LLM_PROXY_API_KEY=outbox-api-key
export LLM_PROXY_API_KEY_PEPPER=outbox-pepper
export GRAFANA_ADMIN_USER=admin
export GRAFANA_ADMIN_PASSWORD=outbox-grafana
export GHCR_OWNER=keyserdsoze
export LLMPROXY_IMAGE_TAG=ci-full
export LLMPROXY_PULL_POLICY=never
export LLMPROXY_INSTANCE_ID=ci-outbox-1
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
export RETENTION_RUNTIME_STATE_OUTBOX_DAYS=30
export BOOTSTRAP_ENABLED=true
export DGX_NODE_NAME=dgx-outbox
export DGX_NODE_BASE_ADDRESS=http://host.docker.internal:3491/outbox
export DGX_NODE_WEIGHT=1
export DGX_NODE_MAX_CONCURRENCY=2
export PUBLIC_MODEL_NAME=agic-code-fast
export PROVIDER_MODEL_NAME=bootstrap-model

cleanup() {
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
  [[ -f /tmp/llmproxy-outbox-mock.log ]] && cat /tmp/llmproxy-outbox-mock.log >&2 || true
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

psql_scalar() {
  local sql="$1"
  "${COMPOSE[@]}" exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" postgres \
    psql -h 127.0.0.1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "$sql" 2>/dev/null \
    | tr -d '[:space:]'
}

call_model() {
  local port="$1"
  local output="$2"
  curl --silent --dump-header "${output}.headers" --output "${output}.json" --write-out '%{http_code}' \
    -H "Authorization: Bearer $LLM_PROXY_API_KEY" \
    -H 'Content-Type: application/json' \
    -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"transactional outbox smoke"}]}' \
    "http://127.0.0.1:${port}/v1/chat/completions"
}

python3 tests/backend/integration/mock_llm.py --port 3491 --prefix /outbox --name outbox > /tmp/llmproxy-outbox-mock.log 2>&1 &
MOCK_PID="$!"
sleep 1

if ! docker image inspect ghcr.io/keyserdsoze/llmproxy:ci-full >/dev/null 2>&1; then
  docker build -f docker/Dockerfile -t ghcr.io/keyserdsoze/llmproxy:ci-full .
fi

if ! "${COMPOSE[@]}" up -d; then
  fail_with_diagnostics "Outbox smoke Compose failed to start."
fi

wait_http http://127.0.0.1:8080/readyz 60 || fail_with_diagnostics "Primary gateway did not become ready."

healthy=false
for attempt in {1..40}; do
  nodes_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes || true)"
  if echo "$nodes_json" | jq -e 'map(select(.name == "dgx-outbox" and .status == "Healthy")) | length == 1' >/dev/null 2>&1; then
    healthy=true
    break
  fi
  sleep 1
done
[[ "$healthy" == "true" ]] || fail_with_diagnostics "Primary gateway node did not become Healthy."

credential_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/governance/credentials)"
credential_id="$(echo "$credential_json" | jq -r '.[0].id')"
[[ -n "$credential_id" && "$credential_id" != "null" ]] || fail_with_diagnostics "Bootstrap credential was not available."

# Start a peer against the same durable PostgreSQL and Redis state. Its health loop is deliberately slow
# so fault injection does not flood the ordered outbox with unrelated health-state mutations.
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
  -e Redis__InstanceId=ci-outbox-2 \
  -e Redis__ReconcileSeconds=1 \
  -e Redis__OutboxBatchSize=17 \
  -e Redis__OutboxPollMilliseconds=250 \
  -e Redis__CapacityLeaseSeconds=30 \
  -e Redis__CapacityRenewSeconds=5 \
  -e OpenTelemetry__Enabled=false \
  -e EntraId__Enabled=false \
  -e Bootstrap__Enabled=false \
  -e Routing__Strategy=WeightedLeastLoaded \
  -e Health__IntervalSeconds=30 \
  -e Health__HealthyAfterSuccesses=1 \
  -e Health__UnhealthyAfterFailures=2 \
  -e RuntimeMetrics__Enabled=false \
  -e HardwareMetrics__Enabled=false \
  -e Retention__Enabled=false \
  ghcr.io/keyserdsoze/llmproxy:ci-full >/dev/null; then
  fail_with_diagnostics "Outbox peer failed to start."
fi

wait_http http://127.0.0.1:8081/readyz 60 || fail_with_diagnostics "Outbox peer did not become ready."

peer_healthy=false
for attempt in {1..40}; do
  peer_nodes="$(curl --fail --silent http://127.0.0.1:8081/api/admin/nodes || true)"
  if echo "$peer_nodes" | jq -e 'map(select(.name == "dgx-outbox" and .status == "Healthy")) | length == 1' >/dev/null 2>&1; then
    peer_healthy=true
    break
  fi
  sleep 1
done
[[ "$peer_healthy" == "true" ]] || fail_with_diagnostics "Outbox peer did not observe the shared node as Healthy."

# Wait until startup/bootstrap outbox work is completely drained before fault injection.
outbox_drained=false
for attempt in {1..40}; do
  pending_count="$(psql_scalar 'SELECT COUNT(*) FROM runtime_state_outbox WHERE "ProcessedAtUtc" IS NULL;')"
  if [[ "$pending_count" == "0" ]]; then
    outbox_drained=true
    break
  fi
  sleep 0.5
done
[[ "$outbox_drained" == "true" ]] || fail_with_diagnostics "Startup runtime-state outbox did not drain before fault injection."

peer_sync_before="$(curl --fail --silent http://127.0.0.1:8081/api/admin/runtime-sync)"
echo "$peer_sync_before" | jq -e '.connected == true and .outbox.pendingCount == 0 and .outbox.failedPendingCount == 0' >/dev/null \
  || fail_with_diagnostics "Runtime-sync diagnostics did not report a clean outbox before fault injection."
peer_published_before="$(echo "$peer_sync_before" | jq -r '.publishedEvents')"

# The control-plane mutation must commit even while Redis is unavailable. The same PostgreSQL commit must
# contain an undelivered outbox record; no caller is allowed to rely on an in-memory enqueue as durability.
"${COMPOSE[@]}" stop redis >/dev/null

redis_loss_observed=false
for attempt in {1..30}; do
  primary_sync="$(curl --fail --silent http://127.0.0.1:8080/api/admin/runtime-sync || true)"
  peer_sync="$(curl --fail --silent http://127.0.0.1:8081/api/admin/runtime-sync || true)"
  if echo "$primary_sync" | jq -e '.connected == false' >/dev/null 2>&1 && \
     echo "$peer_sync" | jq -e '.connected == false' >/dev/null 2>&1; then
    redis_loss_observed=true
    break
  fi
  sleep 0.5
done
[[ "$redis_loss_observed" == "true" ]] || fail_with_diagnostics "Gateways did not observe Redis outage."

policy_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' \
  -d "{\"apiCredentialId\":\"${credential_id}\",\"logicalModel\":\"agic-code-fast\",\"requestsPerWindow\":1,\"windowSeconds\":60,\"enabled\":true}" \
  http://127.0.0.1:8080/api/admin/rate-limits)"
printf '%s\n' "$policy_json" > /tmp/runtime-outbox-policy.json
policy_id="$(echo "$policy_json" | jq -r '.id')"
[[ "$policy_id" =~ ^[0-9a-fA-F-]{36}$ ]] || fail_with_diagnostics "Rate policy was not committed while Redis was down."

pending_policy="$(psql_scalar "SELECT COUNT(*) FROM runtime_state_outbox WHERE \"Kind\" = 'rate-policy' AND \"Action\" = 'upsert' AND \"EntityId\" = '${policy_id}'::uuid AND \"ProcessedAtUtc\" IS NULL;")"
[[ "$pending_policy" == "1" ]] || fail_with_diagnostics "Expected one pending transactional outbox row for policy ${policy_id}; got ${pending_policy}."

retry_observed=false
for attempt in {1..30}; do
  failed_pending="$(psql_scalar 'SELECT COUNT(*) FROM runtime_state_outbox WHERE "ProcessedAtUtc" IS NULL AND "AttemptCount" >= 1;')"
  if [[ "$failed_pending" =~ ^[0-9]+$ && "$failed_pending" -ge 1 ]]; then
    retry_observed=true
    break
  fi
  sleep 0.5
done
[[ "$retry_observed" == "true" ]] || fail_with_diagnostics "Outbox worker did not retain a failed Redis publication for retry."

fault_sync="$(curl --fail --silent http://127.0.0.1:8080/api/admin/runtime-sync)"
echo "$fault_sync" | jq -e '.connected == false and .outbox.pendingCount >= 1 and .outbox.failedPendingCount >= 1 and .outbox.oldestPendingAgeSeconds >= 0 and .outbox.maxPendingAttemptCount >= 1 and (.outbox.lastError | type == "string" and length > 0)' >/dev/null \
  || fail_with_diagnostics "Runtime-sync diagnostics did not expose the failed pending outbox event."

# Stop the replica that originated the DB mutation. The surviving peer is now the only process that can
# acquire the PostgreSQL outbox advisory lock, so replay explicitly exercises non-originating publication.
"${COMPOSE[@]}" stop llmproxy >/dev/null
"${COMPOSE[@]}" start redis >/dev/null

redis_ready=false
for attempt in {1..40}; do
  if "${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" ping 2>/dev/null | grep -q PONG; then
    redis_ready=true
    break
  fi
  sleep 0.5
done
[[ "$redis_ready" == "true" ]] || fail_with_diagnostics "Redis did not recover for outbox replay."

peer_reconnected=false
for attempt in {1..40}; do
  peer_sync="$(curl --fail --silent http://127.0.0.1:8081/api/admin/runtime-sync || true)"
  if echo "$peer_sync" | jq -e '.connected == true' >/dev/null 2>&1; then
    peer_reconnected=true
    break
  fi
  sleep 0.5
done
[[ "$peer_reconnected" == "true" ]] || fail_with_diagnostics "Surviving peer did not reconnect to Redis."

policy_replayed=false
for attempt in {1..60}; do
  processed_policy="$(psql_scalar "SELECT COUNT(*) FROM runtime_state_outbox WHERE \"Kind\" = 'rate-policy' AND \"Action\" = 'upsert' AND \"EntityId\" = '${policy_id}'::uuid AND \"ProcessedAtUtc\" IS NOT NULL;")"
  if [[ "$processed_policy" == "1" ]]; then
    policy_replayed=true
    break
  fi
  sleep 0.5
done
[[ "$policy_replayed" == "true" ]] || fail_with_diagnostics "Pending rate-policy outbox row was not replayed after Redis recovery."

policy_field="${policy_id//-/}"
redis_policy_present="$("${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" HEXISTS llmproxy:rate-policies "$policy_field" 2>/dev/null | tr -d '\r')"
[[ "$redis_policy_present" == "1" ]] || fail_with_diagnostics "Replayed rate policy was not durably written to Redis."

peer_sync_after="$(curl --fail --silent http://127.0.0.1:8081/api/admin/runtime-sync)"
echo "$peer_sync_after" | jq -e '.connected == true and .outbox.pendingCount == 0 and .outbox.failedPendingCount == 0 and .outbox.lastProcessedAtUtc != null and .outbox.lastError == null' >/dev/null \
  || fail_with_diagnostics "Runtime-sync diagnostics did not report a healthy drained outbox after recovery."
peer_published_after="$(echo "$peer_sync_after" | jq -r '.publishedEvents')"
[[ "$peer_published_after" -gt "$peer_published_before" ]] || fail_with_diagnostics "Surviving peer did not publish the recovered outbox work."

# The peer started before the policy existed and the originating gateway is stopped. Enforcing the new
# policy here proves the replay updated the publishing peer's own L1 rather than only Redis/other replicas.
outbox_first="$(call_model 8081 /tmp/runtime-outbox-rate-a)"
outbox_second="$(call_model 8081 /tmp/runtime-outbox-rate-b)"
[[ "$outbox_first" == "200" ]] || fail_with_diagnostics "Expected first request under replayed policy to succeed; got ${outbox_first}."
[[ "$outbox_second" == "429" ]] || fail_with_diagnostics "Expected surviving peer to enforce replayed rate policy from its local L1; got ${outbox_second}."
jq -e '.error.type == "rate_limit_error" and .error.code == "rate_limit_exceeded"' /tmp/runtime-outbox-rate-b.json >/dev/null \
  || fail_with_diagnostics "Replayed policy rejection did not expose rate_limit_exceeded."

echo "Runtime-state transactional outbox smoke passed: DB commit survives Redis outage, diagnostics expose backlog/retry state, pending delivery is replayed after recovery, and a non-originating peer applies it to its own L1."
