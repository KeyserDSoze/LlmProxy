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
  [[ -f /tmp/llmproxy-governance-mock.log ]] && cat /tmp/llmproxy-governance-mock.log >&2 || true
  exit 1
}

on_error() {
  local exit_code="$1"
  local line="$2"
  local command="$3"
  fail_with_diagnostics "Governance smoke failed at line ${line}: ${command} (exit ${exit_code})."
}
trap 'on_error "$?" "$LINENO" "$BASH_COMMAND"' ERR

wait_ready() {
  for attempt in {1..30}; do
    if curl --fail --silent http://127.0.0.1:8080/readyz >/dev/null; then
      return 0
    fi
    sleep 2
  done
  fail_with_diagnostics "Gateway did not become ready for governance smoke test."
}

python3 tests/backend/integration/mock_llm.py --port 3470 --prefix /governance --name governance > /tmp/llmproxy-governance-mock.log 2>&1 &
MOCK_PID="$!"
sleep 1

export LLM_PROXY_API_KEY="governance-test-key"
export LLM_PROXY_API_KEY_PEPPER="governance-test-pepper"
export ENTRA_ENABLED="false"
export BOOTSTRAP_ENABLED="true"
export DGX_NODE_NAME="dgx-governance"
export DGX_NODE_BASE_ADDRESS="http://host.docker.internal:3470/governance"
export DGX_NODE_WEIGHT="1"
export DGX_NODE_MAX_CONCURRENCY="10"
export ROUTING_STRATEGY="WeightedLeastLoaded"
export HEALTH_INTERVAL_SECONDS="1"
export HEALTH_HEALTHY_AFTER_SUCCESSES="1"
export HEALTH_UNHEALTHY_AFTER_FAILURES="2"
export RUNTIME_METRICS_ENABLED="false"
export HARDWARE_METRICS_ENABLED="false"

if ! "${COMPOSE[@]}" up -d --build; then
  fail_with_diagnostics "Docker Compose stack failed to start for governance smoke test."
fi
wait_ready

credential_json="$(curl --fail --silent http://127.0.0.1:8080/api/admin/governance/credentials)"
credential_id="$(echo "$credential_json" | jq -r '.[0].id')"
if [[ -z "$credential_id" || "$credential_id" == "null" ]]; then
  fail_with_diagnostics "Bootstrap credential was not available to governance API."
fi

group_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' \
  -d '{"name":"Development CRM","description":"Governance smoke team"}' \
  http://127.0.0.1:8080/api/admin/usage-groups)"
group_id="$(echo "$group_json" | jq -r '.id')"

curl --fail --silent -X PUT -H 'Content-Type: application/json' \
  -d "{\"usageGroupId\":\"${group_id}\"}" \
  "http://127.0.0.1:8080/api/admin/api-credentials/${credential_id}/usage-group" >/dev/null

membership="$(curl --fail --silent http://127.0.0.1:8080/api/admin/governance/credentials)"
echo "$membership" | jq -e --arg group "$group_id" '.[0].usageGroupId == $group' >/dev/null

policy_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' \
  -d "{\"apiCredentialId\":\"${credential_id}\",\"logicalModel\":\"agic-code-fast\",\"requestsPerWindow\":2,\"windowSeconds\":60,\"enabled\":true}" \
  http://127.0.0.1:8080/api/admin/rate-limits)"
policy_id="$(echo "$policy_json" | jq -r '.id')"
echo "$policy_json" | jq -e '.requestsPerWindow == 2 and .windowSeconds == 60 and .logicalModel == "agic-code-fast"' >/dev/null

call_model() {
  local output="$1"
  curl --silent --dump-header "${output}.headers" --output "${output}.json" --write-out '%{http_code}' \
    -H 'Authorization: Bearer governance-test-key' \
    -H 'Content-Type: application/json' \
    -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"governance smoke"}]}' \
    http://127.0.0.1:8080/v1/chat/completions
}

call_model_with_key() {
  local api_key="$1"
  local output="$2"
  curl --silent --dump-header "${output}.headers" --output "${output}.json" --write-out '%{http_code}' \
    -H "Authorization: Bearer ${api_key}" \
    -H 'Content-Type: application/json' \
    -d '{"model":"agic-code-fast","messages":[{"role":"user","content":"user quota smoke"}]}' \
    http://127.0.0.1:8080/v1/chat/completions
}

