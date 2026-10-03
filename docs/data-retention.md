# Data retention and historical usage rollups

LlmProxy stores metadata-only inference history in `request_metrics`, durable historical usage aggregates in `usage_daily_rollups`, administrative history in `audit_events`, and runtime publication history in `runtime_state_outbox`.

## Defaults

```text
raw request metrics:             90 days
daily usage rollups:            730 days
audit events:                   365 days
processed runtime-state outbox:  30 days
cleanup:                         every 24 hours
batch size:                      5000 rows
```

Environment variables:

```text
RETENTION_ENABLED=true
RETENTION_REQUEST_METRICS_DAYS=90
RETENTION_USAGE_ROLLUPS_DAYS=730
RETENTION_AUDIT_EVENTS_DAYS=365
RETENTION_RUNTIME_STATE_OUTBOX_DAYS=30
RETENTION_INTERVAL_HOURS=24
RETENTION_BATCH_SIZE=5000
```

ASP.NET mapping:

```text
Retention:Enabled
Retention:RequestMetricsDays
Retention:UsageRollupsDays
Retention:AuditEventsDays
Retention:RuntimeStateOutboxDays
Retention:IntervalHours
Retention:BatchSize
```

Retention days are clamped to safe configured bounds; interval and batch size are bounded as well.

## Raw request metrics -> daily rollups

Before an expired raw request-metric day is deleted, LlmProxy compacts complete UTC calendar days into `usage_daily_rollups`.

Rollup key:

```text
UTC day
+ ApiCredentialId (or internal no-credential sentinel)
+ UsageGroupId (or internal ungrouped sentinel)
+ LogicalModel
```

The rollup stores counts/sums needed by Usage & Governance reporting, including requests, errors, input/output/total tokens, rate-limited requests, capacity-exhausted requests, duration totals and TTFT sample totals/counts.

No prompt, source code, generated output or raw API secret is stored in rollups.

### Atomicity and HA safety

Compaction is serialized across replicas with a PostgreSQL advisory **transaction** lock. For each retention iteration, rollup creation/update and deletion of corresponding expired raw rows occur inside a database transaction.

Important consequences:

- crash before commit -> neither rollup nor deletion becomes visible;
- successful commit -> aggregate and raw deletion become visible together;
- a second gateway cannot concurrently compact the same logical work while the advisory lock is held;
- rerunning retention is idempotent and does not double-count an already compacted/deleted raw day.

## Reporting semantics

Usage reporting is based on **UTC calendar-day windows**. This makes historical daily rollups deterministic.

For a requested window, the reporting reader combines:

```text
historical rows from usage_daily_rollups
+
newer/uncompacted rows from request_metrics
```

The two sources are partitioned so one request is not counted twice. The API response exposes:

```text
rawRequestCount
rolledUpRequestCount
historicalRollupsUsed
rawRetentionDays
rollupRetentionDays
```

The Admin Usage & Governance page supports windows up to 730 days and visibly indicates when rollups contributed to the result.

Historical credential/group attribution uses the request-time snapshot captured in the raw metric before compaction. Rollup rows intentionally do not depend on foreign keys to live credential/group entities, so reporting history can outlive operational entity lifecycle changes.

## Other retention rules

Audit and runtime-outbox retention remain independent.

Critical outbox rule:

```text
ProcessedAtUtc != null
```

is required before an outbox row is eligible for deletion. Pending outbox rows are never retention-deleted, regardless of age or retry count.

Expired historical rollups are deleted according to `Retention:UsageRollupsDays`.

## Admin API

Read active settings:

```http
GET /api/admin/retention
```

Run cleanup immediately:

```http
POST /api/admin/retention/run
```

Manual cleanup remains available even when the background retention worker is disabled. With Entra enabled, read requires `AdminRead`; manual run requires `AdminWrite`.

The manual cleanup is audited as `retention.cleanup.run` with safe counts/cutoffs only.

## Validation contract

The Docker retention smoke proves:

- an expired raw metric is compacted into a daily rollup before deletion;
- a recent raw metric remains raw;
- a historical usage query still contains the expired request after raw deletion;
- raw + rolled-up request counts are disclosed correctly;
- running cleanup a second time does not duplicate the rollup;
- expired audit is removed and recent audit remains;
- expired **processed** outbox is removed;
- expired **pending** outbox remains;
- manual cleanup produces an audit event.

Canonical validated evidence:

```text
version 0.2.0-preview.1
commit  5d66c7dcdae42955c6e26849aba84bed4787ff00
CI      35075387110 SUCCESS
```

## Production guidance boundary

The 90/730/365/30-day defaults are product defaults, not customer compliance policy. Confirm production durations, backup destination/encryption and any immutable external audit/export requirements with the target environment. Full-body request audit can be configured up to 4015 days when policy requires it, but operators must size PostgreSQL, backups and encryption-key recovery accordingly. For analytical history alone, prefer aggregate rollups or exports instead of extending payload retention unnecessarily.

## Full-body inference content logs

Full request/response payload logging is intentionally separate from `request_metrics` and daily usage rollups.

Defaults and bounds:

```text
default retention      30 days
minimum retention      10 days
maximum retention    4015 days (11 x 365)
cleanup cadence         4 hours
storage                 PostgreSQL, application-encrypted ciphertext
global read              LlmProxy.Admin only\nself-service read         owner-scoped personal credentials only
```

Administrator endpoints:

```http
GET  /api/admin/content-logs
GET  /api/admin/content-logs/{id}
GET  /api/admin/content-logs/settings
PUT  /api/admin/content-logs/settings
POST /api/admin/content-logs/retention/run
```

`PUT /api/admin/content-logs/settings` accepts:

```json
{ "retentionDays": 30 }
```

Values below 10 or above 4015 are rejected. The hosted cleanup worker runs at startup and then every four hours, deleting rows whose `StartedAtUtc` is older than the active retention window.\n\nNormal-user self-service endpoints are:\n\n```http\nGET /api/me/content-logs\nGET /api/me/content-logs/{id}\n```\n\nThese endpoints derive identity from the authenticated Entra principal and return only rows whose `ApiCredentialId` belongs to a personal credential owned by the same stable `tid + oid`. Supplying another user's credential ID yields no rows, and direct detail access to a non-owned row returns not found. Organization/shared credentials are not exposed through self-service. Decrypted detail responses use `Cache-Control: no-store`.

This retention does not change request-metric/rollup/audit/outbox retention. Full-body content is excluded from telemetry rollups and OTEL export.
