#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE=(docker compose -f docker/docker-compose.yml)
MOCK_PIDS=()

cleanup() {
  "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
  for pid in "${MOCK_PIDS[@]:-}"; do
    kill "$pid" >/dev/null 2>&1 || true
    wait "$pid" >/dev/null 2>&1 || true
  done
}
trap cleanup EXIT

fail_with_diagnostics() {
  local message="$1"
  echo "$message" >&2
  "${COMPOSE[@]}" ps -a >&2 || true
  "${COMPOSE[@]}" logs --no-color >&2 || true
  exit 1
}

start_mock() {
  local port="$1"
  local prefix="$2"
  local name="$3"
  local api_key="${4:-}"
  python3 tests/backend/integration/mock_llm.py --port "$port" --prefix "$prefix" --name "$name" --api-key "$api_key" >"/tmp/llmproxy-mock-${name}.log" 2>&1 &
  MOCK_PIDS+=("$!")
}

wait_ready() {
  local ready=false
  for attempt in {1..30}; do
    if curl --fail --silent http://127.0.0.1:8080/readyz >/dev/null; then
      ready=true
      break
    fi
    sleep 2
  done
  if [[ "$ready" != "true" ]]; then
    fail_with_diagnostics "Gateway did not become ready."
  fi
}

wait_node_status() {
  local node_id="$1"
  local expected="$2"
  local attempts="${3:-20}"
  for ((attempt=1; attempt<=attempts; attempt++)); do
    status="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes | jq -r --arg id "$node_id" '.[] | select(.id == $id) | .status')"
    if [[ "$status" == "$expected" ]]; then
      return 0
    fi
    sleep 0.5
  done
  fail_with_diagnostics "Node ${node_id} did not reach ${expected}."
}

start_mock 3450 /primopath primary
start_mock 3451 /altropath alternate
start_mock 3452 /classifier classifier laya-upstream
sleep 1

export LLM_PROXY_API_KEY="dev-change-me"
export LLM_PROXY_API_KEY_PEPPER="ci-test-pepper"
# System One legacy bootstrap imports its upstream bearer into the managed node catalog,
# so the integration fixture must provide the same stable encryption key required in production.
export LLMPROXY_UPSTREAM_CREDENTIAL_KEY="00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"
export ENTRA_ENABLED="false"
export BOOTSTRAP_ENABLED="true"
export INFERENCE_NODE_NAME="inference-local-primary"
export INFERENCE_NODE_BASE_ADDRESS="http://host.docker.internal:3450/primopath"
export INFERENCE_NODE_WEIGHT="1"
export INFERENCE_NODE_MAX_CONCURRENCY="4"
export ROUTING_STRATEGY="WeightedRoundRobin"
export HEALTH_INTERVAL_SECONDS="1"
export HEALTH_HEALTHY_AFTER_SUCCESSES="2"
export HEALTH_UNHEALTHY_AFTER_FAILURES="3"
export RUNTIME_METRICS_ENABLED="true"
export RUNTIME_METRICS_INTERVAL_SECONDS="1"
export SYSTEM_ONE_ENABLED="true"
export SYSTEM_ONE_BASE_ADDRESS="http://host.docker.internal:3452/classifier"
export SYSTEM_ONE_API_KEY="laya-upstream"
export SYSTEM_ONE_TIMEOUT_SECONDS="5"

if ! "${COMPOSE[@]}" up -d --build; then
  fail_with_diagnostics "Docker Compose stack failed to start."
fi
wait_ready

# Public LLMProxy-domain Agent installer and checksum are accessible without a login.
public_agent_tmp="$(mktemp -d)"
curl --fail --silent http://127.0.0.1:8080/downloads/agent/connect-node.sh \
  -o "$public_agent_tmp/connect-node.sh"
curl --fail --silent http://127.0.0.1:8080/downloads/agent/connect-node.sh.sha256 \
  -o "$public_agent_tmp/connect-node.sh.sha256"