call_budget_model() {
  local output="$1"
  curl --silent --dump-header "${output}.headers" --output "${output}.json" --write-out '%{http_code}' \
    -H 'Authorization: Bearer governance-test-key' \
    -H 'Content-Type: application/json' \
    -d '{"model":"agic-code-fast","max_tokens":10,"messages":[{"role":"user","content":"output token budget smoke"}]}' \
    http://127.0.0.1:8080/v1/chat/completions
}

# Organization credentials are exempt from caller-specific governance by default.
exempt1="$(call_model /tmp/governance-org-exempt-1)"
exempt2="$(call_model /tmp/governance-org-exempt-2)"
exempt3="$(call_model /tmp/governance-org-exempt-3)"
[[ "$exempt1" == "200" && "$exempt2" == "200" && "$exempt3" == "200" ]] || fail_with_diagnostics "Organization credential should ignore caller quota by default; got ${exempt1}/${exempt2}/${exempt3}."

curl --fail --silent -X PUT -H 'Content-Type: application/json' \
  -d '{"enabled":true}' \
  "http://127.0.0.1:8080/api/admin/api-credentials/${credential_id}/caller-governance" >/dev/null

governed_credential="$(curl --fail --silent http://127.0.0.1:8080/api/admin/governance/credentials)"
echo "$governed_credential" | jq -e --arg credential "$credential_id" 'map(select(.id == $credential and .kind == "organization" and .enforceCallerGovernance == true)) | length == 1' >/dev/null

status1="$(call_model /tmp/governance-request-1)"
status2="$(call_model /tmp/governance-request-2)"
status3="$(call_model /tmp/governance-request-3)"
[[ "$status1" == "200" && "$status2" == "200" ]] || fail_with_diagnostics "Expected first two governed requests to succeed; got ${status1}/${status2}."
[[ "$status3" == "429" ]] || fail_with_diagnostics "Expected third governed request to be rate limited; got ${status3}."
jq -e '.error.type == "rate_limit_error" and .error.code == "rate_limit_exceeded"' /tmp/governance-request-3.json >/dev/null
grep -i --quiet '^Retry-After:' /tmp/governance-request-3.headers
if jq -e '.error.code == "capacity_exhausted"' /tmp/governance-request-3.json >/dev/null 2>&1; then
  fail_with_diagnostics "Caller rate limiting must remain distinct from physical capacity exhaustion."
fi

usage_ready=false
for attempt in {1..20}; do
  usage_json="$(curl --fail --silent 'http://127.0.0.1:8080/api/admin/usage/summary?days=30')"
  if echo "$usage_json" | jq -e --arg group "$group_id" '.requestCount >= 3 and .rateLimitedRequests >= 1 and (.groups | map(select(.usageGroupId == $group and .requestCount >= 3 and .rateLimitedRequests >= 1)) | length) == 1' >/dev/null; then
    usage_ready=true
    break
  fi
  sleep 0.25
done
[[ "$usage_ready" == "true" ]] || fail_with_diagnostics "Usage aggregation did not expose group-attributed rate-limited traffic."

echo "$usage_json" | jq -e '.models | map(select(.logicalModel == "agic-code-fast" and .requestCount >= 3)) | length == 1' >/dev/null
echo "$usage_json" | jq -e --arg credential "$credential_id" '.credentials | map(select(.apiCredentialId == $credential and .requestCount >= 3)) | length == 1' >/dev/null

# Policy and group assignment persist. Runtime counters intentionally restart from a clean window,
# but enforcement must be republished from PostgreSQL at gateway startup.
"${COMPOSE[@]}" restart llmproxy >/dev/null
wait_ready

persisted_policy="$(curl --fail --silent http://127.0.0.1:8080/api/admin/rate-limits)"
echo "$persisted_policy" | jq -e --arg credential "$credential_id" 'map(select(.apiCredentialId == $credential and .logicalModel == "agic-code-fast" and .requestsPerWindow == 2 and .enabled == true)) | length == 1' >/dev/null
persisted_membership="$(curl --fail --silent http://127.0.0.1:8080/api/admin/governance/credentials)"
echo "$persisted_membership" | jq -e --arg group "$group_id" '.[0].usageGroupId == $group' >/dev/null

restart1="$(call_model /tmp/governance-restart-1)"
restart2="$(call_model /tmp/governance-restart-2)"
restart3="$(call_model /tmp/governance-restart-3)"
[[ "$restart1" == "200" && "$restart2" == "200" && "$restart3" == "429" ]] || fail_with_diagnostics "Persisted policy was not republished after restart; got ${restart1}/${restart2}/${restart3}."

