#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE=(docker compose -f docker/docker-compose.full.yml)
PEER_NAME="llmproxy-rotation-peer"
MOCK_PID=""

export POSTGRES_DB=llmproxy
export POSTGRES_USER=llmproxy
export POSTGRES_PASSWORD=rotation-postgres
export REDIS_PASSWORD=rotation-redis
export REDIS_KEY_PREFIX=llmproxy
export REDIS_RECONCILE_SECONDS=1
export REDIS_OUTBOX_BATCH_SIZE=20
export REDIS_OUTBOX_POLL_MILLISECONDS=200
export REDIS_CAPACITY_LEASE_SECONDS=30
export REDIS_CAPACITY_RENEW_SECONDS=5
export LLM_PROXY_API_KEY=rotation-old-api-key
export LLM_PROXY_API_KEY_PEPPER=rotation-pepper
export GRAFANA_ADMIN_USER=admin
export GRAFANA_ADMIN_PASSWORD=rotation-grafana
export GHCR_OWNER=keyserdsoze
export LLMPROXY_IMAGE_TAG=ci-full
export LLMPROXY_PULL_POLICY=never
export LLMPROXY_INSTANCE_ID=ci-rotation-1
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
export INFERENCE_NODE_NAME=inference-rotation
export INFERENCE_NODE_BASE_ADDRESS=http://host.docker.internal:3493/rotation
export INFERENCE_NODE_WEIGHT=1
export INFERENCE_NODE_MAX_CONCURRENCY=4
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
  [[ -f /tmp/credential-rotation-response.json ]] && cat /tmp/credential-rotation-response.json >&2 || true
  [[ -f /tmp/llmproxy-rotation-mock.log ]] && cat /tmp/llmproxy-rotation-mock.log >&2 || true
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
  local secret="$2"
  local output="$3"
  curl --silent --dump-header "${output}.headers" --output "${output}.json" --write-out '%{http_code}' \
    -H "Authorization: Bearer ${secret}" \
    -H 'Content-Type: application/json' \
    -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"credential rotation smoke"}]}' \
    "http://127.0.0.1:${port}/v1/chat/completions"
}

hash_secret() {
  python3 - "$LLM_PROXY_API_KEY_PEPPER" "$1" <<'PY'
import hashlib
import hmac
import sys
print(hmac.new(sys.argv[1].encode(), sys.argv[2].encode(), hashlib.sha256).hexdigest().upper())
PY
}

python3 tests/backend/integration/mock_llm.py --port 3493 --prefix /rotation --name rotation > /tmp/llmproxy-rotation-mock.log 2>&1 &
MOCK_PID="$!"
sleep 1

if ! docker image inspect ghcr.io/keyserdsoze/llmproxy:ci-full >/dev/null 2>&1; then
  docker build -f docker/Dockerfile -t ghcr.io/keyserdsoze/llmproxy:ci-full .
fi

if ! "${COMPOSE[@]}" up -d; then
  fail_with_diagnostics "Credential-rotation Compose failed to start."
fi
wait_http http://127.0.0.1:8080/readyz 60 || fail_with_diagnostics "Primary gateway did not become ready."

healthy=false
for attempt in {1..40}; do
  nodes_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes || true)"
  if echo "$nodes_json" | jq -e 'map(select(.name == "inference-rotation" and .status == "Healthy")) | length == 1' >/dev/null 2>&1; then
    healthy=true
    break
  fi
  sleep 1
done
[[ "$healthy" == "true" ]] || fail_with_diagnostics "Rotation node did not become Healthy."

credential_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/governance/credentials)"
credential_id="$(echo "$credential_json" | jq -r '.[0].id')"
old_prefix="$(echo "$credential_json" | jq -r '.[0].keyPrefix')"
[[ "$credential_id" =~ ^[0-9a-fA-F-]{36}$ ]] || fail_with_diagnostics "Bootstrap credential was not available."

# Preserve meaningful caller-governance state across the rotation.
group_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' \
  -d '{"name":"Rotation Team","description":"Credential rotation HA smoke"}' \
  http://127.0.0.1:8080/api/admin/usage-groups)"
