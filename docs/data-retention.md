# Data retention

LlmProxy stores metadata-only inference history in `request_metrics`, administrative history in `audit_events`, and delivered runtime publication history in `runtime_state_outbox`. These datasets have independent retention because they serve different purposes.

## Defaults

```text
request metrics:                 90 days
audit events:                   365 days
processed runtime-state outbox:  30 days
cleanup:                         every 24 hours
batch size:                      5000 rows
```

Environment variables:

```text
RETENTION_ENABLED=true
RETENTION_REQUEST_METRICS_DAYS=90
RETENTION_AUDIT_EVENTS_DAYS=365
RETENTION_RUNTIME_STATE_OUTBOX_DAYS=30
RETENTION_INTERVAL_HOURS=24
RETENTION_BATCH_SIZE=5000
```

ASP.NET configuration mapping:

```text
Retention:Enabled
Retention:RequestMetricsDays
Retention:AuditEventsDays
Retention:RuntimeStateOutboxDays
Retention:IntervalHours
Retention:BatchSize
```

Safety bounds:

```text
retention days: 1..3650
interval hours: 1..168
batch size:     100..50000
```

## Background cleanup

`DataRetentionWorker` runs outside the inference path. When enabled it performs cleanup after application startup and repeats on the configured interval.

Deletion is batched: select eligible IDs up to `BatchSize`, delete those IDs, repeat until no eligible rows remain. Cleanup failures are logged/retried later and do not stop inference.

Cutoffs:

```text
request_metrics.StartedAtUtc < now - RequestMetricsDays
audit_events.OccurredAtUtc  < now - AuditEventsDays
runtime_state_outbox.ProcessedAtUtc < now - RuntimeStateOutboxDays
```

### Critical outbox safety rule

Outbox retention includes the predicate:

```text
ProcessedAtUtc != null
```

A pending outbox row is **never** eligible for retention deletion, even if `OccurredAtUtc` is much older than the retention window or it has failed many delivery attempts. Pending rows represent committed configuration changes that still require distributed publication and must remain durable until processed.

## Admin API

Read active configuration:

```http
GET /api/admin/retention
```

Run cleanup immediately:

```http
POST /api/admin/retention/run
```

The response includes cutoffs and deletion counts for request metrics, audit events and processed runtime-state outbox rows. A successful manual run records `retention.cleanup.run` after cleanup.

With Entra enabled:

```text
GET  -> AdminRead
POST -> AdminWrite
```

`RETENTION_ENABLED=false` disables only the background timer; manual cleanup remains available.

## Reporting consequence

Deleting raw `request_metrics` limits detailed historical usage reporting to retained history. If year-over-year reporting is later required while keeping raw metadata for only 30–90 days, add aggregate daily/monthly rollups before raw deletion. Never retain prompt/source/output bodies in rollups.

## Audit consequence

Audit has a longer default because administrative/security history often has different governance. Production duration must be confirmed against customer security/compliance requirements. If immutable external audit is required, export it before database retention rather than using the inference database as an indefinite archive.

## Validation contract

The Docker retention smoke disables the automatic worker and inserts old/recent request metrics, old/recent audit events, an old processed outbox row and an old pending outbox row. It then runs manual cleanup and verifies:

- expired request metric is removed and recent metric remains;
- expired audit event is removed and recent audit remains;
- expired **processed** outbox row is removed;
- old **pending** outbox row remains;
- cleanup deletion counts are correct;
- the manual run creates a current audit event.

Canonical validated evidence:

```text
commit 79de2dfd7c995b5a6cac7e87fcf89e3e991d9d72
CI     34976465066 SUCCESS
```