grep --quiet '^#!/usr/bin/env bash' "$public_agent_tmp/connect-node.sh"
(cd "$public_agent_tmp" && sha256sum --check connect-node.sh.sha256)
rm -rf "$public_agent_tmp"

curl --fail --silent http://127.0.0.1:8080/healthz | grep --quiet '"status":"ok"'
curl --fail --silent http://127.0.0.1:8080/healthz | grep --quiet 'WeightedRoundRobin'
curl --fail --silent http://127.0.0.1:8080/api/admin/routing | grep --quiet 'WeightedRoundRobin'

default_tuning="$(curl --fail --silent http://127.0.0.1:8080/api/admin/routing/tuning)"
echo "$default_tuning" | jq -e '.warmupSamples == 3 and .ttftTargetMilliseconds == 2000 and .kvCacheThreshold == 0.7 and .queuePenaltyWeight == 0.75' >/dev/null

updated_tuning="$(curl --fail --silent -X PUT -H 'Content-Type: application/json' -d '{"warmupSamples":4,"ttftTargetMilliseconds":1500,"ttftPenaltyWeight":0.3,"failurePenaltyWeight":1.7,"externalLoadPenaltyWeight":0.45,"queuePenaltyWeight":0.8,"kvCacheThreshold":0.75,"kvCachePenaltyWeight":0.65,"degradedNodePenalty":0.4,"unknownNodePenalty":0.12}' http://127.0.0.1:8080/api/admin/routing/tuning)"
echo "$updated_tuning" | jq -e '.warmupSamples == 4 and .ttftTargetMilliseconds == 1500 and .failurePenaltyWeight == 1.7 and .kvCacheThreshold == 0.75' >/dev/null

unauthorized_models_status="$(curl --silent --output /dev/null --write-out '%{http_code}' http://127.0.0.1:8080/v1/models)"
if [[ "$unauthorized_models_status" != "401" ]]; then
  fail_with_diagnostics "Expected /v1/models without bearer token to return 401, got ${unauthorized_models_status}."
fi

unauthorized_responses_status="$(curl --silent --output /dev/null --write-out '%{http_code}' -X POST -H 'Content-Type: application/json' -d '{"model":"agic-code-fast","input":"hello"}' http://127.0.0.1:8080/v1/responses)"
if [[ "$unauthorized_responses_status" != "401" ]]; then
  fail_with_diagnostics "Expected /v1/responses without bearer token to return 401, got ${unauthorized_responses_status}."
fi

unauthorized_systemone_status="$(curl --silent --output /dev/null --write-out '%{http_code}' -X POST -H 'Content-Type: application/json' -d '{"state":{"document":"duplicate charge"},"questions":{"billing":{"type":"noul","instructions":"Is this billing?"}}}' http://127.0.0.1:8080/v1/systemone)"
if [[ "$unauthorized_systemone_status" != "401" ]]; then
  fail_with_diagnostics "Expected /v1/systemone without bearer token to return 401, got ${unauthorized_systemone_status}."
fi

systemone_response="$(curl --fail --silent -H 'Authorization: Bearer dev-change-me' -H 'Content-Type: application/json' -d '{"state":{"document":"duplicate charge"},"questions":{"billing":{"type":"noul","instructions":"Is this billing?"}}}' http://127.0.0.1:8080/v1/systemone)"
echo "$systemone_response" | jq -e '.served_by == "classifier" and .answers.billing.noul == 0.91 and .state.document == "duplicate charge"' >/dev/null

