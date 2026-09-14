#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$ROOT_DIR"

COMPOSE=(docker compose -f docker/docker-compose.yml)

cleanup() {
  "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT

fail_with_diagnostics() {
  echo "$1" >&2
  "${COMPOSE[@]}" ps -a >&2 || true
  "${COMPOSE[@]}" logs --no-color >&2 || true
  exit 1
}

wait_ready() {
  for attempt in {1..30}; do
    if curl --fail --silent http://127.0.0.1:8080/readyz >/dev/null; then
      return 0
    fi
    sleep 2
  done
  fail_with_diagnostics "Gateway did not become ready for retention smoke test."
}

export LLM_PROXY_API_KEY="retention-test-key"
export LLM_PROXY_API_KEY_PEPPER="retention-test-pepper"
export ENTRA_ENABLED="false"
export BOOTSTRAP_ENABLED="false"
export RUNTIME_METRICS_ENABLED="false"
export HARDWARE_METRICS_ENABLED="false"
export RETENTION_ENABLED="false"
export RETENTION_REQUEST_METRICS_DAYS="30"
export RETENTION_AUDIT_EVENTS_DAYS="180"
export RETENTION_INTERVAL_HOURS="24"
export RETENTION_BATCH_SIZE="100"

if ! "${COMPOSE[@]}" up -d --build; then
  fail_with_diagnostics "Docker Compose stack failed to start for retention smoke test."
fi
wait_ready

settings="$(curl --fail --silent http://127.0.0.1:8080/api/admin/retention)"
echo "$settings" | jq -e '.enabled == false and .requestMetricsDays == 30 and .auditEventsDays == 180 and .batchSize == 100' >/dev/null

"${COMPOSE[@]}" exec -T postgres psql -v ON_ERROR_STOP=1 -U "${POSTGRES_USER:-llmproxy}" -d "${POSTGRES_DB:-llmproxy}" <<'SQL' >/dev/null
INSERT INTO request_metrics
    ("RequestId", "StartedAtUtc", "LogicalModel", "Surface", "StatusCode", "DurationMilliseconds", "AttemptCount", "IsStreaming")
VALUES
    ('00000000-0000-0000-0000-000000000031', NOW() - INTERVAL '31 days', 'retention-old', 'chat_completions', 200, 10, 1, FALSE),
    ('00000000-0000-0000-0000-000000000001', NOW() - INTERVAL '1 day', 'retention-recent', 'chat_completions', 200, 10, 1, FALSE);

INSERT INTO audit_events
    ("OccurredAtUtc", "Actor", "Action", "EntityType", "EntityId")
VALUES
    (NOW() - INTERVAL '181 days', 'retention-smoke', 'retention.old', 'test', 'old'),
    (NOW() - INTERVAL '1 day', 'retention-smoke', 'retention.recent', 'test', 'recent');
SQL

result="$(curl --fail --silent -X POST http://127.0.0.1:8080/api/admin/retention/run)"
echo "$result" | jq -e '.deletedRequestMetrics == 1 and .deletedAuditEvents == 1' >/dev/null

metric_old_count="$("${COMPOSE[@]}" exec -T postgres psql -At -U "${POSTGRES_USER:-llmproxy}" -d "${POSTGRES_DB:-llmproxy}" -c "SELECT count(*) FROM request_metrics WHERE \"LogicalModel\" = 'retention-old';")"
metric_recent_count="$("${COMPOSE[@]}" exec -T postgres psql -At -U "${POSTGRES_USER:-llmproxy}" -d "${POSTGRES_DB:-llmproxy}" -c "SELECT count(*) FROM request_metrics WHERE \"LogicalModel\" = 'retention-recent';")"
audit_old_count="$("${COMPOSE[@]}" exec -T postgres psql -At -U "${POSTGRES_USER:-llmproxy}" -d "${POSTGRES_DB:-llmproxy}" -c "SELECT count(*) FROM audit_events WHERE \"Action\" = 'retention.old';")"
audit_recent_count="$("${COMPOSE[@]}" exec -T postgres psql -At -U "${POSTGRES_USER:-llmproxy}" -d "${POSTGRES_DB:-llmproxy}" -c "SELECT count(*) FROM audit_events WHERE \"Action\" = 'retention.recent';")"
audit_run_count="$("${COMPOSE[@]}" exec -T postgres psql -At -U "${POSTGRES_USER:-llmproxy}" -d "${POSTGRES_DB:-llmproxy}" -c "SELECT count(*) FROM audit_events WHERE \"Action\" = 'retention.cleanup.run';")"

[[ "$metric_old_count" == "0" && "$metric_recent_count" == "1" ]] || fail_with_diagnostics "Request-metric retention removed the wrong rows."
[[ "$audit_old_count" == "0" && "$audit_recent_count" == "1" ]] || fail_with_diagnostics "Audit retention removed the wrong rows."
[[ "$audit_run_count" == "1" ]] || fail_with_diagnostics "Manual retention cleanup was not audited."

echo "Retention smoke suite passed: independent request-metric/audit cutoffs and manual audited cleanup verified."