# Aggregate user request quota: two personal credentials with the same Entra tid/oid must share one counter.
user_key_a_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' \
  -d '{"name":"User quota key A"}' \
  http://127.0.0.1:8080/api/admin/api-credentials)"
user_key_b_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' \
  -d '{"name":"User quota key B"}' \
  http://127.0.0.1:8080/api/admin/api-credentials)"
user_key_a_id="$(echo "$user_key_a_json" | jq -r '.id')"
user_key_b_id="$(echo "$user_key_b_json" | jq -r '.id')"
user_key_a_secret="$(echo "$user_key_a_json" | jq -r '.secret')"
user_key_b_secret="$(echo "$user_key_b_json" | jq -r '.secret')"

"${COMPOSE[@]}" exec -T postgres psql \
  -U "${POSTGRES_USER:-llmproxy}" \
  -d "${POSTGRES_DB:-llmproxy}" \
  -v ON_ERROR_STOP=1 \
  -c "UPDATE api_credentials SET \"OwnerTenantId\"='tenant-smoke', \"OwnerObjectId\"='user-smoke', \"OwnerPrincipalName\"='smoke@example.com' WHERE \"Id\" IN ('${user_key_a_id}', '${user_key_b_id}');" >/dev/null

# Direct SQL is test-only setup; restart rebuilds the credential L1 from PostgreSQL so ownership is present on the inference path.
"${COMPOSE[@]}" restart llmproxy >/dev/null
wait_ready

user_policy_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' \
  -d '{"ownerTenantId":"tenant-smoke","ownerObjectId":"user-smoke","logicalModel":"agic-code-fast","requestsPerWindow":2,"windowSeconds":60,"enabled":true}' \
  http://127.0.0.1:8080/api/admin/user-rate-limits)"
user_policy_id="$(echo "$user_policy_json" | jq -r '.id')"
echo "$user_policy_json" | jq -e '.requestsPerWindow == 2 and .logicalModel == "agic-code-fast"' >/dev/null

user_limit_list="$(curl --fail --silent http://127.0.0.1:8080/api/admin/user-rate-limits)"
echo "$user_limit_list" | jq -e --arg policy "$user_policy_id" 'map(select(.id == $policy and .ownerObjectId == "USER-SMOKE" and .requestsPerWindow == 2)) | length == 1' >/dev/null

user_status1="$(call_model_with_key "$user_key_a_secret" /tmp/governance-user-1)"
user_status2="$(call_model_with_key "$user_key_b_secret" /tmp/governance-user-2)"
user_status3="$(call_model_with_key "$user_key_a_secret" /tmp/governance-user-3)"
[[ "$user_status1" == "200" && "$user_status2" == "200" ]] || fail_with_diagnostics "Expected first two cross-key user-quota requests to succeed; got ${user_status1}/${user_status2}."
[[ "$user_status3" == "429" ]] || fail_with_diagnostics "Expected aggregate user quota to reject third cross-key request; got ${user_status3}."
jq -e '.error.code == "rate_limit_exceeded"' /tmp/governance-user-3.json >/dev/null

# User policy must survive restart and republish through the normal runtime-state bootstrap.
"${COMPOSE[@]}" restart llmproxy >/dev/null
wait_ready
persisted_user_limits="$(curl --fail --silent http://127.0.0.1:8080/api/admin/user-rate-limits)"
echo "$persisted_user_limits" | jq -e --arg policy "$user_policy_id" 'map(select(.id == $policy and .enabled == true)) | length == 1' >/dev/null
user_restart1="$(call_model_with_key "$user_key_a_secret" /tmp/governance-user-restart-1)"
user_restart2="$(call_model_with_key "$user_key_b_secret" /tmp/governance-user-restart-2)"
user_restart3="$(call_model_with_key "$user_key_b_secret" /tmp/governance-user-restart-3)"
[[ "$user_restart1" == "200" && "$user_restart2" == "200" && "$user_restart3" == "429" ]] \
  || fail_with_diagnostics "Persisted user quota was not republished after restart; got ${user_restart1}/${user_restart2}/${user_restart3}."