systemone_models="$(curl --fail --silent -H 'Authorization: Bearer dev-change-me' http://127.0.0.1:8080/v1/systemone/models)"
echo "$systemone_models" | jq -e '.data | map(.id) | index("systemone-default") != null' >/dev/null
curl --fail --silent -H 'Authorization: Bearer dev-change-me' http://127.0.0.1:8080/v1/models | grep --quiet 'agic-code-fast'
if curl --fail --silent -H 'Authorization: Bearer dev-change-me' http://127.0.0.1:8080/v1/models | grep --quiet 'systemone-default'; then
  fail_with_diagnostics "System One logical models must not leak into the OpenAI /v1/models catalog."
fi

primary_response="$(curl --fail --silent -H 'Authorization: Bearer dev-change-me' -H 'Content-Type: application/json' -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"hello"}]}' http://127.0.0.1:8080/v1/chat/completions)"
echo "$primary_response" | grep --quiet '"served_by":"primary"'
echo "$primary_response" | grep --quiet '"model":"bootstrap-model"'
echo "$primary_response" | jq -e '.usage.total_tokens == 18' >/dev/null

admin_session="$(curl --fail --silent http://127.0.0.1:8080/api/admin/session)"
echo "$admin_session" | jq -e '.canWrite == true' >/dev/null

user_access_settings="$(curl --fail --silent http://127.0.0.1:8080/api/admin/users/settings)"
echo "$user_access_settings" | jq -e '.provisioningMode == "manual"' >/dev/null

automatic_user_access="$(curl --fail --silent -X PUT -H 'Content-Type: application/json' -d '{"provisioningMode":"automatic"}' http://127.0.0.1:8080/api/admin/users/settings)"
echo "$automatic_user_access" | jq -e '.provisioningMode == "automatic"' >/dev/null
curl --fail --silent -X PUT -H 'Content-Type: application/json' -d '{"provisioningMode":"manual"}' http://127.0.0.1:8080/api/admin/users/settings >/dev/null

platform_user="$(curl --fail --silent -H 'Content-Type: application/json' -d '{"tenantId":"tenant-smoke","objectId":"object-smoke","principalName":"user-smoke@example.com","displayName":"Smoke User","enabled":true}' http://127.0.0.1:8080/api/admin/users)"
platform_user_id="$(echo "$platform_user" | jq -r '.id')"
echo "$platform_user" | jq -e '.tenantId == "tenant-smoke" and .objectId == "object-smoke" and .enabled == true and .provisioningSource == "admin"' >/dev/null

user_credential="$(curl --fail --silent -H 'Content-Type: application/json' -d '{"name":"User suspension smoke"}' http://127.0.0.1:8080/api/admin/api-credentials)"
user_credential_id="$(echo "$user_credential" | jq -r '.id')"
user_credential_secret="$(echo "$user_credential" | jq -r '.secret')"

"${COMPOSE[@]}" exec -T postgres psql -U llmproxy -d llmproxy -v ON_ERROR_STOP=1 -c "UPDATE api_credentials SET \"OwnerTenantId\"='tenant-smoke', \"OwnerObjectId\"='object-smoke', \"OwnerPrincipalName\"='user-smoke@example.com' WHERE \"Id\"='${user_credential_id}';" >/dev/null

curl --fail --silent -H "Authorization: Bearer ${user_credential_secret}" http://127.0.0.1:8080/v1/models >/dev/null

curl --fail --silent -X POST "http://127.0.0.1:8080/api/admin/users/${platform_user_id}/disable" >/dev/null
disabled_user_key_status="$(curl --silent --output /dev/null --write-out '%{http_code}' -H "Authorization: Bearer ${user_credential_secret}" http://127.0.0.1:8080/v1/models)"
if [[ "$disabled_user_key_status" != "401" ]]; then
  fail_with_diagnostics "Expected disabled user's personal API key to return 401, got ${disabled_user_key_status}."
fi

platform_users="$(curl --fail --silent http://127.0.0.1:8080/api/admin/users)"
echo "$platform_users" | jq -e --arg id "$platform_user_id" 'map(select(.id == $id and .enabled == false and .activeCredentialCount == 0)) | length == 1' >/dev/null

