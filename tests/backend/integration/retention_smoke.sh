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
export RETENTION_USAGE_ROLLUPS_DAYS="365"
export RETENTION_AUDIT_EVENTS_DAYS="180"
export RETENTION_RUNTIME_STATE_OUTBOX_DAYS="30"
export RETENTION_INTERVAL_HOURS="24"
export RETENTION_BATCH_SIZE="100"

if ! "${COMPOSE[@]}" up -d --build; then
  fail_with_diagnostics "Docker Compose stack failed to start for retention smoke test."
fi
wait_ready

settings="$(curl --fail --silent http://127.0.0.1:8080/api/admin/retention)"
echo "$settings" | jq -e '.enabled == false and .requestMetricsDays == 30 and .usageRollupsDays == 365 and .auditEventsDays == 180 and .runtimeStateOutboxDays == 30 and .batchSize == 100' >/dev/null

"${COMPOSE[@]}" exec -T postgres psql -v ON_ERROR_STOP=1 -U "${POSTGRES_USER:-llmproxy}" -d "${POSTGRES_DB:-llmproxy}" <<'SQL' >/dev/null
INSERT INTO request_metrics
    ("RequestId", "StartedAtUtc", "LogicalModel", "Surface", "StatusCode", "DurationMilliseconds", "AttemptCount", "IsStreaming", "TimeToFirstByteMilliseconds", "InputTokens", "OutputTokens", "TotalTokens")
VALUES
    ('00000000-0000-0000-0000-000000000031', NOW() - INTERVAL '31 days', 'retention-old', 'chat_completions', 200, 10, 1, FALSE, 5, 10, 4, 14),
    ('00000000-0000-0000-0000-000000000001', NOW() - INTERVAL '1 day', 'retention-recent', 'chat_completions', 200, 10, 1, FALSE, 4, 3, 2, 5);

INSERT INTO daily_usage_rollups
    ("DayUtc", "ApiCredentialId", "UsageGroupId", "LogicalModel", "RequestCount", "ErrorCount", "InputTokens", "OutputTokens", "TotalTokens", "RateLimitedRequests", "CapacityExhaustedRequests", "DurationMillisecondsTotal", "TtftMillisecondsTotal", "TtftSampleCount")
VALUES
    (CURRENT_DATE - 366, '00000000-0000-0000-0000-000000000000', '00000000-0000-0000-0000-000000000000', 'expired-rollup', 1, 0, 1, 1, 2, 0, 0, 10, 5, 1);

INSERT INTO audit_events
    ("OccurredAtUtc", "Actor", "Action", "EntityType", "EntityId")
VALUES
    (NOW() - INTERVAL '181 days', 'retention-smoke', 'retention.old', 'test', 'old'),
    (NOW() - INTERVAL '1 day', 'retention-smoke', 'retention.recent', 'test', 'recent');

INSERT INTO runtime_state_outbox
    ("Kind", "Action", "OccurredAtUtc", "AttemptCount", "ProcessedAtUtc")
VALUES
    ('retention-test', 'processed-old', NOW() - INTERVAL '31 days', 1, NOW() - INTERVAL '31 days'),
    ('retention-test', 'processed-recent', NOW() - INTERVAL '1 day', 1, NOW() - INTERVAL '1 day'),
    ('retention-test', 'pending-old', NOW() - INTERVAL '60 days', 7, NULL);
SQL

result="$(curl --fail --silent -X POST http://127.0.0.1:8080/api/admin/retention/run)"
echo "$result" | jq -e '.rolledUpRequestMetricDays == 1 and .deletedRequestMetrics == 1 and .deletedUsageRollups == 1 and .deletedAuditEvents == 1 and .deletedRuntimeStateOutbox == 1' >/dev/null

