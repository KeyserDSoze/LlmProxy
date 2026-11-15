#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE=(docker compose -f docker/docker-compose.yml)
MOCK_PID=""
BASE="http://127.0.0.1:${LLMPROXY_PORT:-8080}"
MOCK_PORT="${ROUTE_CATALOG_MOCK_PORT:-3480}"

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
    if curl --fail --silent "$BASE/readyz" >/dev/null; then
      return 0
    fi
    sleep 2
  done
  fail_with_diagnostics "Gateway did not become ready for route-catalog smoke test."
}

python3 tests/backend/integration/mock_llm.py --port "$MOCK_PORT" --prefix /route-catalog --name route-catalog > /tmp/llmproxy-route-catalog-mock.log 2>&1 &
MOCK_PID="$!"
sleep 1

export LLM_PROXY_API_KEY="route-catalog-test-key"
export LLM_PROXY_API_KEY_PEPPER="route-catalog-test-pepper"
export ENTRA_ENABLED="false"
export BOOTSTRAP_ENABLED="true"
export INFERENCE_NODE_NAME="inference-route-catalog"
export INFERENCE_NODE_BASE_ADDRESS="http://host.docker.internal:$MOCK_PORT/route-catalog"
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

build_args=(--build)
if [[ "${LLMPROXY_SMOKE_SKIP_BUILD:-0}" == "1" ]]; then build_args=(); fi
if ! "${COMPOSE[@]}" up -d "${build_args[@]}"; then
  fail_with_diagnostics "Docker Compose stack failed to start for route-catalog smoke test."
fi
wait_ready

healthy=false
for attempt in {1..30}; do
  nodes_json="$(curl --fail --silent "$BASE/api/admin/nodes")"
  if echo "$nodes_json" | jq -e 'map(select(.name == "inference-route-catalog" and .status == "Healthy")) | length == 1' >/dev/null; then
    healthy=true
    break
  fi
  sleep 1
done
[[ "$healthy" == "true" ]] || fail_with_diagnostics "Bootstrap node did not become Healthy before PostgreSQL outage test."

node_id="$(echo "$nodes_json" | jq -r 'map(select(.name == "inference-route-catalog"))[0].id')"
node_base_address="$(echo "$nodes_json" | jq -r 'map(select(.name == "inference-route-catalog"))[0].baseAddress')"
catalog_before="$(curl --fail --silent "$BASE/api/admin/routing/catalog")"
version_before="$(echo "$catalog_before" | jq -r '.version')"
echo "$catalog_before" | jq -e '.provider == "in-memory" and .version >= 1 and .nodeCount == 1 and .modelCount == 1 and .deploymentCount == 1' >/dev/null

# Mutate durable configuration after startup. The successful SaveChanges must publish
# the new node snapshot immediately into the route catalog before the admin call returns.
curl --fail --silent -X PUT -H 'Content-Type: application/json' \
  -d "{\"name\":\"inference-route-catalog\",\"baseAddress\":\"${node_base_address}\",\"weight\":2,\"maxConcurrency\":4}" \
  "$BASE/api/admin/nodes/${node_id}" >/dev/null
catalog_after_mutation="$(curl --fail --silent "$BASE/api/admin/routing/catalog")"
echo "$catalog_after_mutation" | jq -e --argjson previous "$version_before" '.version > $previous and .nodeCount == 1 and .modelCount == 1 and .deploymentCount == 1' >/dev/null

# Rename an active model in place: PostgreSQL identity, runtime and live L1 survive.
model_id="$(curl -fsS "$BASE/api/admin/models" | jq -r '.[0].id')"
curl -fsS -X PUT -H 'Content-Type: application/json' -d '{"publicName":"renamed-code"}' "$BASE/api/admin/models/$model_id/name" | jq -e --arg id "$model_id" '.id == $id and .providerModelName == "bootstrap-model"' >/dev/null
curl -fsS -H 'Authorization: Bearer route-catalog-test-key' "$BASE/v1/models" | jq -e '.data | any(.id == "renamed-code")' >/dev/null
curl -fsS -X PUT -H 'Content-Type: application/json' -d '{"publicName":"agic-code-fast"}' "$BASE/api/admin/models/$model_id/name" >/dev/null

# A gateway restart has no open Admin page to finish activation. Persist wanted-but-not-ready state,
# then let the worker verify the Agent + /health and restore it after process replacement.
"${COMPOSE[@]}" exec -T postgres psql -U llmproxy -d llmproxy -v ON_ERROR_STOP=1 -c \
  "UPDATE nodes SET \"ManagementBaseAddress\" = '${INFERENCE_NODE_BASE_ADDRESS}/publishing-management' WHERE \"Id\" = '$node_id'; UPDATE deployments SET \"ManagedInstallationId\" = 'publishing-smoke', \"RoutingDesired\" = true, \"Enabled\" = false, \"ActivationStartJobId\" = NULL;" >/dev/null
"${COMPOSE[@]}" restart llmproxy >/dev/null
wait_ready
restored=false
for attempt in {1..30}; do
  if curl -fsS "$BASE/api/admin/deployments" | jq -e '.[0].enabled and .[0].routingDesired' >/dev/null; then restored=true; break; fi
  sleep 1
