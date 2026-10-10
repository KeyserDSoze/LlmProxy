#!/usr/bin/env bash
set -Eeuo pipefail
# Only the failing source line/exit code is logged; command strings and API secrets are not.
trap 'rc=$?; printf "[full-stack] failed at script line %s (exit %s)\\n" "$LINENO" "$rc" >&2' ERR

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE=(docker compose -f docker/docker-compose.full.yml)
MOCK_PID=""
REMOTE_AGENT_PID=""
REMOTE_AGENT_STATE=/tmp/llmproxy-remote-agent-state.json
PEER_NAME="llmproxy-full-peer"

export POSTGRES_DB=llmproxy
export POSTGRES_USER=llmproxy
export POSTGRES_PASSWORD=full-stack-postgres
export REDIS_PASSWORD=full-stack-redis
export REDIS_KEY_PREFIX=llmproxy
export REDIS_RECONCILE_SECONDS=1
export REDIS_CAPACITY_LEASE_SECONDS=20
export REDIS_CAPACITY_RENEW_SECONDS=4
export LLM_PROXY_API_KEY=full-stack-api-key
export LLM_PROXY_API_KEY_PEPPER=full-stack-pepper
export LLMPROXY_UPSTREAM_CREDENTIAL_KEY=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
export GRAFANA_ADMIN_USER=admin
export GRAFANA_ADMIN_PASSWORD=full-stack-grafana
export GHCR_OWNER=keyserdsoze
export LLMPROXY_IMAGE_TAG=ci-full
export LLMPROXY_PULL_POLICY=never
export LLMPROXY_INSTANCE_ID=ci-full-1
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
export INFERENCE_NODE_NAME=inference-full-stack
export INFERENCE_NODE_BASE_ADDRESS=http://host.docker.internal:3490/full-stack
export INFERENCE_NODE_WEIGHT=1
export INFERENCE_NODE_MAX_CONCURRENCY=1
export PUBLIC_MODEL_NAME=agic-code-fast
export PROVIDER_MODEL_NAME=bootstrap-model

cleanup() {
  docker rm -f "$PEER_NAME" >/dev/null 2>&1 || true
  "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
  if [[ -n "$REMOTE_AGENT_PID" ]]; then
    kill "$REMOTE_AGENT_PID" >/dev/null 2>&1 || true
    wait "$REMOTE_AGENT_PID" >/dev/null 2>&1 || true
  fi
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
  "${COMPOSE[@]}" logs --no-color --tail=200 >&2 || true
  [[ -f /tmp/llmproxy-full-stack-mock.log ]] && cat /tmp/llmproxy-full-stack-mock.log >&2 || true
  exit 1
}

wait_http() {
  local url="$1"
  local attempts="${2:-60}"
  for ((i=1; i<=attempts; i++)); do
    if curl --fail --silent "$url" >/dev/null 2>&1; then
      return 0
    fi
    sleep 2
  done
  return 1
}

call_model() {
  local port="$1"
  local output="$2"
  curl --silent --dump-header "${output}.headers" --output "${output}.json" --write-out '%{http_code}' \
    -H "Authorization: Bearer $LLM_PROXY_API_KEY" \
    -H 'Content-Type: application/json' \
    -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"distributed coordination smoke"}]}' \
    "http://127.0.0.1:${port}/v1/chat/completions"
}

