#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE=(docker compose -f docker/docker-compose.full.yml)
MOCK_PID=""

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
  "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
  if [[ -n "$MOCK_PID" ]]; then
    kill "$MOCK_PID" >/dev/null 2>&1 || true
    wait "$MOCK_PID" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

fail_with_diagnostics() {
  echo "$1" >&2
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

echo "Full-stack smoke passed: PostgreSQL, Redis runtime sync, explicit application spans, OTLP trace delivery, Tempo, Loki, Prometheus and Grafana are operational."
