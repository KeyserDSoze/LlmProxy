#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE_FILE="tests/backend/integration/backup_restore_compose.yml"
COMPOSE=(docker compose -f "$COMPOSE_FILE")
BACKUP_FILE="/tmp/llmproxy-backup-restore-powershell.dump"
MOCK_PID=""

export POSTGRES_DB=llmproxy
export POSTGRES_USER=llmproxy
export POSTGRES_PASSWORD=backup-restore-powershell-postgres
export LLM_PROXY_API_KEY=backup-powershell-bootstrap-key
export LLM_PROXY_API_KEY_PEPPER=backup-restore-powershell-pepper
export LLMPROXY_PORT=8086

cleanup() {
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
  "${COMPOSE[@]}" ps -a >&2 || true
  "${COMPOSE[@]}" logs --no-color --tail=250 >&2 || true
  [[ -f /tmp/llmproxy-backup-restore-powershell-mock.log ]] && cat /tmp/llmproxy-backup-restore-powershell-mock.log >&2 || true
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
  for attempt in {1..50}; do
    local nodes_json
    nodes_json="$(curl --fail --silent http://127.0.0.1:8086/api/admin/nodes || true)"
    if echo "$nodes_json" | jq -e 'map(select(.name == "dgx-backup-restore" and .status == "Healthy")) | length == 1' >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done
  return 1
}

call_model() {
  local secret="$1"
  local output="$2"
  curl --silent --dump-header "${output}.headers" --output "${output}.json" --write-out '%{http_code}' \
    -H "Authorization: Bearer ${secret}" \
    -H 'Content-Type: application/json' \
    -d '{"model":"agic-code-fast","max_tokens":50,"messages":[{"role":"user","content":"powershell backup restore verification"}]}' \
    http://127.0.0.1:8086/v1/chat/completions
}

command -v pwsh >/dev/null 2>&1 || fail_with_diagnostics "pwsh is required for the PowerShell backup/restore smoke."

python3 tests/backend/integration/mock_llm.py --port 3494 --prefix /backup-restore --name backup-restore-powershell > /tmp/llmproxy-backup-restore-powershell-mock.log 2>&1 &
MOCK_PID="$!"
sleep 1

if ! docker image inspect llmproxy:ci >/dev/null 2>&1; then
  docker build -f docker/Dockerfile -t llmproxy:ci .
fi

rm -f "$BACKUP_FILE" "${BACKUP_FILE}.sha256" "${BACKUP_FILE}.meta"
"${COMPOSE[@]}" up -d || fail_with_diagnostics "PowerShell backup/restore stack failed to start."
wait_http http://127.0.0.1:8086/readyz 60 || fail_with_diagnostics "PowerShell source gateway did not become ready."
wait_node_healthy || fail_with_diagnostics "PowerShell source DGX route did not become Healthy."

group_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' \
  -d '{"name":"PowerShell Restore Team","description":"PowerShell backup restore verification group"}' \
  http://127.0.0.1:8086/api/admin/usage-groups)"
group_id="$(echo "$group_json" | jq -r '.id')"

credential_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' \
  -d '{"name":"PowerShell Restore Client"}' \
  http://127.0.0.1:8086/api/admin/api-credentials)"
credential_id="$(echo "$credential_json" | jq -r '.id')"
credential_secret="$(echo "$credential_json" | jq -r '.secret')"
[[ "$credential_secret" == lp_* ]] || fail_with_diagnostics "PowerShell source credential secret was not returned."

curl --fail --silent -X PUT -H 'Content-Type: application/json' \
  -d "{\"usageGroupId\":\"${group_id}\"}" \
  "http://127.0.0.1:8086/api/admin/api-credentials/${credential_id}/usage-group" >/dev/null

policy_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' \
  -d "{\"apiCredentialId\":\"${credential_id}\",\"logicalModel\":\"agic-code-fast\",\"requestsPerWindow\":25,\"windowSeconds\":120,\"enabled\":true}" \
  http://127.0.0.1:8086/api/admin/rate-limits)"