done
[[ "$restored" == true ]] || fail_with_diagnostics "Requested routing was not recovered after gateway restart."
deployment_id="$(curl -fsS "$BASE/api/admin/deployments" | jq -r '.[0].id')"
curl -fsS -X PUT -H 'Content-Type: application/json' -d '{"weight":1,"enabled":false}' "$BASE/api/admin/deployments/$deployment_id" >/dev/null
"${COMPOSE[@]}" restart llmproxy >/dev/null
wait_ready
sleep 12
curl -fsS "$BASE/api/admin/deployments" | jq -e '.[0].enabled == false and .[0].routingDesired == false' >/dev/null || fail_with_diagnostics "Recovery undid an explicit operator disable."
start_job_id="$(curl -fsS -X POST "$BASE/api/admin/model-management/deployments/$deployment_id/start-jobs" | jq -r '.id')"
curl -fsS "$BASE/api/admin/deployments" | jq -e --arg id "$start_job_id" '.[0].routingDesired and (.[0].activationStartJobId == $id or .[0].enabled)' >/dev/null
"${COMPOSE[@]}" restart llmproxy >/dev/null
wait_ready
restored=false
for attempt in {1..30}; do
  if curl -fsS "$BASE/api/admin/deployments" | jq -e '.[0].enabled and .[0].activationStartJobId == null' >/dev/null; then restored=true; break; fi
  sleep 1
done
[[ "$restored" == true ]] || fail_with_diagnostics "Accepted start-job intent was not recovered after gateway restart with clock skew."

# Capacity: raise the context of a running model. The gateway restarts it through the Agent (a mock here), keeps routing off while
# it reloads, gives it back only when the job it asked for has finished, and the context clients read follows the runtime.
limit_status="$(curl -sS -o /tmp/route-limits.json -w '%{http_code}' -X PUT -H 'Content-Type: application/json' \
  -d '{"maxModelLen":16384,"maxNumSeqs":1,"maxOutputTokens":null,"applyNow":true,"syncPublishedContext":true}' \
  "$BASE/api/admin/model-management/deployments/$deployment_id/configuration")"
[[ "$limit_status" == "202" ]] || fail_with_diagnostics "Applying new limits should start a background operation; got HTTP ${limit_status}: $(cat /tmp/route-limits.json)"
applied=false
for attempt in {1..40}; do
  if curl -fsS "$BASE/api/admin/operations" | jq -e 'map(select(.kind == "model.limits")) | length == 1 and .[0].status == "Succeeded"' >/dev/null; then applied=true; break; fi
  curl -fsS "$BASE/api/admin/operations" | jq -e 'map(select(.kind == "model.limits" and .status == "Failed")) | length == 0' >/dev/null \
    || fail_with_diagnostics "The capacity operation failed: $(curl -fsS "$BASE/api/admin/operations")"
  sleep 1
done
[[ "$applied" == true ]] || fail_with_diagnostics "The capacity operation did not finish."
curl -fsS "$BASE/api/admin/deployments" | jq -e '.[0].enabled and .[0].routingDesired and .[0].activationStartJobId == null' >/dev/null \
  || fail_with_diagnostics "Routing was not given back after the capacity change."
curl -fsS "$BASE/api/admin/models" | jq -e '.[0].contextWindowTokens == 16384' >/dev/null \
  || fail_with_diagnostics "The published context did not follow the new runtime limits."
curl -fsS "$BASE/api/admin/audit?take=50" | jq -e 'any(.[]; .action == "model.runtime.configure")' >/dev/null \
  || fail_with_diagnostics "The capacity change was not audited."

# Stop the durable store after startup. /readyz is expected to become unavailable,
# but already-published inference authentication + route resolution must keep working.
"${COMPOSE[@]}" stop postgres >/dev/null
sleep 1

catalog_during_outage="$(curl --fail --silent "$BASE/api/admin/routing/catalog")"
echo "$catalog_during_outage" | jq -e '.provider == "in-memory" and .version >= 1 and .nodeCount == 1 and .modelCount == 1 and .deploymentCount == 1' >/dev/null

models_status="$(curl --silent --output /tmp/route-models.json --write-out '%{http_code}' \
  -H 'Authorization: Bearer route-catalog-test-key' \
  "$BASE/v1/models")"
[[ "$models_status" == "200" ]] || fail_with_diagnostics "Expected /v1/models to work with PostgreSQL stopped; got HTTP ${models_status}."
jq -e '.data | map(select(.id == "agic-code-fast")) | length == 1' /tmp/route-models.json >/dev/null

chat_status="$(curl --silent --output /tmp/route-chat.json --write-out '%{http_code}' \
  -H 'Authorization: Bearer route-catalog-test-key' \
  -H 'Content-Type: application/json' \
  -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"route catalog postgres outage smoke"}]}' \
  "$BASE/v1/chat/completions")"
[[ "$chat_status" == "200" ]] || fail_with_diagnostics "Expected chat completion to work with PostgreSQL stopped; got HTTP ${chat_status}."
jq -e '.served_by == "route-catalog" and .model == "bootstrap-model"' /tmp/route-chat.json >/dev/null

echo "Route-catalog smoke suite passed: live publication, catalog diagnostics, /v1/models and chat completion remained available with PostgreSQL stopped after startup."
