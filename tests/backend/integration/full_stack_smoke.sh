#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE=(docker compose -f docker/docker-compose.full.yml)
MOCK_PID=""
PEER_NAME="llmproxy-full-peer"

export POSTGRES_DB=llmproxy
export POSTGRES_USER=llmproxy
export POSTGRES_PASSWORD=full-stack-postgres
export REDIS_PASSWORD=full-stack-redis
export REDIS_KEY_PREFIX=llmproxy
export REDIS_RECONCILE_SECONDS=1
export LLM_PROXY_API_KEY=full-stack-api-key
export LLM_PROXY_API_KEY_PEPPER=full-stack-pepper
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
export DGX_NODE_NAME=dgx-full-stack
export DGX_NODE_BASE_ADDRESS=http://host.docker.internal:3490/full-stack
export DGX_NODE_WEIGHT=1
export DGX_NODE_MAX_CONCURRENCY=4
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
  if echo "$nodes_json" | jq -e 'map(select(.name == "dgx-full-stack" and .status == "Healthy")) | length == 1' >/dev/null 2>&1; then
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

# Prove that rate-limit counters are shared across gateway instances rather than process-local.
credential_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/governance/credentials)"
credential_id="$(echo "$credential_json" | jq -r '.[0].id')"
[[ -n "$credential_id" && "$credential_id" != "null" ]] || fail_with_diagnostics "Bootstrap credential was not available for distributed rate-limit smoke."

policy_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' \
  -d "{\"apiCredentialId\":\"${credential_id}\",\"logicalModel\":\"agic-code-fast\",\"requestsPerWindow\":2,\"windowSeconds\":60,\"enabled\":true}" \
  http://127.0.0.1:8080/api/admin/rate-limits)"
echo "$policy_json" | jq -e '.requestsPerWindow == 2 and .windowSeconds == 60 and .enabled == true' >/dev/null

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
  -e Redis__InstanceId=ci-full-2 \
  -e Redis__ReconcileSeconds=1 \
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
  if echo "$peer_nodes" | jq -e 'map(select(.name == "dgx-full-stack" and .status == "Healthy")) | length == 1' >/dev/null 2>&1; then
    peer_healthy=true
    break
  fi
  sleep 1
done
[[ "$peer_healthy" == "true" ]] || fail_with_diagnostics "Second gateway did not observe the shared inference node as Healthy."

shared1="$(call_model 8080 /tmp/full-stack-rate-a)"
shared2="$(call_model 8081 /tmp/full-stack-rate-b)"
shared3="$(call_model 8080 /tmp/full-stack-rate-c)"
[[ "$shared1" == "200" && "$shared2" == "200" ]] || fail_with_diagnostics "Expected first two globally governed requests to succeed; got ${shared1}/${shared2}."
[[ "$shared3" == "429" ]] || fail_with_diagnostics "Expected third request across two gateways to hit the shared Redis rate limit; got ${shared3}."
jq -e '.error.type == "rate_limit_error" and .error.code == "rate_limit_exceeded"' /tmp/full-stack-rate-c.json >/dev/null

global_counter_keys="$("${COMPOSE[@]}" exec -T redis redis-cli -a "$REDIS_PASSWORD" --scan --pattern 'llmproxy:rate-limit:*' 2>/dev/null | wc -l | tr -d ' ')"
[[ "$global_counter_keys" -ge 1 ]] || fail_with_diagnostics "Expected a Redis-backed shared rate-limit counter key."

echo "Full-stack smoke passed: PostgreSQL, Redis runtime sync, explicit application spans, OTLP observability, Grafana and cross-gateway Redis rate limiting are operational."