metric_old_count="$("${COMPOSE[@]}" exec -T postgres psql -At -U "${POSTGRES_USER:-llmproxy}" -d "${POSTGRES_DB:-llmproxy}" -c "SELECT count(*) FROM request_metrics WHERE \"LogicalModel\" = 'retention-old';")"
metric_recent_count="$("${COMPOSE[@]}" exec -T postgres psql -At -U "${POSTGRES_USER:-llmproxy}" -d "${POSTGRES_DB:-llmproxy}" -c "SELECT count(*) FROM request_metrics WHERE \"LogicalModel\" = 'retention-recent';")"
rollup_old_count="$("${COMPOSE[@]}" exec -T postgres psql -At -U "${POSTGRES_USER:-llmproxy}" -d "${POSTGRES_DB:-llmproxy}" -c "SELECT count(*) FROM daily_usage_rollups WHERE \"LogicalModel\" = 'retention-old' AND \"RequestCount\" = 1 AND \"TotalTokens\" = 14;")"
rollup_expired_count="$("${COMPOSE[@]}" exec -T postgres psql -At -U "${POSTGRES_USER:-llmproxy}" -d "${POSTGRES_DB:-llmproxy}" -c "SELECT count(*) FROM daily_usage_rollups WHERE \"LogicalModel\" = 'expired-rollup';")"
audit_old_count="$("${COMPOSE[@]}" exec -T postgres psql -At -U "${POSTGRES_USER:-llmproxy}" -d "${POSTGRES_DB:-llmproxy}" -c "SELECT count(*) FROM audit_events WHERE \"Action\" = 'retention.old';")"
audit_recent_count="$("${COMPOSE[@]}" exec -T postgres psql -At -U "${POSTGRES_USER:-llmproxy}" -d "${POSTGRES_DB:-llmproxy}" -c "SELECT count(*) FROM audit_events WHERE \"Action\" = 'retention.recent';")"
outbox_old_processed_count="$("${COMPOSE[@]}" exec -T postgres psql -At -U "${POSTGRES_USER:-llmproxy}" -d "${POSTGRES_DB:-llmproxy}" -c "SELECT count(*) FROM runtime_state_outbox WHERE \"Action\" = 'processed-old';")"
outbox_recent_processed_count="$("${COMPOSE[@]}" exec -T postgres psql -At -U "${POSTGRES_USER:-llmproxy}" -d "${POSTGRES_DB:-llmproxy}" -c "SELECT count(*) FROM runtime_state_outbox WHERE \"Action\" = 'processed-recent';")"
outbox_old_pending_count="$("${COMPOSE[@]}" exec -T postgres psql -At -U "${POSTGRES_USER:-llmproxy}" -d "${POSTGRES_DB:-llmproxy}" -c "SELECT count(*) FROM runtime_state_outbox WHERE \"Action\" = 'pending-old' AND \"ProcessedAtUtc\" IS NULL;")"

[[ "$metric_old_count" == "0" && "$metric_recent_count" == "1" ]] || fail_with_diagnostics "Request-metric retention removed the wrong rows."
[[ "$rollup_old_count" == "1" && "$rollup_expired_count" == "0" ]] || fail_with_diagnostics "Daily usage rollup retention/compaction is incorrect."
[[ "$audit_old_count" == "0" && "$audit_recent_count" == "1" ]] || fail_with_diagnostics "Audit retention removed the wrong rows."
[[ "$outbox_old_processed_count" == "0" && "$outbox_recent_processed_count" == "1" ]] || fail_with_diagnostics "Processed outbox retention removed the wrong rows."
[[ "$outbox_old_pending_count" == "1" ]] || fail_with_diagnostics "Retention must never delete an undelivered outbox row, regardless of age."

usage="$(curl --fail --silent 'http://127.0.0.1:8080/api/admin/usage/summary?days=60')"
echo "$usage" | jq -e '
  .windowGranularity == "utc_day" and
  .requestCount == 2 and
  .rawRequestCount == 1 and
  .rolledUpRequestCount == 1 and
  .historicalRollupsUsed == true and
  .totalTokens == 19 and
  (.models | any(.logicalModel == "retention-old" and .requestCount == 1 and .totalTokens == 14)) and
  (.models | any(.logicalModel == "retention-recent" and .requestCount == 1 and .totalTokens == 5))
' >/dev/null

second_result="$(curl --fail --silent -X POST http://127.0.0.1:8080/api/admin/retention/run)"
echo "$second_result" | jq -e '.rolledUpRequestMetricDays == 0 and .deletedRequestMetrics == 0 and .deletedUsageRollups == 0' >/dev/null
rollup_after_second_run="$("${COMPOSE[@]}" exec -T postgres psql -At -U "${POSTGRES_USER:-llmproxy}" -d "${POSTGRES_DB:-llmproxy}" -c "SELECT count(*) FROM daily_usage_rollups WHERE \"LogicalModel\" = 'retention-old';")"
audit_run_count="$("${COMPOSE[@]}" exec -T postgres psql -At -U "${POSTGRES_USER:-llmproxy}" -d "${POSTGRES_DB:-llmproxy}" -c "SELECT count(*) FROM audit_events WHERE \"Action\" = 'retention.cleanup.run';")"
[[ "$rollup_after_second_run" == "1" ]] || fail_with_diagnostics "Repeated retention cleanup duplicated or removed the historical rollup."
[[ "$audit_run_count" == "2" ]] || fail_with_diagnostics "Manual retention cleanup was not audited on each run."

echo "Retention smoke suite passed: atomic daily rollup, raw/rollup reporting, rollup retention, idempotency, audit/processed-outbox cutoffs and pending-outbox safety verified."
