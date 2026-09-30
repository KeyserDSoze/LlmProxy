#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE=(docker compose -f docker/docker-compose.full.yml)
MOCK_PID=""

export POSTGRES_DB=llmproxy
export POSTGRES_USER=llmproxy
export POSTGRES_PASSWORD=upstream-auth-postgres
export REDIS_PASSWORD=upstream-auth-redis
export REDIS_KEY_PREFIX=llmproxy
export LLM_PROXY_API_KEY=upstream-auth-client-key
export LLM_PROXY_API_KEY_PEPPER=upstream-auth-pepper
export LLMPROXY_UPSTREAM_CREDENTIAL_KEY=00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff
export GRAFANA_ADMIN_USER=admin
export GRAFANA_ADMIN_PASSWORD=upstream-auth-grafana
export GHCR_OWNER=keyserdsoze
export LLMPROXY_IMAGE_TAG=ci-upstream-auth
export LLMPROXY_PULL_POLICY=never
export LLMPROXY_INSTANCE_ID=ci-upstream-auth
export DEPLOYMENT_ENVIRONMENT=ci
export ASPNETCORE_ENVIRONMENT=Development
export ENTRA_ENABLED=false
export ROUTING_STRATEGY=WeightedLeastLoaded
export HEALTH_INTERVAL_SECONDS=1
export HEALTH_HEALTHY_AFTER_SUCCESSES=1
export HEALTH_UNHEALTHY_AFTER_FAILURES=1
export RUNTIME_METRICS_ENABLED=true
export RUNTIME_METRICS_INTERVAL_SECONDS=1
export HARDWARE_METRICS_ENABLED=false
export RETENTION_ENABLED=false
export BOOTSTRAP_ENABLED=true
export DGX_NODE_NAME=dgx-upstream-auth
export DGX_NODE_BASE_ADDRESS=http://host.docker.internal:3496/upstream-auth
export DGX_UPSTREAM_BEARER_TOKEN=upstream-secret
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
  "${COMPOSE[@]}" logs --no-color --tail=200 llmproxy >&2 || true
  [[ -f /tmp/llmproxy-upstream-auth-mock.log ]] && cat /tmp/llmproxy-upstream-auth-mock.log >&2 || true
  exit 1
}

wait_http() {
  local url="$1"
  for attempt in {1..60}; do
    if curl --fail --silent "$url" >/dev/null 2>&1; then return 0; fi
    sleep 1
  done
  return 1
}

python3 tests/backend/integration/mock_llm.py \
  --port 3496 \
  --prefix /upstream-auth \
  --name upstream-auth \
  --api-key upstream-secret \
  > /tmp/llmproxy-upstream-auth-mock.log 2>&1 &
MOCK_PID="$!"
sleep 1

if docker image inspect llmproxy:ci >/dev/null 2>&1; then
  docker tag llmproxy:ci ghcr.io/keyserdsoze/llmproxy:ci-upstream-auth
elif ! docker image inspect ghcr.io/keyserdsoze/llmproxy:ci-upstream-auth >/dev/null 2>&1; then
  docker build -f docker/Dockerfile -t ghcr.io/keyserdsoze/llmproxy:ci-upstream-auth .
fi

"${COMPOSE[@]}" up -d || fail_with_diagnostics "Upstream-auth stack failed to start."
wait_http http://127.0.0.1:8080/readyz || fail_with_diagnostics "Gateway did not become ready."

node_json=""
for attempt in {1..40}; do
  node_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes || true)"
  if echo "$node_json" | jq -e 'map(select(.name == "dgx-upstream-auth" and .status == "Healthy" and .hasUpstreamCredential == true)) | length == 1' >/dev/null 2>&1; then
    break
  fi
  sleep 0.5
done
echo "$node_json" | jq -e 'map(select(.name == "dgx-upstream-auth" and .status == "Healthy" and .hasUpstreamCredential == true)) | length == 1' >/dev/null \
  || fail_with_diagnostics "Authenticated bootstrap node did not become Healthy."

echo "$node_json" | grep -Fq 'upstream-secret' && fail_with_diagnostics "Plaintext upstream bearer leaked through node API."
echo "$node_json" | grep -Fq 'UpstreamBearerTokenCiphertext' && fail_with_diagnostics "Ciphertext property leaked through node API."

node_id="$(echo "$node_json" | jq -r 'map(select(.name == "dgx-upstream-auth"))[0].id')"
[[ "$node_id" =~ ^[0-9a-fA-F-]{36}$ ]] || fail_with_diagnostics "Bootstrap node id missing."

stored_ciphertext="$("${COMPOSE[@]}" exec -T postgres psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Atc 'SELECT "UpstreamBearerTokenCiphertext" FROM nodes LIMIT 1;' | tr -d '\r')"
[[ "$stored_ciphertext" == v1.* ]] || fail_with_diagnostics "Upstream bearer was not stored as versioned ciphertext."
[[ "$stored_ciphertext" != *"upstream-secret"* ]] || fail_with_diagnostics "Plaintext upstream bearer was stored in PostgreSQL."

connection_json="$(curl --fail --silent -X POST "http://127.0.0.1:8080/api/admin/nodes/$node_id/test-connection")"
echo "$connection_json" | jq -e '.success == true and .health.success == true and .openAi.success == true' >/dev/null \
  || fail_with_diagnostics "Authenticated node connection test failed."

inference_status="$(curl --silent --output /tmp/upstream-auth-inference.json --write-out '%{http_code}' \
  -H "Authorization: Bearer $LLM_PROXY_API_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"upstream auth"}]}' \
  http://127.0.0.1:8080/v1/chat/completions)"
[[ "$inference_status" == "200" ]] || fail_with_diagnostics "Authenticated inference failed with HTTP $inference_status."

curl --fail --silent -X DELETE "http://127.0.0.1:8080/api/admin/nodes/$node_id/upstream-credential" >/dev/null
cleared="$(curl --fail --silent -X POST "http://127.0.0.1:8080/api/admin/nodes/$node_id/test-connection")"
echo "$cleared" | jq -e '.success == false and (.health.statusCode == 401 or .openAi.statusCode == 401)' >/dev/null \
  || fail_with_diagnostics "Clearing the upstream credential did not remove authorization."

curl --fail --silent -X PUT \
  -H 'Content-Type: application/json' \
  -d '{"bearerToken":"upstream-secret"}' \
  "http://127.0.0.1:8080/api/admin/nodes/$node_id/upstream-credential" \
  | jq -e '.hasUpstreamCredential == true' >/dev/null

restored=false
for attempt in {1..20}; do
  restored_json="$(curl --fail --silent -X POST "http://127.0.0.1:8080/api/admin/nodes/$node_id/test-connection" || true)"
  if echo "$restored_json" | jq -e '.success == true' >/dev/null 2>&1; then restored=true; break; fi
  sleep 0.2
done
[[ "$restored" == "true" ]] || fail_with_diagnostics "Restored upstream bearer did not take effect live."

echo "Upstream bearer credential smoke passed."
