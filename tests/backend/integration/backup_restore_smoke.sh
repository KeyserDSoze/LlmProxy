#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE_FILE="tests/backend/integration/backup_restore_compose.yml"
COMPOSE=(docker compose -f "$COMPOSE_FILE")
BACKUP_FILE="/tmp/llmproxy-backup-restore.dump"
MOCK_PID=""
REDIS_NAME="llmproxy-backup-restore-redis"
PEER_NAME="llmproxy-backup-restore-peer"

export POSTGRES_DB=llmproxy
export POSTGRES_USER=llmproxy
export POSTGRES_PASSWORD=backup-restore-postgres
export LLM_PROXY_API_KEY=backup-bootstrap-key
export LLM_PROXY_API_KEY_PEPPER=backup-restore-pepper
export LLMPROXY_PORT=8084

cleanup() {
  docker rm -f "$PEER_NAME" "$REDIS_NAME" >/dev/null 2>&1 || true
  "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
  if [[ -n "$MOCK_PID" ]]; then
    kill "$MOCK_PID" >/dev/null 2>&1 || true
    wait "$MOCK_PID" >/dev/null 2>&1 || true
  fi
  rm -f "$BACKUP_FILE" "${BACKUP_FILE}.sha256" "${BACKUP_FILE}.meta"
}
trap cleanup EXIT

