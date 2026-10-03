#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE=(docker compose -f docker/docker-compose.full.yml)
PEER_NAME="llmproxy-token-budget-peer"
MOCK_PID=""

export POSTGRES_DB=llmproxy
export POSTGRES_USER=llmproxy
export POSTGRES_PASSWORD=token-budget-postgres
export REDIS_PASSWORD=token-budget-redis
export REDIS_KEY_PREFIX=llmproxy
export REDIS_RECONCILE_SECONDS=1
export REDIS_CAPACITY_LEASE_SECONDS=30
export REDIS_CAPACITY_RENEW_SECONDS=5
export LLM_PROXY_API_KEY=token-budget-api-key
export LLM_PROXY_API_KEY_PEPPER=token-budget-pepper
export GRAFANA_ADMIN_USER=admin
export GRAFANA_ADMIN_PASSWORD=token-budget-grafana
export GHCR_OWNER=keyserdsoze
export LLMPROXY_IMAGE_TAG=ci-full
export LLMPROXY_PULL_POLICY=never
export LLMPROXY_INSTANCE_ID=ci-token-budget-1
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
export INFERENCE_NODE_NAME=inference-token-budget
export INFERENCE_NODE_BASE_ADDRESS=http://host.docker.internal:3492/token-budget
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
  [[ -f /tmp/llmproxy-token-budget-mock.log ]] && cat /tmp/llmproxy-token-budget-mock.log >&2 || true
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

call_model_with_key() {
  local port="$1"
  local api_key="$2"
  local output="$3"
  curl --silent --dump-header "${output}.headers" --output "${output}.json" --write-out '%{http_code}' \
    -H "Authorization: Bearer ${api_key}" \
    -H 'Content-Type: application/json' \
    -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"distributed user quota smoke"}]}' \
    "http://127.0.0.1:${port}/v1/chat/completions"
}

call_budget_model() {
  local port="$1"
  local output="$2"
  curl --silent --dump-header "${output}.headers" --output "${output}.json" --write-out '%{http_code}' \
    -H "Authorization: Bearer $LLM_PROXY_API_KEY" \
    -H 'Content-Type: application/json' \
    -d '{"model":"agic-code-fast","max_tokens":10,"messages":[{"role":"user","content":"distributed output token budget smoke"}]}' \
    "http://127.0.0.1:${port}/v1/chat/completions"
}

python3 tests/backend/integration/mock_llm.py --port 3492 --prefix /token-budget --name token-budget > /tmp/llmproxy-token-budget-mock.log 2>&1 &
MOCK_PID="$!"
sleep 1

if ! docker image inspect ghcr.io/keyserdsoze/llmproxy:ci-full >/dev/null 2>&1; then
  docker build -f docker/Dockerfile -t ghcr.io/keyserdsoze/llmproxy:ci-full .
fi

if ! "${COMPOSE[@]}" up -d; then
  fail_with_diagnostics "Token-budget smoke Compose failed to start."
fi

wait_http http://127.0.0.1:8080/readyz 60 || fail_with_diagnostics "Primary gateway did not become ready."

healthy=false
for attempt in {1..40}; do
  nodes_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes || true)"
  if echo "$nodes_json" | jq -e 'map(select(.name == "inference-token-budget" and .status == "Healthy")) | length == 1' >/dev/null 2>&1; then
    healthy=true
    break
  fi
  sleep 1
done
[[ "$healthy" == "true" ]] || fail_with_diagnostics "Primary token-budget node did not become Healthy."

credential_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/governance/credentials)"
credential_id="$(echo "$credential_json" | jq -r '.[0].id')"
[[ -n "$credential_id" && "$credential_id" != "null" ]] || fail_with_diagnostics "Bootstrap credential was not available."