group_id="$(echo "$group_json" | jq -r '.id')"
curl --fail --silent -X PUT -H 'Content-Type: application/json' \
  -d "{\"usageGroupId\":\"${group_id}\"}" \
  "http://127.0.0.1:8080/api/admin/api-credentials/${credential_id}/usage-group" >/dev/null
policy_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' \
  -d "{\"apiCredentialId\":\"${credential_id}\",\"logicalModel\":\"agic-code-fast\",\"requestsPerWindow\":100,\"windowSeconds\":60,\"enabled\":true}" \
  http://127.0.0.1:8080/api/admin/rate-limits)"
policy_id="$(echo "$policy_json" | jq -r '.id')"

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
  -e Redis__InstanceId=ci-rotation-2 \
  -e Redis__ReconcileSeconds=1 \
  -e Redis__OutboxBatchSize=20 \
  -e Redis__OutboxPollMilliseconds=200 \
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
  fail_with_diagnostics "Rotation peer failed to start."
fi
wait_http http://127.0.0.1:8081/readyz 60 || fail_with_diagnostics "Rotation peer did not become ready."

peer_healthy=false
for attempt in {1..40}; do
  peer_nodes="$(curl --fail --silent http://127.0.0.1:8081/api/admin/nodes || true)"
  if echo "$peer_nodes" | jq -e 'map(select(.name == "inference-rotation" and .status == "Healthy")) | length == 1' >/dev/null 2>&1; then
    peer_healthy=true
    break
  fi
  sleep 1
done
[[ "$peer_healthy" == "true" ]] || fail_with_diagnostics "Rotation peer did not observe the shared inference node as Healthy."

old_primary="$(call_model 8080 "$LLM_PROXY_API_KEY" /tmp/credential-rotation-old-primary-before)"
old_peer="$(call_model 8081 "$LLM_PROXY_API_KEY" /tmp/credential-rotation-old-peer-before)"
[[ "$old_primary" == "200" && "$old_peer" == "200" ]] || fail_with_diagnostics "Old credential was not usable before rotation; got ${old_primary}/${old_peer}."

rotation_json="$(curl --fail --silent -X POST "http://127.0.0.1:8080/api/admin/api-credentials/${credential_id}/rotate")"
printf '%s\n' "$rotation_json" > /tmp/credential-rotation-response.json
new_secret="$(echo "$rotation_json" | jq -r '.secret')"
new_prefix="$(echo "$rotation_json" | jq -r '.keyPrefix')"
echo "$rotation_json" | jq -e --arg id "$credential_id" --arg group "$group_id" '.id == $id and .usageGroupId == $group and .enabled == true and (.secret | startswith("lp_"))' >/dev/null \
  || fail_with_diagnostics "Rotation response did not preserve credential identity/group or return the one-time secret."
[[ "$new_prefix" != "$old_prefix" ]] || fail_with_diagnostics "Credential prefix did not change after rotation."
[[ "$new_secret" != "$LLM_PROXY_API_KEY" ]] || fail_with_diagnostics "Credential secret did not change after rotation."

# Originating L1 should cut over immediately after the durable save.
new_primary="$(call_model 8080 "$new_secret" /tmp/credential-rotation-new-primary)"
old_primary_after="$(call_model 8080 "$LLM_PROXY_API_KEY" /tmp/credential-rotation-old-primary-after)"
[[ "$new_primary" == "200" ]] || fail_with_diagnostics "New credential was not immediately usable on the originating gateway; got ${new_primary}."
[[ "$old_primary_after" == "401" ]] || fail_with_diagnostics "Old credential remained valid on the originating gateway after rotation; got ${old_primary_after}."

# The peer existed before rotation. Wait for transactional outbox -> Redis -> peer L1 propagation.
peer_cutover=false
for attempt in {1..60}; do
  new_peer="$(call_model 8081 "$new_secret" /tmp/credential-rotation-new-peer)"
  old_peer_after="$(call_model 8081 "$LLM_PROXY_API_KEY" /tmp/credential-rotation-old-peer-after)"
  if [[ "$new_peer" == "200" && "$old_peer_after" == "401" ]]; then
    peer_cutover=true
    break
  fi
  sleep 0.25