fail_with_diagnostics() {
  echo "$1" >&2
  docker logs "$PEER_NAME" >&2 2>/dev/null || true
  docker logs "$REDIS_NAME" >&2 2>/dev/null || true
  "${COMPOSE[@]}" ps -a >&2 || true
  "${COMPOSE[@]}" logs --no-color --tail=250 >&2 || true
  [[ -f /tmp/llmproxy-backup-restore-mock.log ]] && cat /tmp/llmproxy-backup-restore-mock.log >&2 || true
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

wait_node_healthy() {
  local port="$1"
  for attempt in {1..50}; do
    local nodes_json
    nodes_json="$(curl --fail --silent "http://127.0.0.1:${port}/api/admin/nodes" || true)"
    if echo "$nodes_json" | jq -e 'map(select(.name == "dgx-backup-restore" and .status == "Healthy")) | length == 1' >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done
  return 1
}

call_model() {
  local port="$1"
  local secret="$2"
  local output="$3"
  curl --silent --dump-header "${output}.headers" --output "${output}.json" --write-out '%{http_code}' \
    -H "Authorization: Bearer ${secret}" \
    -H 'Content-Type: application/json' \
    -d '{"model":"agic-code-fast","max_tokens":50,"messages":[{"role":"user","content":"backup restore verification"}]}' \
    "http://127.0.0.1:${port}/v1/chat/completions"
}

psql_scalar() {
  local sql="$1"
  "${COMPOSE[@]}" exec -T postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "$sql" 2>/dev/null | tr -d '[:space:]'
}

python3 tests/backend/integration/mock_llm.py --port 3494 --prefix /backup-restore --name backup-restore > /tmp/llmproxy-backup-restore-mock.log 2>&1 &
MOCK_PID="$!"
sleep 1

if ! docker image inspect llmproxy:ci >/dev/null 2>&1; then
  docker build -f docker/Dockerfile -t llmproxy:ci .
fi

rm -f "$BACKUP_FILE" "${BACKUP_FILE}.sha256" "${BACKUP_FILE}.meta"
if ! "${COMPOSE[@]}" up -d; then
  fail_with_diagnostics "Source backup/restore stack failed to start."
fi
wait_http http://127.0.0.1:8084/readyz 60 || fail_with_diagnostics "Source gateway did not become ready."
wait_node_healthy 8084 || fail_with_diagnostics "Source DGX route did not become Healthy."

# Create durable state that is not part of bootstrap so restore correctness is observable.
group_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' \
  -d '{"name":"Restore Team","description":"Backup restore verification group"}' \
  http://127.0.0.1:8084/api/admin/usage-groups)"
group_id="$(echo "$group_json" | jq -r '.id')"

credential_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' \
  -d '{"name":"Restore Client"}' \
  http://127.0.0.1:8084/api/admin/api-credentials)"
credential_id="$(echo "$credential_json" | jq -r '.id')"
credential_secret="$(echo "$credential_json" | jq -r '.secret')"
credential_prefix="$(echo "$credential_json" | jq -r '.keyPrefix')"
[[ "$credential_secret" == lp_* ]] || fail_with_diagnostics "Created credential secret was not returned."

curl --fail --silent -X PUT -H 'Content-Type: application/json' \
  -d "{\"usageGroupId\":\"${group_id}\"}" \
  "http://127.0.0.1:8084/api/admin/api-credentials/${credential_id}/usage-group" >/dev/null

policy_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' \
  -d "{\"apiCredentialId\":\"${credential_id}\",\"logicalModel\":\"agic-code-fast\",\"requestsPerWindow\":20,\"windowSeconds\":120,\"enabled\":true}" \
  http://127.0.0.1:8084/api/admin/rate-limits)"
policy_id="$(echo "$policy_json" | jq -r '.id')"
curl --fail --silent -X PUT -H 'Content-Type: application/json' \
  -d '{"outputTokensPerWindow":1000,"maxOutputTokensPerRequest":100}' \
  "http://127.0.0.1:8084/api/admin/rate-limits/${policy_id}/output-token-budget" >/dev/null

source_status="$(call_model 8084 "$credential_secret" /tmp/backup-restore-source)"
[[ "$source_status" == "200" ]] || fail_with_diagnostics "Source credential could not perform inference before backup; got ${source_status}."

# Allow async metric/last-used persistence to flush at least once before the backup.
sleep 2
source_audit_count="$(psql_scalar 'SELECT COUNT(*) FROM audit_events;')"
source_group_count="$(psql_scalar 'SELECT COUNT(*) FROM usage_groups;')"
source_policy_count="$(psql_scalar 'SELECT COUNT(*) FROM rate_limit_policies;')"
[[ "$source_audit_count" -ge 3 && "$source_group_count" -ge 1 && "$source_policy_count" -ge 1 ]] \
  || fail_with_diagnostics "Source durable state was incomplete before backup."

COMPOSE_FILE="$COMPOSE_FILE" bash docker/scripts/postgres-backup.sh "$BACKUP_FILE"
[[ -s "$BACKUP_FILE" && -s "${BACKUP_FILE}.sha256" && -s "${BACKUP_FILE}.meta" ]] \
  || fail_with_diagnostics "Backup artifact/checksum/metadata was not created."

grep -q 'Authentication__ApiKeyPepper' "${BACKUP_FILE}.meta" \
  || fail_with_diagnostics "Backup metadata did not warn about external pepper preservation."

# Destroy the complete source database volume and recreate only a clean PostgreSQL target.
"${COMPOSE[@]}" down -v --remove-orphans >/dev/null
if docker volume inspect llmproxy-backup-restore_postgres-data >/dev/null 2>&1; then
  fail_with_diagnostics "Source PostgreSQL volume still existed after destructive reset."
fi
"${COMPOSE[@]}" up -d postgres
for attempt in {1..40}; do
  if "${COMPOSE[@]}" exec -T postgres pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB" >/dev/null 2>&1; then
    break
  fi
  sleep 1
done

# Prove the target really is clean before invoking restore.
clean_schema="$(psql_scalar "SELECT COALESCE(to_regclass('public.api_credentials')::text, '');")"
[[ -z "$clean_schema" ]] || fail_with_diagnostics "Restore target was not clean before restore."

COMPOSE_FILE="$COMPOSE_FILE" RESTORE_START_GATEWAY=true \
  bash docker/scripts/postgres-restore.sh "$BACKUP_FILE" --confirm-destructive

wait_http http://127.0.0.1:8084/readyz 60 || fail_with_diagnostics "Restored gateway did not become ready."
wait_node_healthy 8084 || fail_with_diagnostics "Restored route catalog did not recover healthy DGX state."

restored_status="$(call_model 8084 "$credential_secret" /tmp/backup-restore-restored)"
[[ "$restored_status" == "200" ]] || fail_with_diagnostics "Preserved API credential failed after clean-target restore; got ${restored_status}."

restored_credentials="$(curl --fail --silent http://127.0.0.1:8084/api/admin/governance/credentials)"
echo "$restored_credentials" | jq -e --arg id "$credential_id" --arg group "$group_id" --arg prefix "$credential_prefix" \
  'map(select(.id == $id and .usageGroupId == $group and .keyPrefix == $prefix and .enabled == true)) | length == 1' >/dev/null \
  || fail_with_diagnostics "Credential identity/group/prefix did not survive restore."

restored_policies="$(curl --fail --silent http://127.0.0.1:8084/api/admin/rate-limits)"
echo "$restored_policies" | jq -e --arg id "$policy_id" --arg credential "$credential_id" \
  'map(select(.id == $id and .apiCredentialId == $credential and .requestsPerWindow == 20 and .windowSeconds == 120 and .outputTokensPerWindow == 1000 and .maxOutputTokensPerRequest == 100 and .enabled == true)) | length == 1' >/dev/null \
  || fail_with_diagnostics "Request/token governance policy did not survive restore."

restored_group_count="$(psql_scalar 'SELECT COUNT(*) FROM usage_groups;')"
restored_policy_count="$(psql_scalar 'SELECT COUNT(*) FROM rate_limit_policies;')"
restored_audit_count="$(psql_scalar 'SELECT COUNT(*) FROM audit_events;')"
[[ "$restored_group_count" == "$source_group_count" && "$restored_policy_count" == "$source_policy_count" && "$restored_audit_count" -ge "$source_audit_count" ]] \
  || fail_with_diagnostics "Durable row counts did not survive the clean-target restore."

# Raw secrets must not exist in durable DB state. The stored credential value must be the HMAC.
stored_hash="$(psql_scalar "SELECT \"KeyHash\" FROM api_credentials WHERE \"Id\" = '${credential_id}'::uuid;")"
[[ -n "$stored_hash" && "$stored_hash" != "$credential_secret" ]] || fail_with_diagnostics "Credential durable state contained an invalid/raw secret value."