curl --fail --silent -X POST "http://127.0.0.1:8080/api/admin/users/${platform_user_id}/enable" >/dev/null
reenabled_old_key_status="$(curl --silent --output /dev/null --write-out '%{http_code}' -H "Authorization: Bearer ${user_credential_secret}" http://127.0.0.1:8080/v1/models)"
if [[ "$reenabled_old_key_status" != "401" ]]; then
  fail_with_diagnostics "Expected re-enabled user to keep the previously revoked API key invalid, got ${reenabled_old_key_status}."
fi

systemone_status="$(curl --fail --silent http://127.0.0.1:8080/api/admin/testing/systemone)"
echo "$systemone_status" | jq -e '.enabled == true and .apiKeyConfigured == true and .publicEndpoint == "/v1/systemone" and .timeoutSeconds == 5 and (.upstreamEndpoint | contains("3452/classifier/v1/systemone"))' >/dev/null

# Same host, different runtime ports are one physical capacity pool. Legacy System One
# keeps its runtime root and encrypted bearer at deployment scope rather than creating
# a second pseudo-hardware node.
bootstrap_nodes="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes)"
echo "$bootstrap_nodes" | jq -e 'length == 1' >/dev/null
bootstrap_node_id="$(echo "$bootstrap_nodes" | jq -r '.[0].id')"
systemone_model_id="$(curl --fail --silent http://127.0.0.1:8080/api/admin/models | jq -r 'map(select(.surface == "SystemOne")) | first | .id')"
systemone_deployment="$(curl --fail --silent http://127.0.0.1:8080/api/admin/deployments | jq -c --arg model "$systemone_model_id" 'map(select(.modelId == $model)) | first')"
echo "$systemone_deployment" | jq -e --arg node "$bootstrap_node_id" '.nodeId == $node and .maxConcurrency == 8 and (.runtimeBaseAddress | contains("3452/classifier"))' >/dev/null
systemone_bearer_ciphertext="$("${COMPOSE[@]}" exec -T postgres psql -U llmproxy -d llmproxy -Atc "SELECT \"UpstreamBearerTokenCiphertext\" FROM deployments WHERE \"Id\"='$(echo "$systemone_deployment" | jq -r '.id')';" | tr -d '\r')"
[[ -n "$systemone_bearer_ciphertext" ]] || fail_with_diagnostics "Legacy System One bearer was not preserved at deployment scope."

consolidation_source="$(curl --fail --silent -H 'Content-Type: application/json' -d '{"name":"z-consolidation-source","baseAddress":"http://host.docker.internal:3452/classifier","weight":1,"maxConcurrency":3,"upstreamBearerToken":"laya-upstream"}' http://127.0.0.1:8080/api/admin/nodes)"
consolidation_source_id="$(echo "$consolidation_source" | jq -r '.id')"
consolidation_model="$(curl --fail --silent -H 'Content-Type: application/json' -d '{"publicName":"zz-consolidation-smoke","providerModelName":"bootstrap-model","supportsStreaming":true,"supportsTools":false,"surface":"OpenAi"}' http://127.0.0.1:8080/api/admin/models)"
consolidation_model_id="$(echo "$consolidation_model" | jq -r '.id')"
consolidation_deployment="$(curl --fail --silent -H 'Content-Type: application/json' -d "{\"nodeId\":\"${consolidation_source_id}\",\"modelId\":\"${consolidation_model_id}\",\"weight\":1}" http://127.0.0.1:8080/api/admin/deployments)"
consolidation_deployment_id="$(echo "$consolidation_deployment" | jq -r '.id')"
consolidation_result="$(curl --fail --silent -X POST "http://127.0.0.1:8080/api/admin/nodes/${consolidation_source_id}/consolidate-into/${bootstrap_node_id}")"
echo "$consolidation_result" | jq -e --arg source "$consolidation_source_id" --arg target "$bootstrap_node_id" '.sourceNodeId == $source and .targetNodeId == $target and .movedDeployments == 1' >/dev/null
curl --fail --silent http://127.0.0.1:8080/api/admin/nodes | jq -e --arg source "$consolidation_source_id" 'map(select(.id == $source)) | length == 0' >/dev/null
consolidated_deployment="$(curl --fail --silent http://127.0.0.1:8080/api/admin/deployments | jq -c --arg id "$consolidation_deployment_id" 'map(select(.id == $id)) | first')"
echo "$consolidated_deployment" | jq -e --arg target "$bootstrap_node_id" '.nodeId == $target and .maxConcurrency == 3 and (.runtimeBaseAddress | contains("3452/classifier"))' >/dev/null
consolidated_bearer_ciphertext="$("${COMPOSE[@]}" exec -T postgres psql -U llmproxy -d llmproxy -Atc "SELECT \"UpstreamBearerTokenCiphertext\" FROM deployments WHERE \"Id\"='${consolidation_deployment_id}';" | tr -d '\r')"
[[ -n "$consolidated_bearer_ciphertext" ]] || fail_with_diagnostics "Consolidation did not preserve the source upstream bearer at deployment scope."

