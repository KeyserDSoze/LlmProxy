# Data retention

LlmProxy stores metadata-only inference history in `request_metrics` and administrative history in `audit_events`. These datasets intentionally have separate retention policies because they serve different operational/compliance purposes.

## Defaults

```text
request metrics: 90 days
audit events:    365 days
cleanup:         every 24 hours
batch size:      5000 rows
```

The values are configuration, not database schema constants.

Environment variables used by the Docker deployment:

```text
RETENTION_ENABLED=true
RETENTION_REQUEST_METRICS_DAYS=90
RETENTION_AUDIT_EVENTS_DAYS=365
RETENTION_INTERVAL_HOURS=24
RETENTION_BATCH_SIZE=5000
```

They map to ASP.NET configuration:

```text
Retention:Enabled
Retention:RequestMetricsDays
Retention:AuditEventsDays
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

`DataRetentionWorker` runs outside the inference path. When enabled it performs one cleanup after application startup and then repeats on the configured interval.

Deletion is intentionally batched:

```text
select oldest IDs before cutoff, up to BatchSize
    -> delete those IDs
    -> repeat until no eligible rows remain
```

This avoids loading full metric/audit payloads into application memory and avoids one unbounded delete operation against a large history table.

Current cutoffs:

```text
request_metrics.StartedAtUtc < now - RequestMetricsDays
audit_events.OccurredAtUtc  < now - AuditEventsDays
```

Retention work uses PostgreSQL in background and is independent from inference authentication/routing/admission. A cleanup failure is logged and retried on a later iteration; it must not stop inference.

## Admin API

Read active configuration:

```http
GET /api/admin/retention
```

Run an immediate cleanup:

```http
POST /api/admin/retention/run
```

The manual endpoint returns cutoffs and deleted-row counts. A successful manual run writes a current `retention.cleanup.run` audit event after the old audit rows have been deleted.

With Entra enabled:

```text
GET  -> AdminRead
POST -> AdminWrite
```

## Disabling the timer

```text
RETENTION_ENABLED=false
```

disables the automatic background schedule. The explicit admin run endpoint remains available for maintenance/testing.

## Usage reporting consequence

Deleting raw `request_metrics` also limits the maximum historical detail available to current usage reports. Therefore do not increase reporting windows beyond the retained raw history and expect old raw data to exist.

If the product later needs year-over-year usage analytics while keeping raw request metadata only 30–90 days, add daily/monthly aggregate rollups before deletion:

```text
raw request_metrics
    -> daily UsageRollup
    -> retention deletes raw rows
    -> long-term reporting reads rollups
```

Do not preserve prompts or generated content as part of rollups.

## Audit retention

Audit has a separate and longer default because administrative/security history often needs different governance. Before production rollout, confirm the required audit duration with the customer's security/compliance policy.

If immutable/exported audit is required later, export to an external security/log platform before deletion rather than making the inference database an indefinite archive.

## Validation contract

The dedicated Docker smoke disables the automatic worker, inserts:

```text
request metric older than metric cutoff
request metric newer than cutoff
audit event older than audit cutoff
audit event newer than cutoff
```

It then runs the admin cleanup and verifies:

- exactly the expired request metric is removed;
- exactly the expired audit event is removed;
- recent rows remain;
- the manual cleanup creates a current audit event;
- request-metric and audit cutoffs are independent.

The feature is `VALIDATED` only after this smoke and the complete repository quality gate are green.