# Now attach a completely clean Redis + second gateway to the restored DB. Startup must
# rebuild and publish runtime snapshots from PostgreSQL without relying on old Redis data.
docker run -d --name "$REDIS_NAME" --network llmproxy-backup-restore_default \
  redis:8.2-alpine redis-server --appendonly no --requirepass restore-redis >/dev/null
for attempt in {1..30}; do
  if docker exec "$REDIS_NAME" redis-cli -a restore-redis ping 2>/dev/null | grep -q PONG; then
    break
  fi
  sleep 1
done

if ! docker run -d --name "$PEER_NAME" \
  --network llmproxy-backup-restore_default \
  --add-host host.docker.internal:host-gateway \
  -p 127.0.0.1:8085:8080 \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e "ConnectionStrings__Postgres=Host=postgres;Port=5432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}" \
  -e "Authentication__ApiKey=${LLM_PROXY_API_KEY}" \
  -e "Authentication__ApiKeyPepper=${LLM_PROXY_API_KEY_PEPPER}" \
  -e Redis__Enabled=true \
  -e "Redis__ConnectionString=${REDIS_NAME}:6379,password=restore-redis,abortConnect=false" \
  -e Redis__KeyPrefix=restoretest \
  -e Redis__InstanceId=restore-peer \
  -e Redis__ReconcileSeconds=1 \
  -e Redis__OutboxBatchSize=20 \
  -e Redis__OutboxPollMilliseconds=200 \
  -e OpenTelemetry__Enabled=false \
  -e EntraId__Enabled=false \
  -e Bootstrap__Enabled=false \
  -e Routing__Strategy=WeightedLeastLoaded \
  -e Health__IntervalSeconds=1 \
  -e Health__HealthyAfterSuccesses=1 \
  -e Health__UnhealthyAfterFailures=2 \
  -e RuntimeMetrics__Enabled=false \
  -e HardwareMetrics__Enabled=false \
  -e Retention__Enabled=false \
  llmproxy:ci >/dev/null; then
  fail_with_diagnostics "Redis-enabled restored peer failed to start."
fi

wait_http http://127.0.0.1:8085/readyz 60 || fail_with_diagnostics "Redis-enabled restored peer did not become ready."
wait_node_healthy 8085 || fail_with_diagnostics "Redis-enabled restored peer did not rebuild route catalog."
peer_status="$(call_model 8085 "$credential_secret" /tmp/backup-restore-peer)"
[[ "$peer_status" == "200" ]] || fail_with_diagnostics "Restored credential failed on Redis-enabled peer; got ${peer_status}."

runtime_ready=false
for attempt in {1..40}; do
  credential_count="$(docker exec "$REDIS_NAME" redis-cli -a restore-redis HLEN restoretest:credentials 2>/dev/null | tr -d '\r')"
  node_count="$(docker exec "$REDIS_NAME" redis-cli -a restore-redis HLEN restoretest:route:nodes 2>/dev/null | tr -d '\r')"
  policy_count="$(docker exec "$REDIS_NAME" redis-cli -a restore-redis HLEN restoretest:rate-policies 2>/dev/null | tr -d '\r')"
  if [[ "$credential_count" -ge 2 && "$node_count" -ge 1 && "$policy_count" -ge 1 ]]; then
    runtime_ready=true
    break
  fi
  sleep 1
done
[[ "$runtime_ready" == "true" ]] || fail_with_diagnostics "Restored gateway did not republish credential/route/policy snapshots into clean Redis."

echo "Backup/restore smoke passed: custom dump + checksum, destructive clean-target restore, credential/governance/history recovery, authenticated inference and clean-Redis runtime republish verified."