policy_id="$(echo "$policy_json" | jq -r '.id')"
curl --fail --silent -X PUT -H 'Content-Type: application/json' \
  -d '{"outputTokensPerWindow":1200,"maxOutputTokensPerRequest":120}' \
  "http://127.0.0.1:8086/api/admin/rate-limits/${policy_id}/output-token-budget" >/dev/null

source_status="$(call_model "$credential_secret" /tmp/backup-restore-powershell-source)"
[[ "$source_status" == "200" ]] || fail_with_diagnostics "PowerShell source credential inference failed; got ${source_status}."

COMPOSE_FILE="$COMPOSE_FILE" pwsh -NoProfile -File docker/scripts/postgres-backup.ps1 -BackupFile "$BACKUP_FILE"
[[ -s "$BACKUP_FILE" && -s "${BACKUP_FILE}.sha256" && -s "${BACKUP_FILE}.meta" ]] \
  || fail_with_diagnostics "PowerShell backup artifact/checksum/metadata was not created."
grep -q 'Authentication__ApiKeyPepper' "${BACKUP_FILE}.meta" \
  || fail_with_diagnostics "PowerShell backup metadata did not warn about external pepper preservation."

mutation_json="$(curl --fail --silent -X POST -H 'Content-Type: application/json' \
  -d '{"name":"Post Backup Mutation","description":"must disappear after PowerShell restore"}' \
  http://127.0.0.1:8086/api/admin/usage-groups)"
mutation_id="$(echo "$mutation_json" | jq -r '.id')"

before_restore_groups="$(curl --fail --silent http://127.0.0.1:8086/api/admin/usage-groups)"
echo "$before_restore_groups" | jq -e --arg id "$mutation_id" 'map(select(.id == $id)) | length == 1' >/dev/null \
  || fail_with_diagnostics "Post-backup mutation was not visible before PowerShell restore."

COMPOSE_FILE="$COMPOSE_FILE" RESTORE_START_GATEWAY=true \
  pwsh -NoProfile -File docker/scripts/postgres-restore.ps1 -BackupFile "$BACKUP_FILE" -ConfirmDestructive

wait_http http://127.0.0.1:8086/readyz 60 || fail_with_diagnostics "PowerShell restored gateway did not become ready."
wait_node_healthy || fail_with_diagnostics "PowerShell restored route catalog did not recover."

restored_status="$(call_model "$credential_secret" /tmp/backup-restore-powershell-restored)"
[[ "$restored_status" == "200" ]] || fail_with_diagnostics "PowerShell-restored credential failed inference; got ${restored_status}."

restored_groups="$(curl --fail --silent http://127.0.0.1:8086/api/admin/usage-groups)"
echo "$restored_groups" | jq -e --arg keep "$group_id" --arg removed "$mutation_id" \
  '(map(select(.id == $keep)) | length == 1) and (map(select(.id == $removed)) | length == 0)' >/dev/null \
  || fail_with_diagnostics "PowerShell restore did not roll durable Usage Group state back to the backup point."

restored_credentials="$(curl --fail --silent http://127.0.0.1:8086/api/admin/governance/credentials)"
echo "$restored_credentials" | jq -e --arg id "$credential_id" --arg group "$group_id" \
  'map(select(.id == $id and .usageGroupId == $group and .enabled == true)) | length == 1' >/dev/null \
  || fail_with_diagnostics "PowerShell restore did not preserve credential identity/group membership."

restored_policies="$(curl --fail --silent http://127.0.0.1:8086/api/admin/rate-limits)"
echo "$restored_policies" | jq -e --arg id "$policy_id" --arg credential "$credential_id" \
  'map(select(.id == $id and .apiCredentialId == $credential and .requestsPerWindow == 25 and .windowSeconds == 120 and .outputTokensPerWindow == 1200 and .maxOutputTokensPerRequest == 120 and .enabled == true)) | length == 1' >/dev/null \
  || fail_with_diagnostics "PowerShell restore did not preserve caller-governance policy."

echo "PowerShell backup/restore smoke passed: binary docker compose cp backup, checksum, destructive restore, rollback-to-backup state and credential/governance recovery verified."