systemone_test="$(curl --fail --silent -H 'Content-Type: application/json' -d '{"payload":{"state":{"document":"admin classifier test"},"questions":{"billing":{"type":"noul","instructions":"Is this billing?"}}}}' http://127.0.0.1:8080/api/admin/testing/systemone)"
echo "$systemone_test" | jq -e '.success == true and .statusCode == 200 and (.responseBody | fromjson | .served_by) == "classifier" and (.responseBody | fromjson | .state.document) == "admin classifier test"' >/dev/null

chat_test="$(curl --fail --silent -H 'Content-Type: application/json' -d '{"model":"agic-code-fast","systemPrompt":"You are concise.","userPrompt":"admin model test","maxTokens":64,"temperature":0.1}' http://127.0.0.1:8080/api/admin/testing/chat)"
echo "$chat_test" | jq -e '.success == true and .statusCode == 200 and .logicalModel == "agic-code-fast" and .nodeName == "inference-local-primary" and (.responseBody | fromjson | .served_by) == "primary"' >/dev/null

bootstrap_credential="$(curl --fail --silent http://127.0.0.1:8080/api/admin/api-credentials | jq -c 'map(select(.name == "Bootstrap / GitHub Copilot")) | first')"
bootstrap_credential_id="$(echo "$bootstrap_credential" | jq -r '.id')"
echo "$bootstrap_credential" | jq -e '.secretAvailable == true' >/dev/null
revealed_bootstrap="$(curl --fail --silent "http://127.0.0.1:8080/api/admin/api-credentials/${bootstrap_credential_id}/secret")"
echo "$revealed_bootstrap" | jq -e '.secret == "dev-change-me" and .keyPrefix == "dev-change-me"' >/dev/null

content_log_settings="$(curl --fail --silent http://127.0.0.1:8080/api/admin/content-logs/settings)"
echo "$content_log_settings" | jq -e '.retentionDays == 30 and .minimumRetentionDays == 10 and .maximumRetentionDays == 4015 and .cleanupIntervalHours == 4' >/dev/null

invalid_retention_status="$(curl --silent --output /dev/null --write-out '%{http_code}' -X PUT -H 'Content-Type: application/json' -d '{"retentionDays":9}' http://127.0.0.1:8080/api/admin/content-logs/settings)"
if [[ "$invalid_retention_status" != "400" ]]; then
  fail_with_diagnostics "Expected content-log retention 9 days to be rejected with 400, got ${invalid_retention_status}."
fi

updated_content_log_settings="$(curl --fail --silent -X PUT -H 'Content-Type: application/json' -d '{"retentionDays":10}' http://127.0.0.1:8080/api/admin/content-logs/settings)"
echo "$updated_content_log_settings" | jq -e '.retentionDays == 10 and .cleanupIntervalHours == 4' >/dev/null