# Prepare two personal credentials with one shared Entra identity. Direct SQL is test-only setup;
# restarting the primary rebuilds its credential L1 with ownership metadata before the peer starts.
user_key_a_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' -d '{"name":"Redis user key A"}' http://127.0.0.1:8080/api/admin/api-credentials)"
user_key_b_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' -d '{"name":"Redis user key B"}' http://127.0.0.1:8080/api/admin/api-credentials)"
user_key_a_id="$(echo "$user_key_a_json" | jq -r '.id')"
user_key_b_id="$(echo "$user_key_b_json" | jq -r '.id')"
user_key_a_secret="$(echo "$user_key_a_json" | jq -r '.secret')"
user_key_b_secret="$(echo "$user_key_b_json" | jq -r '.secret')"

"${COMPOSE[@]}" exec -T postgres psql \
  -U "$POSTGRES_USER" \
  -d "$POSTGRES_DB" \
  -v ON_ERROR_STOP=1 \
  -c "UPDATE api_credentials SET \"OwnerTenantId\"='tenant-redis', \"OwnerObjectId\"='user-redis', \"OwnerPrincipalName\"='redis-user@example.com' WHERE \"Id\" IN ('${user_key_a_id}', '${user_key_b_id}');" >/dev/null

"${COMPOSE[@]}" restart llmproxy >/dev/null
wait_http http://127.0.0.1:8080/readyz 60 || fail_with_diagnostics "Primary gateway did not become ready after user-ownership test setup."

# Start a peer before the quota policy exists so the test covers live runtime propagation, not only startup loading.
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
  -e Redis__InstanceId=ci-token-budget-2 \
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
  fail_with_diagnostics "Token-budget peer failed to start."
fi

wait_http http://127.0.0.1:8081/readyz 60 || fail_with_diagnostics "Token-budget peer did not become ready."

peer_healthy=false
for attempt in {1..40}; do
  peer_nodes="$(curl --fail --silent http://127.0.0.1:8081/api/admin/nodes || true)"
  if echo "$peer_nodes" | jq -e 'map(select(.name == "inference-token-budget" and .status == "Healthy")) | length == 1' >/dev/null 2>&1; then
    peer_healthy=true
    break
  fi
  sleep 1
done
[[ "$peer_healthy" == "true" ]] || fail_with_diagnostics "Token-budget peer did not observe the shared node as Healthy."

user_policy_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' \
  -d '{"ownerTenantId":"tenant-redis","ownerObjectId":"user-redis","logicalModel":"agic-code-fast","requestsPerWindow":2,"windowSeconds":60,"enabled":true}' \
  http://127.0.0.1:8080/api/admin/user-rate-limits)"
printf '%s\n' "$user_policy_json" > /tmp/token-budget-user-policy.json
user_policy_id="$(echo "$user_policy_json" | jq -r '.id')"
[[ "$user_policy_id" =~ ^[0-9a-fA-F-]{36}$ ]] || fail_with_diagnostics "User quota policy was not created."

# Prove the peer has applied the live user-policy event before starting the shared-counter assertion.
# The previous smoke assumed propagation had completed by the first peer request, which made this
# test timing-sensitive: an early 200 could bypass the peer's L1 policy and leave the shared count at 1.
user_policy_field="${user_policy_id//-/}"
user_rate_key="llmproxy:rate-limit:${user_policy_field}:2:60"
peer_user_policy_applied=false
for attempt in {1..30}; do
  peer_probe_status="$(call_model_with_key 8081 "$user_key_b_secret" /tmp/token-budget-user-probe)"
  if [[ "$peer_probe_status" == "429" ]]; then
    jq -e '.error.code == "rate_limit_exceeded"' /tmp/token-budget-user-probe.json >/dev/null
    peer_user_policy_applied=true
    break
  fi
  [[ "$peer_probe_status" == "200" ]] || fail_with_diagnostics "Unexpected peer user-policy probe status: ${peer_probe_status}."
  sleep 0.2