# Move request-rate admission out of the way, then configure the output-token budget on the same policy.
# The mock returns 7 completion tokens. Budget=17 and reservation=10 means the second request succeeds
# only if the first settlement refunds 3 unused tokens. The third request must then be rejected at 14+10>17.
curl --fail --silent -X PUT -H 'Content-Type: application/json' \
  -d '{"logicalModel":"agic-code-fast","requestsPerWindow":100,"windowSeconds":300,"enabled":true}' \
  "http://127.0.0.1:8080/api/admin/rate-limits/${policy_id}" >/dev/null

budget_json="$(curl --fail --silent -X PUT -H 'Content-Type: application/json' \
  -d '{"outputTokensPerWindow":17,"maxOutputTokensPerRequest":10}' \
  "http://127.0.0.1:8080/api/admin/rate-limits/${policy_id}/output-token-budget")"
echo "$budget_json" | jq -e '.outputTokensPerWindow == 17 and .maxOutputTokensPerRequest == 10 and .windowSeconds == 300' >/dev/null

budget_list="$(curl --fail --silent http://127.0.0.1:8080/api/admin/output-token-budgets)"
echo "$budget_list" | jq -e --arg policy "$policy_id" 'map(select(.id == $policy and .outputTokensPerWindow == 17 and .maxOutputTokensPerRequest == 10)) | length == 1' >/dev/null

budget1="$(call_budget_model /tmp/governance-budget-1)"
budget2="$(call_budget_model /tmp/governance-budget-2)"
budget3="$(call_budget_model /tmp/governance-budget-3)"
[[ "$budget1" == "200" && "$budget2" == "200" ]] || fail_with_diagnostics "Expected reservation settlement to permit two budgeted requests; got ${budget1}/${budget2}."
[[ "$budget3" == "429" ]] || fail_with_diagnostics "Expected third request to exceed output-token budget; got ${budget3}."
jq -e '.error.type == "rate_limit_error" and .error.code == "token_budget_exceeded"' /tmp/governance-budget-3.json >/dev/null
grep -i --quiet '^Retry-After:' /tmp/governance-budget-3.headers

invalid_budget_status="$(curl --silent --output /tmp/governance-budget-invalid.json --write-out '%{http_code}' \
  -H 'Authorization: Bearer governance-test-key' \
  -H 'Content-Type: application/json' \
  -d '{"model":"agic-code-fast","max_tokens":0,"messages":[]}' \
  http://127.0.0.1:8080/v1/chat/completions)"
[[ "$invalid_budget_status" == "400" ]] || fail_with_diagnostics "Expected invalid output-token limit to return 400; got ${invalid_budget_status}."
jq -e '.error.code == "invalid_output_token_limit"' /tmp/governance-budget-invalid.json >/dev/null

# The policy is durable even though local counters intentionally reset on restart.
# Use a five-minute window here so runner/image/restart latency cannot accidentally roll the
# fixed window between the pre-restart and post-restart assertions.
"${COMPOSE[@]}" restart llmproxy >/dev/null
wait_ready
persisted_budget="$(curl --fail --silent http://127.0.0.1:8080/api/admin/output-token-budgets)"
echo "$persisted_budget" | jq -e --arg policy "$policy_id" 'map(select(.id == $policy and .outputTokensPerWindow == 17 and .maxOutputTokensPerRequest == 10)) | length == 1' >/dev/null

budget_restart1="$(call_budget_model /tmp/governance-budget-restart-1)"
budget_restart2="$(call_budget_model /tmp/governance-budget-restart-2)"
budget_restart3="$(call_budget_model /tmp/governance-budget-restart-3)"
[[ "$budget_restart1" == "200" && "$budget_restart2" == "200" && "$budget_restart3" == "429" ]] \
  || fail_with_diagnostics "Persisted output-token budget was not republished after restart; got ${budget_restart1}/${budget_restart2}/${budget_restart3}."
jq -e '.error.code == "token_budget_exceeded"' /tmp/governance-budget-restart-3.json >/dev/null

audit_json="$(curl --fail --silent 'http://127.0.0.1:8080/api/admin/audit?take=100')"
echo "$audit_json" | jq -e 'map(.action) | index("usage_group.create") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.action) | index("credential.usage_group.assign") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.action) | index("rate_limit.create") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.action) | index("user_rate_limit.create") != null' >/dev/null
echo "$audit_json" | jq -e 'map(.action) | index("output_token_budget.update") != null' >/dev/null

echo "Governance smoke suite passed: usage group attribution, credential + aggregate-user request-rate limiting, output-token reservation/refund/exhaustion, and restart republish verified."