maximum_content_log_settings="$(curl --fail --silent -X PUT -H 'Content-Type: application/json' -d '{"retentionDays":4015}' http://127.0.0.1:8080/api/admin/content-logs/settings)"
echo "$maximum_content_log_settings" | jq -e '.retentionDays == 4015 and .maximumRetentionDays == 4015' >/dev/null

too_long_retention_status="$(curl --silent --output /dev/null --write-out '%{http_code}' -X PUT -H 'Content-Type: application/json' -d '{"retentionDays":4016}' http://127.0.0.1:8080/api/admin/content-logs/settings)"
if [[ "$too_long_retention_status" != "400" ]]; then
  fail_with_diagnostics "Expected request-audit retention 4016 days to be rejected with 400, got ${too_long_retention_status}."
fi

curl --fail --silent -X PUT -H 'Content-Type: application/json' -d '{"retentionDays":10}' http://127.0.0.1:8080/api/admin/content-logs/settings >/dev/null

content_logs_ready=false
for attempt in {1..40}; do
  content_logs_json="$(curl --fail --silent 'http://127.0.0.1:8080/api/admin/content-logs?take=100')"
  if echo "$content_logs_json" | jq -e 'map(.surface) | (index("chat_completions") != null and index("systemone") != null and index("model_test") != null and index("systemone_test") != null)' >/dev/null; then
    content_logs_ready=true
    break
  fi
  sleep 0.25
done
if [[ "$content_logs_ready" != "true" ]]; then
  fail_with_diagnostics "Encrypted full-body content logs did not persist the expected inference and admin diagnostic surfaces."
fi

chat_content_log_id="$(echo "$content_logs_json" | jq -r 'map(select(.surface == "chat_completions" and .logicalModel == "agic-code-fast")) | first | .id')"
chat_content_log="$(curl --fail --silent "http://127.0.0.1:8080/api/admin/content-logs/${chat_content_log_id}")"
echo "$chat_content_log" | jq -e '.requestBody | fromjson | .messages[0].content == "hello"' >/dev/null
echo "$chat_content_log" | jq -e '.responseBody | fromjson | .served_by == "primary"' >/dev/null

curl --fail --silent -X PUT -H 'Content-Type: application/json' -d '{"retentionDays":30}' http://127.0.0.1:8080/api/admin/content-logs/settings >/dev/null

model_id="$(curl --fail --silent http://127.0.0.1:8080/api/admin/models | jq -r '.[0].id')"
node2_json="$(curl --fail --silent -H 'Content-Type: application/json' -d '{"name":"inference-local-alternate","baseAddress":"http://host.docker.internal:3451/altropath","weight":3,"maxConcurrency":4}' http://127.0.0.1:8080/api/admin/nodes)"
node2_id="$(echo "$node2_json" | jq -r '.id')"

curl --fail --silent -H 'Content-Type: application/json' -d "{\"nodeId\":\"${node2_id}\",\"modelId\":\"${model_id}\",\"weight\":1,\"maxConcurrency\":4}" http://127.0.0.1:8080/api/admin/deployments >/dev/null
wait_node_status "$node2_id" Healthy 30

node_health_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes | jq -c --arg id "$node2_id" '.[] | select(.id == $id)')"
echo "$node_health_json" | jq -e '.lastHealthLatencyMilliseconds != null and .consecutiveHealthSuccesses >= 2 and .lastHealthError == null' >/dev/null

runtime_ready=false
for attempt in {1..20}; do
  runtime_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/routing/runtime)"
  if echo "$runtime_json" | jq -e --arg id "$node2_id" 'map(select(.nodeId == $id and .available == true and .runningRequests == 2 and .waitingRequests == 1 and .kvCacheUsageRatio > 0.5 and .modelName == "bootstrap-model")) | length == 1' >/dev/null; then runtime_ready=true; break; fi
  sleep 0.25