done
[[ "$peer_user_policy_applied" == "true" ]] || fail_with_diagnostics "Peer did not apply the live aggregate user quota policy."

# The convergence probe intentionally consumes the test policy after it becomes active. Reset only this
# isolated Redis test counter so the real two-gateway assertion begins from a deterministic empty window.
"${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" DEL "$user_rate_key" >/dev/null 2>&1

user_first="$(call_model_with_key 8080 "$user_key_a_secret" /tmp/token-budget-user-a)"
[[ "$user_first" == "200" ]] || fail_with_diagnostics "Expected first user-quota request on primary to succeed; got ${user_first}."
user_second="$(call_model_with_key 8081 "$user_key_b_secret" /tmp/token-budget-user-b)"
[[ "$user_second" == "200" ]] || fail_with_diagnostics "Expected second user-quota request on peer to succeed; got ${user_second}."
user_third="$(call_model_with_key 8080 "$user_key_a_secret" /tmp/token-budget-user-c)"
[[ "$user_third" == "429" ]] || fail_with_diagnostics "Expected third cross-gateway request to exceed aggregate user quota; got ${user_third}."
jq -e '.error.code == "rate_limit_exceeded"' /tmp/token-budget-user-c.json >/dev/null

user_count="$("${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" HGET "$user_rate_key" count 2>/dev/null | tr -d '\r')"
[[ "$user_count" == "2" ]] || fail_with_diagnostics "Expected shared Redis user rate-limit count=2 after rejection; got ${user_count}."

policy_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' \
  -d "{\"apiCredentialId\":\"${credential_id}\",\"logicalModel\":\"agic-code-fast\",\"requestsPerWindow\":100,\"windowSeconds\":300,\"enabled\":true,\"outputTokensPerWindow\":17,\"maxOutputTokensPerRequest\":10}" \
  http://127.0.0.1:8080/api/admin/rate-limits)"
printf '%s\n' "$policy_json" > /tmp/token-budget-policy.json
policy_id="$(echo "$policy_json" | jq -r '.id')"
[[ "$policy_id" =~ ^[0-9a-fA-F-]{36}$ ]] || fail_with_diagnostics "Output-token budget policy was not created."
echo "$policy_json" | jq -e '.requestsPerWindow == 100 and .windowSeconds == 300 and .outputTokensPerWindow == 17 and .maxOutputTokensPerRequest == 10' >/dev/null

# A max_tokens=0 request is accepted by the mock runtime but must be rejected by quota middleware.
# Polling this on the peer proves the new policy reached the peer's local L1 through runtime-state propagation.
peer_policy_applied=false
for attempt in {1..40}; do
  peer_probe_status="$(curl --silent --output /tmp/token-budget-peer-probe.json --write-out '%{http_code}' \
    -H "Authorization: Bearer $LLM_PROXY_API_KEY" \
    -H 'Content-Type: application/json' \
    -d '{"model":"agic-code-fast","max_tokens":0,"messages":[]}' \
    http://127.0.0.1:8081/v1/chat/completions || true)"
  if [[ "$peer_probe_status" == "400" ]] && jq -e '.error.code == "invalid_output_token_limit"' /tmp/token-budget-peer-probe.json >/dev/null 2>&1; then
    peer_policy_applied=true
    break
  fi
  sleep 0.5
done
[[ "$peer_policy_applied" == "true" ]] || fail_with_diagnostics "Peer did not apply output-token budget policy to local L1."

policy_field="${policy_id//-/}"
budget_key="llmproxy:output-token-budget:${policy_field}:17:300"

first="$(call_budget_model 8080 /tmp/token-budget-a)"
[[ "$first" == "200" ]] || fail_with_diagnostics "Expected first budgeted request on primary to succeed; got ${first}."

first_settled=false
for attempt in {1..30}; do
  used="$("${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" HGET "$budget_key" used 2>/dev/null | tr -d '\r')"
  if [[ "$used" == "7" ]]; then
    first_settled=true
    break
  fi
  sleep 0.2
done
[[ "$first_settled" == "true" ]] || fail_with_diagnostics "First reservation did not settle from 10 to actual 7 output tokens in Redis."

second="$(call_budget_model 8081 /tmp/token-budget-b)"
[[ "$second" == "200" ]] || fail_with_diagnostics "Expected second request on peer to use the refunded shared budget; got ${second}."

second_settled=false
for attempt in {1..30}; do
  used="$("${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" HGET "$budget_key" used 2>/dev/null | tr -d '\r')"
  if [[ "$used" == "14" ]]; then
    second_settled=true
    break
  fi
  sleep 0.2
done
[[ "$second_settled" == "true" ]] || fail_with_diagnostics "Cross-gateway settlement did not produce shared Redis usage=14."

third="$(call_budget_model 8080 /tmp/token-budget-c)"
[[ "$third" == "429" ]] || fail_with_diagnostics "Expected third cross-gateway request to exceed the shared output-token budget; got ${third}."
jq -e '.error.type == "rate_limit_error" and .error.code == "token_budget_exceeded"' /tmp/token-budget-c.json >/dev/null
grep -i --quiet '^Retry-After:' /tmp/token-budget-c.headers
used_after_reject="$("${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" HGET "$budget_key" used 2>/dev/null | tr -d '\r')"
[[ "$used_after_reject" == "14" ]] || fail_with_diagnostics "Rejected reservation changed shared Redis usage; expected 14, got ${used_after_reject}."

# Distributed token-budget admission is a hard governance boundary: Redis loss must fail closed.
"${COMPOSE[@]}" stop redis >/dev/null
redis_loss_observed=false
for attempt in {1..30}; do
  sync_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/runtime-sync || true)"
  if echo "$sync_json" | jq -e '.connected == false' >/dev/null 2>&1; then
    redis_loss_observed=true
    break
  fi
  sleep 0.5
done
[[ "$redis_loss_observed" == "true" ]] || fail_with_diagnostics "Primary gateway did not observe Redis outage."

unavailable="$(call_budget_model 8080 /tmp/token-budget-redis-down)"
[[ "$unavailable" == "503" ]] || fail_with_diagnostics "Expected Redis outage to fail token-budget admission closed with 503; got ${unavailable}."
jq -e '.error.type == "gateway_unavailable" and .error.code == "token_budget_coordination_unavailable"' /tmp/token-budget-redis-down.json >/dev/null
grep -i --quiet '^Retry-After:' /tmp/token-budget-redis-down.headers

"${COMPOSE[@]}" start redis >/dev/null
redis_ready=false
for attempt in {1..40}; do
  if "${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" ping 2>/dev/null | grep -q PONG; then
    redis_ready=true
    break
  fi
  sleep 0.5
done
[[ "$redis_ready" == "true" ]] || fail_with_diagnostics "Redis did not recover for token-budget smoke."

reconnected=false
for attempt in {1..40}; do
  sync_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/runtime-sync || true)"
  if echo "$sync_json" | jq -e '.connected == true' >/dev/null 2>&1; then
    reconnected=true
    break
  fi
  sleep 0.5
done
[[ "$reconnected" == "true" ]] || fail_with_diagnostics "Primary gateway did not reconnect after Redis recovery."

recovered="$(call_budget_model 8080 /tmp/token-budget-recovered)"
[[ "$recovered" == "429" ]] || fail_with_diagnostics "Expected persisted shared budget usage to remain exhausted after Redis recovery; got ${recovered}."
jq -e '.error.code == "token_budget_exceeded"' /tmp/token-budget-recovered.json >/dev/null

echo "Distributed governance smoke passed: aggregate user request quota, live policy propagation, output-token reservation/refund settlement, shared cross-gateway Redis usage, fail-closed outage handling, and recovery verified."