wait_capacity_released() {
  for attempt in {1..50}; do
    local remaining
    remaining="$("${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" --scan --pattern 'llmproxy:capacity:*' 2>/dev/null | wc -l | tr -d ' ')"
    if [[ "$remaining" == "0" ]]; then
      return 0
    fi
    sleep 0.1
  done
  return 1
}

python3 tests/backend/integration/mock_llm.py --port 3490 --prefix /full-stack --name full-stack > /tmp/llmproxy-full-stack-mock.log 2>&1 &
MOCK_PID="$!"
sleep 1

docker build -f docker/Dockerfile -t ghcr.io/keyserdsoze/llmproxy:ci-full .

if ! "${COMPOSE[@]}" up -d; then
  fail_with_diagnostics "Full-stack Compose failed to start."
fi

wait_http http://127.0.0.1:8080/readyz 60 || fail_with_diagnostics "LlmProxy did not become ready."
wait_http http://127.0.0.1:3000/api/health 60 || fail_with_diagnostics "Grafana did not become ready."
wait_http http://127.0.0.1:3200/ready 60 || fail_with_diagnostics "Tempo did not become ready."
wait_http http://127.0.0.1:3100/ready 60 || fail_with_diagnostics "Loki did not become ready."
wait_http http://127.0.0.1:9090/-/ready 60 || fail_with_diagnostics "Prometheus did not become ready."

healthy=false
for attempt in {1..40}; do
  nodes_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes || true)"
  if echo "$nodes_json" | jq -e 'map(select(.name == "inference-full-stack" and .status == "Healthy")) | length == 1' >/dev/null 2>&1; then
    healthy=true
    break
  fi
  sleep 1
done
[[ "$healthy" == "true" ]] || fail_with_diagnostics "Bootstrap inference node did not become Healthy."

sync_ready=false
for attempt in {1..40}; do
  sync_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/runtime-sync || true)"
  if echo "$sync_json" | jq -e '.enabled == true and .provider == "redis-l2+local-l1" and .connected == true and .publishedEvents >= 3' >/dev/null 2>&1; then
    sync_ready=true
    break
  fi
  sleep 1
done
[[ "$sync_ready" == "true" ]] || fail_with_diagnostics "Redis runtime-state synchronization did not become ready."