done
[[ "$peer_cutover" == "true" ]] || fail_with_diagnostics "Peer L1 did not converge to the rotated credential."

# Durable/runtime state must contain the new HMAC only; the raw secret must not appear in Redis.
credential_field="${credential_id//-/}"
redis_credential="$("${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" HGET llmproxy:credentials "$credential_field" 2>/dev/null | tr -d '\r')"
new_hash="$(hash_secret "$new_secret")"
old_hash="$(hash_secret "$LLM_PROXY_API_KEY")"
[[ "$redis_credential" == *"$new_hash"* ]] || fail_with_diagnostics "Redis credential snapshot did not contain the rotated HMAC."
[[ "$redis_credential" != *"$old_hash"* ]] || fail_with_diagnostics "Redis credential snapshot still contained the previous HMAC."
[[ "$redis_credential" != *"$new_secret"* ]] || fail_with_diagnostics "Raw rotated secret leaked into Redis runtime state."

processed_rotation="$(psql_scalar "SELECT COUNT(*) FROM runtime_state_outbox WHERE \"Kind\" = 'credential' AND \"Action\" = 'upsert' AND \"EntityId\" = '${credential_id}'::uuid AND \"ProcessedAtUtc\" IS NOT NULL;")"
[[ "$processed_rotation" -ge 1 ]] || fail_with_diagnostics "Credential rotation did not complete transactional outbox publication."

membership="$(curl --fail --silent http://127.0.0.1:8081/api/admin/governance/credentials)"
echo "$membership" | jq -e --arg id "$credential_id" --arg group "$group_id" 'map(select(.id == $id and .usageGroupId == $group)) | length == 1' >/dev/null \
  || fail_with_diagnostics "Credential rotation lost Usage Group assignment."
persisted_policy="$(curl --fail --silent http://127.0.0.1:8081/api/admin/rate-limits)"
echo "$persisted_policy" | jq -e --arg id "$policy_id" --arg credential "$credential_id" 'map(select(.id == $id and .apiCredentialId == $credential and .enabled == true)) | length == 1' >/dev/null \
  || fail_with_diagnostics "Credential rotation lost caller-governance policy identity."

audit_json="$(curl --fail --silent 'http://127.0.0.1:8080/api/admin/audit?take=100')"
rotation_audit="$(echo "$audit_json" | jq -r 'map(select(.action == "credential.rotate")) | first | .detailsJson // ""')"
[[ -n "$rotation_audit" ]] || fail_with_diagnostics "Credential rotation audit event was not recorded."
[[ "$rotation_audit" == *"$old_prefix"* && "$rotation_audit" == *"$new_prefix"* ]] || fail_with_diagnostics "Credential rotation audit did not record safe prefix transition metadata."
[[ "$rotation_audit" != *"$new_secret"* ]] || fail_with_diagnostics "Raw rotated secret leaked into audit metadata."
[[ "$rotation_audit" != *"$new_hash"* ]] || fail_with_diagnostics "Rotated HMAC leaked into audit metadata."

# Restart the peer to prove PostgreSQL/Redis startup hydration retains only the new key.
docker restart "$PEER_NAME" >/dev/null
wait_http http://127.0.0.1:8081/readyz 60 || fail_with_diagnostics "Rotation peer did not recover after restart."
restart_new="$(call_model 8081 "$new_secret" /tmp/credential-rotation-restart-new)"
restart_old="$(call_model 8081 "$LLM_PROXY_API_KEY" /tmp/credential-rotation-restart-old)"
[[ "$restart_new" == "200" && "$restart_old" == "401" ]] || fail_with_diagnostics "Rotated credential was not preserved across peer restart; got ${restart_new}/${restart_old}."

echo "Credential rotation smoke passed: in-place hard cutover, cross-replica propagation, Redis HMAC replacement, governance preservation, audit secrecy and restart hydration verified."