done
if [[ "$runtime_ready" != "true" ]]; then fail_with_diagnostics "vLLM runtime metrics were not collected from the path-prefixed alternate node."; fi

connection_test="$(curl --fail --silent -X POST http://127.0.0.1:8080/api/admin/nodes/${node2_id}/test-connection)"
echo "$connection_test" | jq -e '.success == true and .health.statusCode == 200 and .openAi.statusCode == 200' >/dev/null
echo "$connection_test" | grep --quiet 'altropath/v1/chat/completions'
echo "$connection_test" | grep --quiet 'altropath/v1/responses'

primary_count=0
alternate_count=0
for attempt in {1..8}; do
  response="$(curl --fail --silent -H 'Authorization: Bearer dev-change-me' -H 'Content-Type: application/json' -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"weighted route"}]}' http://127.0.0.1:8080/v1/chat/completions)"
  if echo "$response" | grep --quiet '"served_by":"primary"'; then primary_count=$((primary_count + 1));
  elif echo "$response" | grep --quiet '"served_by":"alternate"'; then alternate_count=$((alternate_count + 1));
  else fail_with_diagnostics "Weighted routing returned an unknown backend response: $response"; fi
done
if [[ "$primary_count" -ne 2 || "$alternate_count" -ne 6 ]]; then fail_with_diagnostics "Expected weighted split primary=2 alternate=6, got primary=${primary_count} alternate=${alternate_count}."; fi

performance_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/routing/performance)"
echo "$performance_json" | jq -e 'map(select(.sampleCount > 0)) | length > 0' >/dev/null

curl --fail --silent -X PUT -H 'Content-Type: application/json' -d '{"strategy":"RoundRobin"}' http://127.0.0.1:8080/api/admin/routing | grep --quiet 'RoundRobin'
curl --fail --silent http://127.0.0.1:8080/healthz | grep --quiet 'RoundRobin'

rr_primary=0
rr_alternate=0
for attempt in {1..4}; do
  response="$(curl --fail --silent -H 'Authorization: Bearer dev-change-me' -H 'Content-Type: application/json' -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"round robin route"}]}' http://127.0.0.1:8080/v1/chat/completions)"
  if echo "$response" | grep --quiet '"served_by":"primary"'; then rr_primary=$((rr_primary + 1)); elif echo "$response" | grep --quiet '"served_by":"alternate"'; then rr_alternate=$((rr_alternate + 1)); fi
done
if [[ "$rr_primary" -ne 2 || "$rr_alternate" -ne 2 ]]; then fail_with_diagnostics "Expected live round-robin split 2/2, got primary=${rr_primary} alternate=${rr_alternate}."; fi

"${COMPOSE[@]}" restart llmproxy >/dev/null
wait_ready
curl --fail --silent http://127.0.0.1:8080/api/admin/routing | grep --quiet 'RoundRobin'
curl --fail --silent http://127.0.0.1:8080/healthz | grep --quiet 'RoundRobin'
persisted_tuning="$(curl --fail --silent http://127.0.0.1:8080/api/admin/routing/tuning)"
echo "$persisted_tuning" | jq -e '.warmupSamples == 4 and .ttftTargetMilliseconds == 1500 and .queuePenaltyWeight == 0.8 and .kvCacheThreshold == 0.75' >/dev/null

responses_payload="$(curl --fail --silent -H 'Authorization: Bearer dev-change-me' -H 'Content-Type: application/json' -d '{"model":"agic-code-fast","input":"hello responses"}' http://127.0.0.1:8080/v1/responses)"
echo "$responses_payload" | grep --quiet '"object":"response"'
echo "$responses_payload" | jq -e '.usage.input_tokens == 13 and .usage.output_tokens == 5' >/dev/null