node_count="$("${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" HLEN llmproxy:route:nodes 2>/dev/null | tr -d '\r')"
credential_count="$("${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" HLEN llmproxy:credentials 2>/dev/null | tr -d '\r')"
[[ "$node_count" -ge 1 ]] || fail_with_diagnostics "Redis route-node snapshot was not published."
[[ "$credential_count" -ge 1 ]] || fail_with_diagnostics "Redis credential snapshot was not published."

curl --silent --show-error \
  --dump-header /tmp/full-stack-headers.txt \
  --output /tmp/full-stack-chat.json \
  -H "Authorization: Bearer $LLM_PROXY_API_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"full stack trace smoke"}]}' \
  http://127.0.0.1:8080/v1/chat/completions

jq -e '.served_by == "full-stack" and .model == "bootstrap-model"' /tmp/full-stack-chat.json >/dev/null
trace_id="$(awk 'BEGIN { IGNORECASE=1 } /^X-LlmProxy-Trace-Id:/ { gsub("\r", "", $2); print $2 }' /tmp/full-stack-headers.txt | tail -n1)"
[[ "$trace_id" =~ ^[0-9a-f]{32}$ ]] || fail_with_diagnostics "Expected a 32-hex X-LlmProxy-Trace-Id header; got '$trace_id'."

expected_spans=(
  "llmproxy.auth"
  "llmproxy.governance.rate_limit"
  "llmproxy.routing.select"
  "llmproxy.capacity.acquire"
)
trace_complete=false
for attempt in {1..30}; do
  if curl --fail --silent "http://127.0.0.1:3200/api/traces/$trace_id" >/tmp/full-stack-trace.json 2>/dev/null; then
    all_spans_present=true
    for span_name in "${expected_spans[@]}"; do
      if ! grep -Fq "$span_name" /tmp/full-stack-trace.json; then
        all_spans_present=false
        break
      fi
    done

    if [[ "$all_spans_present" == "true" ]]; then
      trace_complete=true
      break
    fi
  fi
  sleep 2
done
[[ "$trace_complete" == "true" ]] || fail_with_diagnostics "Trace $trace_id did not arrive in Tempo with all expected LlmProxy application spans."

grafana_sources="$(curl --fail --silent -u "$GRAFANA_ADMIN_USER:$GRAFANA_ADMIN_PASSWORD" http://127.0.0.1:3000/api/datasources)"
echo "$grafana_sources" | jq -e 'map(.name) | (index("Prometheus") != null and index("Tempo") != null and index("Loki") != null)' >/dev/null \
  || fail_with_diagnostics "Grafana datasources were not provisioned."

credential_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/governance/credentials)"
credential_id="$(echo "$credential_json" | jq -r '.[0].id')"
[[ -n "$credential_id" && "$credential_id" != "null" ]] || fail_with_diagnostics "Bootstrap credential was not available for distributed coordination smoke."

# Organization credentials are caller-governance exempt by default. This test intentionally opts
# the bootstrap organization key in before validating shared cross-replica caller limits.
credential_governance="$(curl --fail --silent -X PUT -H 'Content-Type: application/json' \
  -d '{"enabled":true}' \
  "http://127.0.0.1:8080/api/admin/api-credentials/${credential_id}/caller-governance")"
echo "$credential_governance" | jq -e '.kind == "organization" and .enforceCallerGovernance == true' >/dev/null \
  || fail_with_diagnostics "Bootstrap organization credential could not be opted into caller governance."

# Start a second gateway against the same PostgreSQL, Redis and inference node runtime.
if ! docker run -d --name "$PEER_NAME" \
  --network llmproxy-full_default \
  --add-host host.docker.internal:host-gateway \
  -p 127.0.0.1:8081:8080 \
  -e ASPNETCORE_ENVIRONMENT=Development \
  -e "ConnectionStrings__Postgres=Host=postgres;Port=5432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}" \
  -e "Authentication__ApiKey=${LLM_PROXY_API_KEY}" \
  -e "Authentication__ApiKeyPepper=${LLM_PROXY_API_KEY_PEPPER}" \
  -e "Security__UpstreamCredentialEncryptionKey=${LLMPROXY_UPSTREAM_CREDENTIAL_KEY}" \
  -e Redis__Enabled=true \
  -e "Redis__ConnectionString=redis:6379,password=${REDIS_PASSWORD},abortConnect=false" \
  -e "Redis__KeyPrefix=${REDIS_KEY_PREFIX}" \
  -e Redis__InstanceId=ci-full-2 \
  -e Redis__ReconcileSeconds=1 \
  -e Redis__CapacityLeaseSeconds=30 \
  -e Redis__CapacityRenewSeconds=5 \
  -e OpenTelemetry__Enabled=true \
  -e OpenTelemetry__OtlpEndpoint=http://otel-collector:4317 \
  -e OpenTelemetry__ServiceName=llmproxy \
  -e OpenTelemetry__ServiceNamespace=agic.ai \
  -e OpenTelemetry__ServiceInstanceId=ci-full-2 \
  -e OpenTelemetry__Environment=ci \
  -e EntraId__Enabled=false \
  -e Bootstrap__Enabled=false \
  -e Routing__Strategy=WeightedLeastLoaded \
  -e Health__IntervalSeconds=1 \
  -e Health__HealthyAfterSuccesses=1 \
  -e Health__UnhealthyAfterFailures=2 \
  -e RuntimeMetrics__Enabled=false \
  -e HardwareMetrics__Enabled=false \
  -e Retention__Enabled=false \
  ghcr.io/keyserdsoze/llmproxy:ci-full >/dev/null; then
  fail_with_diagnostics "Second LlmProxy gateway failed to start."
fi

wait_http http://127.0.0.1:8081/readyz 60 || fail_with_diagnostics "Second LlmProxy gateway did not become ready."
peer_healthy=false
for attempt in {1..40}; do
  peer_nodes="$(curl --fail --silent http://127.0.0.1:8081/api/admin/nodes || true)"
  if echo "$peer_nodes" | jq -e 'map(select(.name == "inference-full-stack" and .status == "Healthy")) | length == 1' >/dev/null 2>&1; then
    peer_healthy=true
    break
  fi
  sleep 1
done
[[ "$peer_healthy" == "true" ]] || fail_with_diagnostics "Second gateway did not observe the shared inference node as Healthy."

# Rate-limit counter must be shared across both gateways.
peer_sync_before_policy="$(curl --fail --silent http://127.0.0.1:8081/api/admin/runtime-sync)"
peer_version_before_policy="$(echo "$peer_sync_before_policy" | jq -r '.lastAppliedVersion // 0')"

policy_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' \
  -d "{\"apiCredentialId\":\"${credential_id}\",\"logicalModel\":\"agic-code-fast\",\"requestsPerWindow\":2,\"windowSeconds\":60,\"enabled\":true}" \
  http://127.0.0.1:8080/api/admin/rate-limits)"
policy_id="$(echo "$policy_json" | jq -r '.id')"
policy_redis_field="${policy_id//-/}"
echo "$policy_json" | jq -e '.requestsPerWindow == 2 and .windowSeconds == 60 and .enabled == true' >/dev/null

policy_converged=false
for attempt in {1..50}; do
  peer_sync_after_policy="$(curl --fail --silent http://127.0.0.1:8081/api/admin/runtime-sync || true)"
  peer_version_after_policy="$(echo "$peer_sync_after_policy" | jq -r '.lastAppliedVersion // 0' 2>/dev/null || echo 0)"
  redis_policy_present="$("${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" HEXISTS llmproxy:rate-policies "$policy_redis_field" 2>/dev/null | tr -d '\r' || true)"
  if [[ "$peer_version_after_policy" =~ ^[0-9]+$ && "$peer_version_after_policy" -gt "$peer_version_before_policy" && "$redis_policy_present" == "1" ]]; then
    policy_converged=true
    break
  fi
  sleep 0.2
done
[[ "$policy_converged" == "true" ]] || fail_with_diagnostics "Rate-limit policy did not converge to Redis and the peer gateway before shared-counter validation."

shared1="$(call_model 8080 /tmp/full-stack-rate-a)"
[[ "$shared1" == "200" ]] || fail_with_diagnostics "Expected first globally governed request to succeed; got ${shared1}."
wait_capacity_released || fail_with_diagnostics "Capacity lease from first rate-limit probe did not release."

shared2="$(call_model 8081 /tmp/full-stack-rate-b)"
[[ "$shared2" == "200" ]] || fail_with_diagnostics "Expected second globally governed request to succeed; got ${shared2}."
wait_capacity_released || fail_with_diagnostics "Capacity lease from second rate-limit probe did not release."

shared3="$(call_model 8080 /tmp/full-stack-rate-c)"
[[ "$shared3" == "429" ]] || fail_with_diagnostics "Expected third request across two gateways to hit the shared Redis rate limit; got ${shared3}."
jq -e '.error.type == "rate_limit_error" and .error.code == "rate_limit_exceeded"' /tmp/full-stack-rate-c.json >/dev/null \
  || fail_with_diagnostics "Expected shared rate limiter to reject the third request before capacity admission."

global_counter_keys="$("${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" --scan --pattern 'llmproxy:rate-limit:*' 2>/dev/null | wc -l | tr -d ' ')"
[[ "$global_counter_keys" -ge 1 ]] || fail_with_diagnostics "Expected a Redis-backed shared rate-limit counter key."

# Remove the policy and restart the peer so the capacity test cannot be masked by caller quota state.
curl --fail --silent -X DELETE "http://127.0.0.1:8080/api/admin/rate-limits/${policy_id}" >/dev/null
docker restart "$PEER_NAME" >/dev/null
wait_http http://127.0.0.1:8081/readyz 60 || fail_with_diagnostics "Second gateway did not recover after rate-policy removal."

# Hold the single physical inference node slot on gateway A. Gateway B must observe the same Redis lease and reject.
curl --silent --no-buffer \
  -H "Authorization: Bearer $LLM_PROXY_API_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"model":"agic-code-fast","stream":true,"messages":[{"role":"user","content":"hold distributed capacity"}]}' \
  http://127.0.0.1:8080/v1/chat/completions > /tmp/full-stack-capacity-stream.txt &
stream_pid="$!"
sleep 0.15

capacity_status="$(call_model 8081 /tmp/full-stack-capacity-b)"
if [[ "$capacity_status" != "429" ]]; then
  wait "$stream_pid" || true
  fail_with_diagnostics "Expected peer gateway to honor the shared inference node capacity lease; got ${capacity_status}."
fi
jq -e '.error.type == "rate_limit_error" and .error.code == "capacity_exhausted"' /tmp/full-stack-capacity-b.json >/dev/null

# While the stream is active, Redis must contain the distributed capacity lease.
capacity_keys="$("${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" --scan --pattern 'llmproxy:capacity:*' 2>/dev/null | wc -l | tr -d ' ')"
[[ "$capacity_keys" -ge 1 ]] || fail_with_diagnostics "Expected Redis-backed capacity lease keys while inference is active."

wait "$stream_pid"
grep --quiet 'data: \[DONE\]' /tmp/full-stack-capacity-stream.txt

recovered_status="$(call_model 8081 /tmp/full-stack-capacity-recovered)"
[[ "$recovered_status" == "200" ]] || fail_with_diagnostics "Expected traffic to recover after shared capacity lease release; got ${recovered_status}."

# The HTTP client can receive the recovered response before the peer finishes lease disposal.
# Wait for Redis to observe the actual release so the fault-injection request starts from a clean capacity state.
capacity_released=false
for attempt in {1..50}; do
  remaining_capacity_keys="$("${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" --scan --pattern 'llmproxy:capacity:*' 2>/dev/null | wc -l | tr -d ' ')"
  if [[ "$remaining_capacity_keys" == "0" ]]; then
    capacity_released=true
    break
  fi
  sleep 0.1
done
[[ "$capacity_released" == "true" ]] || fail_with_diagnostics "Distributed capacity lease was not released before Redis fault injection."

# A real authenticated WSS session on gateway A must be reachable from gateway B via Redis.
# This runs against both Docker gateways and the actual Redis service; the Agent is a
# dependency-free simulator, NOT a real GPU or an on-host Docker daemon.
rm -f "$REMOTE_AGENT_STATE"
python3 tests/backend/integration/mock_outbound_agent.py \
  --gateway http://127.0.0.1:8080 --state-file "$REMOTE_AGENT_STATE" \
  >/tmp/llmproxy-remote-agent.log 2>&1 &
REMOTE_AGENT_PID="$!"
for attempt in {1..30}; do
  if [[ -s "$REMOTE_AGENT_STATE" ]]; then break; fi
  sleep 1
done
[[ -s "$REMOTE_AGENT_STATE" ]] ||
  fail_with_diagnostics "Fake outbound Agent could not pair and connect over WebSocket."
outbound_node_id="$(jq -r .nodeId "$REMOTE_AGENT_STATE")"
outbound_connected=false
for attempt in {1..30}; do
  peers="$(curl --fail --silent http://127.0.0.1:8081/api/admin/node-enrollment/nodes || true)"
  if echo "$peers" | jq -e --arg id "$outbound_node_id" \
    'any(.[]; .nodeId == $id and .tunnelConnected == true)' >/dev/null 2>&1; then
    outbound_connected=true
    break
  fi
  sleep 1
done
[[ "$outbound_connected" == true ]] ||
  fail_with_diagnostics "Second replica cannot discover first replica's authenticated Agent tunnel."

# Pub/sub owner leases are transient during first paired WebSocket handshake.
# Retry brief startup races, but fail definitively if the second API cannot read
# live inventory from the Agent on the first API within this bounded window.
remote_overview_ready=false
for attempt in {1..20}; do
  remote_overview="$(curl --fail --silent \
    "http://127.0.0.1:8081/api/admin/model-management/nodes/$outbound_node_id/overview" || true)"
  if echo "$remote_overview" | jq -e \
    '.agentAvailable == true and .hardware.hostname == "ci-outbound-agent"' >/dev/null 2>&1; then
    remote_overview_ready=true
    break
  fi
  sleep 1
done
[[ "$remote_overview_ready" == true ]] ||
  fail_with_diagnostics "Cross-replica management HTTP failed over encrypted Redis after 20 retries."

catalog_id="$(curl --fail --silent http://127.0.0.1:8081/api/admin/model-management/catalog | jq -r '.[0].id')"
[[ -n "$catalog_id" && "$catalog_id" != null ]] ||
  fail_with_diagnostics "No curated model exists for Agent SSE relay smoke."
# The Agent tunnel has only just been paired and is relayed over Redis.
# Capture the exact HTTP status and a safe error code instead of stopping at
# curl exit 22 with no explanation. Retry only transient relay failures.
installed=""
install_succeeded=false
for attempt in {1..8}; do
  install_status="$(curl --silent --show-error \
    --output /tmp/llmproxy-remote-install.json --write-out '%{http_code}' \
    -X POST -H 'Content-Type: application/json' \
    -d '{"force":true,"maxNumSeqs":2}' \
    "http://127.0.0.1:8081/api/admin/model-management/nodes/$outbound_node_id/models/$catalog_id/install" || true)"
  if [[ "$install_status" == "200" ]] &&
     jq -e '.deployment.id != null' /tmp/llmproxy-remote-install.json >/dev/null 2>&1; then
    installed="$(cat /tmp/llmproxy-remote-install.json)"
    install_succeeded=true
    break
  fi
  # Only log the HTTP status and response error type, not raw upstream
  # responses (which might contain secrets in other scenarios).
  install_error="$(jq -r '.error.code // .error // .title // .status // "unknown_error" | tostring' \
    /tmp/llmproxy-remote-install.json 2>/dev/null | head -c 160 || true)"
  echo "Remote Agent install attempt $attempt: HTTP ${install_status:-unreachable}, reason=${install_error:-invalid_response}" >&2
  case "$install_status" in
    000|502|503|504) [[ "$attempt" -lt 8 ]] && sleep 2 ;;
    *) break ;;
  esac
done
[[ "$install_succeeded" == "true" ]] ||
  fail_with_diagnostics "Cross-replica model installation failed; see HTTP status and error type above."
deployment_id="$(echo "$installed" | jq -r '.deployment.id')"
[[ -n "$deployment_id" && "$deployment_id" != null ]] ||
  fail_with_diagnostics "Cross-replica model installation over Agent relay did not register."

remote_started=false
for attempt in {1..8}; do
  start_status="$(curl --silent --show-error --output /tmp/llmproxy-remote-start.json \
    --write-out '%{http_code}' -X POST \
    "http://127.0.0.1:8081/api/admin/model-management/deployments/$deployment_id/start" || true)"
  if [[ "$start_status" == "200" ]] && \
    jq -e '.state.status == "running"' /tmp/llmproxy-remote-start.json >/dev/null 2>&1; then
    remote_started=true
    break
  fi
  start_error="$(jq -r '.error.code // .title // .error // "no error code"' /tmp/llmproxy-remote-start.json 2>/dev/null || true)"
  echo "Remote Agent start attempt $attempt: HTTP $start_status ($start_error)" >&2
  sleep 1
done
[[ "$remote_started" == true ]] ||
  fail_with_diagnostics "Remote Agent start did not succeed after eight verified attempts."

outbound_healthy=false
for attempt in {1..30}; do
  node_list="$(curl --fail --silent http://127.0.0.1:8081/api/admin/nodes || true)"
  if echo "$node_list" | jq -e --arg id "$outbound_node_id" \
    'any(.[]; .id == $id and .status == "Healthy")' >/dev/null 2>&1; then
    outbound_healthy=true
    break
  fi
  sleep 1
done
[[ "$outbound_healthy" == true ]] ||
  fail_with_diagnostics "Remote managed model failed virtual runtime health routing."

request_payload="$(jq -nc --arg model "$catalog_id" \
  '{model:$model,stream:true,messages:[{role:"user",content:"synthetic Redis relay test"}]}')"
curl --fail --silent --show-error --no-buffer -H "Authorization: Bearer $LLM_PROXY_API_KEY" \
  -H 'Content-Type: application/json' -d "$request_payload" \
  http://127.0.0.1:8081/v1/chat/completions > /tmp/llmproxy-cross-replica-sse.txt ||
  fail_with_diagnostics "Cross-replica Agent SSE inference returned non-200."
grep -Fq 'peer-ok' /tmp/llmproxy-cross-replica-sse.txt ||
  fail_with_diagnostics "Cross-replica streamed response lost its content delta."
grep -Fq 'data: [DONE]' /tmp/llmproxy-cross-replica-sse.txt ||
  fail_with_diagnostics "Cross-replica streamed response did not reach SSE completion."
echo "Authenticated Agent WSS, Redis ownership, remote lifecycle and SSE roundtrip passed."

# If Redis disappears during a long stream, the gateway must abort before its lease can expire and be reused elsewhere.
loss_stream_file=/tmp/full-stack-capacity-coordination-loss.txt
loss_stream_err=/tmp/full-stack-capacity-coordination-loss.err
loss_stream_headers=/tmp/full-stack-capacity-coordination-loss.headers
curl --silent --show-error --no-buffer \
  --dump-header "$loss_stream_headers" \
  -H "Authorization: Bearer $LLM_PROXY_API_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"model":"agic-code-fast","stream":true,"mock_stream_delay_seconds":12,"messages":[{"role":"user","content":"lose Redis coordination before lease expiry"}]}' \
  http://127.0.0.1:8080/v1/chat/completions > "$loss_stream_file" 2> "$loss_stream_err" &
loss_stream_pid="$!"

first_event_seen=false
for attempt in {1..50}; do
  if grep -q '"content":"first"' "$loss_stream_file" 2>/dev/null; then
    first_event_seen=true
    break
  fi
  sleep 0.1
done
if [[ "$first_event_seen" != "true" ]]; then
  kill "$loss_stream_pid" >/dev/null 2>&1 || true
  wait "$loss_stream_pid" >/dev/null 2>&1 || true
  fail_with_diagnostics "Long-running stream did not begin before Redis outage simulation."
fi

loss_trace_id="$(awk 'BEGIN { IGNORECASE=1 } /^X-LlmProxy-Trace-Id:/ { gsub("\r", "", $2); print $2 }' "$loss_stream_headers" | tail -n1)"
[[ "$loss_trace_id" =~ ^[0-9a-f]{32}$ ]] || fail_with_diagnostics "Expected a trace id for the lease-loss request; got '$loss_trace_id'."

loss_started_epoch="$(date +%s)"
"${COMPOSE[@]}" stop redis >/dev/null
set +e
wait "$loss_stream_pid"
loss_stream_exit="$?"
set -e
loss_elapsed_seconds="$(( $(date +%s) - loss_started_epoch ))"

if grep -q 'data: \[DONE\]' "$loss_stream_file"; then
  fail_with_diagnostics "Inference reached [DONE] after Redis coordination was lost; expected proactive cancellation."
fi
[[ "$loss_elapsed_seconds" -lt "$REDIS_CAPACITY_LEASE_SECONDS" ]] \
  || fail_with_diagnostics "Inference was not cancelled before Redis lease expiry (${loss_elapsed_seconds}s, curl exit ${loss_stream_exit})."

lease_loss_recorded=false
for attempt in {1..30}; do
  lease_loss_count="$("${COMPOSE[@]}" exec -T -e PGPASSWORD="$POSTGRES_PASSWORD" postgres \
    psql -h 127.0.0.1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc \
    "SELECT COUNT(*) FROM request_metrics WHERE \"ErrorCode\" = 'capacity_lease_lost';" 2>/dev/null | tr -d '[:space:]')"
  if [[ "$lease_loss_count" =~ ^[0-9]+$ && "$lease_loss_count" -ge 1 ]]; then
    lease_loss_recorded=true
    break
  fi
  sleep 1
done
[[ "$lease_loss_recorded" == "true" ]] \
  || fail_with_diagnostics "Expected capacity_lease_lost request metric after Redis outage."

lease_loss_trace_recorded=false
for attempt in {1..30}; do
  if curl --fail --silent "http://127.0.0.1:3200/api/traces/$loss_trace_id" >/tmp/full-stack-lease-loss-trace.json 2>/dev/null && \
     grep -Fq 'capacity_lease_lost' /tmp/full-stack-lease-loss-trace.json; then
    lease_loss_trace_recorded=true
    break
  fi
  sleep 1
done
[[ "$lease_loss_trace_recorded" == "true" ]] \
  || fail_with_diagnostics "Expected capacity_lease_lost to be visible in Tempo trace $loss_trace_id."

echo "Full-stack smoke passed: PostgreSQL, Redis runtime sync, explicit application spans, OTLP observability, cross-gateway rate limits, distributed inference node capacity leases and lease-loss cancellation are operational."
