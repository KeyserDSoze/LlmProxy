#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE=(docker compose -f docker/docker-compose.yml)
MOCK_PID=""

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
  "${COMPOSE[@]}" logs --no-color >&2 || true
  [[ -f /tmp/llmproxy-route-catalog-mock.log ]] && cat /tmp/llmproxy-route-catalog-mock.log >&2 || true
  exit 1
}

wait_ready() {
  for attempt in {1..30}; do
    if curl --fail --silent http://127.0.0.1:8080/readyz >/dev/null; then
      return 0
    fi
    sleep 2
  done
  fail_with_diagnostics "Gateway did not become ready for route-catalog smoke test."
}

python3 tests/backend/integration/mock_llm.py --port 3480 --prefix /route-catalog --name route-catalog > /tmp/llmproxy-route-catalog-mock.log 2>&1 &
MOCK_PID="$!"
sleep 1

export LLM_PROXY_API_KEY="route-catalog-test-key"
export LLM_PROXY_API_KEY_PEPPER="route-catalog-test-pepper"
export ENTRA_ENABLED="false"
export BOOTSTRAP_ENABLED="true"
export INFERENCE_NODE_NAME="inference-route-catalog"
export INFERENCE_NODE_BASE_ADDRESS="http://host.docker.internal:3480/route-catalog"
export INFERENCE_NODE_WEIGHT="1"
export INFERENCE_NODE_MAX_CONCURRENCY="4"
export PUBLIC_MODEL_NAME="agic-code-fast"
export PROVIDER_MODEL_NAME="bootstrap-model"
export ROUTING_STRATEGY="WeightedLeastLoaded"
export HEALTH_INTERVAL_SECONDS="60"
export HEALTH_HEALTHY_AFTER_SUCCESSES="1"
export HEALTH_UNHEALTHY_AFTER_FAILURES="2"
export RUNTIME_METRICS_ENABLED="false"
export HARDWARE_METRICS_ENABLED="false"

if ! "${COMPOSE[@]}" up -d --build; then
  fail_with_diagnostics "Docker Compose stack failed to start for route-catalog smoke test."
fi
wait_ready

healthy=false
for attempt in {1..30}; do
  nodes_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes)"
  if echo "$nodes_json" | jq -e 'map(select(.name == "inference-route-catalog" and .status == "Healthy")) | length == 1' >/dev/null; then
    healthy=true
    break
  fi
  sleep 1
done
[[ "$healthy" == "true" ]] || fail_with_diagnostics "Bootstrap node did not become Healthy before PostgreSQL outage test."

node_id="$(echo "$nodes_json" | jq -r 'map(select(.name == "inference-route-catalog"))[0].id')"
node_base_address="$(echo "$nodes_json" | jq -r 'map(select(.name == "inference-route-catalog"))[0].baseAddress')"
catalog_before="$(curl --fail --silent http://127.0.0.1:8080/api/admin/routing/catalog)"
version_before="$(echo "$catalog_before" | jq -r '.version')"
echo "$catalog_before" | jq -e '.provider == "in-memory" and .version >= 1 and .nodeCount == 1 and .modelCount == 1 and .deploymentCount == 1' >/dev/null

# Mutate durable configuration after startup. The successful SaveChanges must publish
# the new node snapshot immediately into the route catalog before the admin call returns.
curl --fail --silent -X PUT -H 'Content-Type: application/json' \
  -d "{\"name\":\"inference-route-catalog\",\"baseAddress\":\"${node_base_address}\",\"weight\":2,\"maxConcurrency\":4}" \
  "http://127.0.0.1:8080/api/admin/nodes/${node_id}" >/dev/null
catalog_after_mutation="$(curl --fail --silent http://127.0.0.1:8080/api/admin/routing/catalog)"
echo "$catalog_after_mutation" | jq -e --argjson previous "$version_before" '.version > $previous and .nodeCount == 1 and .modelCount == 1 and .deploymentCount == 1' >/dev/null

# Stop the durable store after startup. /readyz is expected to become unavailable,
# but already-published inference authentication + route resolution must keep working.
"${COMPOSE[@]}" stop postgres >/dev/null
sleep 1

catalog_during_outage="$(curl --fail --silent http://127.0.0.1:8080/api/admin/routing/catalog)"
echo "$catalog_during_outage" | jq -e '.provider == "in-memory" and .version >= 1 and .nodeCount == 1 and .modelCount == 1 and .deploymentCount == 1' >/dev/null

models_status="$(curl --silent --output /tmp/route-models.json --write-out '%{http_code}' \
  -H 'Authorization: Bearer route-catalog-test-key' \
  http://127.0.0.1:8080/v1/models)"
[[ "$models_status" == "200" ]] || fail_with_diagnostics "Expected /v1/models to work with PostgreSQL stopped; got HTTP ${models_status}."
jq -e '.data | map(select(.id == "agic-code-fast")) | length == 1' /tmp/route-models.json >/dev/null

chat_status="$(curl --silent --output /tmp/route-chat.json --write-out '%{http_code}' \
  -H 'Authorization: Bearer route-catalog-test-key' \
  -H 'Content-Type: application/json' \
  -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"route catalog postgres outage smoke"}]}' \
  http://127.0.0.1:8080/v1/chat/completions)"
[[ "$chat_status" == "200" ]] || fail_with_diagnostics "Expected chat completion to work with PostgreSQL stopped; got HTTP ${chat_status}."
jq -e '.served_by == "route-catalog" and .model == "bootstrap-model"' /tmp/route-chat.json >/dev/null

echo "Route-catalog smoke suite passed: live publication, catalog diagnostics, /v1/models and chat completion remained available with PostgreSQL stopped after startup."