python3 tests/backend/integration/assert_streaming.py http://127.0.0.1:8080/v1/chat/completions dev-change-me

observability_ready=false
metrics_json='[]'
for attempt in {1..30}; do
  metrics_json="$(curl --fail --silent 'http://127.0.0.1:8080/api/admin/metrics?take=200')"
  if echo "$metrics_json" | jq -e 'map(select(.isStreaming == true and .timeToFirstByteMilliseconds != null and .totalTokens == 23)) | length > 0' >/dev/null; then observability_ready=true; break; fi
  sleep 0.25
done
if [[ "$observability_ready" != "true" ]]; then fail_with_diagnostics "Inference observability metrics did not contain the completed SSE request."; fi

echo "$metrics_json" | jq -e 'map(select(.surface == "chat_completions" and .attemptCount >= 1 and .upstreamHeaderMilliseconds != null and .totalTokens != null)) | length > 0' >/dev/null
echo "$metrics_json" | jq -e 'map(select(.surface == "responses" and .inputTokens == 13 and .outputTokens == 5 and .totalTokens == 18)) | length > 0' >/dev/null
echo "$metrics_json" | jq -e 'map(select(.isStreaming == true and .inputTokens == 17 and .outputTokens == 6 and .totalTokens == 23)) | length > 0' >/dev/null

summary_json="$(curl --fail --silent 'http://127.0.0.1:8080/api/admin/metrics/summary?hours=24')"
echo "$summary_json" | jq -e '.windowHours == 24 and .requestCount > 0 and .successCount > 0 and .p50DurationMilliseconds != null and .p95DurationMilliseconds != null and .p50TimeToFirstByteMilliseconds != null and .p95TimeToFirstByteMilliseconds != null and .outputTokens > 0 and .tokenObservedRequests > 0 and (.byModel | length) > 0 and (.byNode | length) > 0' >/dev/null

curl --fail --silent -X POST http://127.0.0.1:3451/__control/health/500 >/dev/null
wait_node_status "$node2_id" Degraded 10
wait_node_status "$node2_id" Unhealthy 15
failed_health="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes | jq -c --arg id "$node2_id" '.[] | select(.id == $id)')"
echo "$failed_health" | jq -e '.consecutiveHealthFailures >= 3 and (.lastHealthError | contains("HTTP 500"))' >/dev/null

curl --fail --silent -X POST http://127.0.0.1:3451/__control/health/200 >/dev/null
wait_node_status "$node2_id" Degraded 10
wait_node_status "$node2_id" Healthy 15
recovered_health="$(curl --fail --silent http://127.0.0.1:8080/api/admin/nodes | jq -c --arg id "$node2_id" '.[] | select(.id == $id)')"
echo "$recovered_health" | jq -e '.consecutiveHealthSuccesses >= 2 and .consecutiveHealthFailures == 0 and .lastHealthError == null' >/dev/null

audit_json="$(curl --fail --silent 'http://127.0.0.1:8080/api/admin/audit?take=100')"
echo "$audit_json" | jq -e 'map(.action) | index("node.create") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.action) | index("deployment.create") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.action) | index("node.test_connection") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.action) | index("routing.update") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.action) | index("routing.tuning.update") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.action) | index("credential.secret.reveal") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.action) | index("content_log.retention.update") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.action) | index("user.provisioning_mode.update") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.action) | index("user.create") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.action) | index("user.disable") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.action) | index("user.enable") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.actor) | index("local-admin") != null' >/dev/null

curl --fail --silent http://127.0.0.1:8080/api/admin/overview | grep --quiet 'activeRequests'

echo "Backend integration smoke suite passed. Weighted=${primary_count}/${alternate_count}, round-robin=${rr_primary}/${rr_alternate}, System One proxy + admin classifier test, admin model chat test, encrypted API-key recovery, encrypted full-body logs/retention, live tuning, vLLM runtime telemetry, observability, health hysteresis and audit verified."
