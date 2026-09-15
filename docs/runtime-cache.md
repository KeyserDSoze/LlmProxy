# Runtime cache architecture

LlmProxy keeps durable configuration/history in PostgreSQL, distributed coordination/runtime L2 state in Redis when enabled, and latency-sensitive request-path decisions in local RAM.

## Current validated topology

```text
PostgreSQL = durable source of truth + transactional runtime-state outbox
Redis      = distributed L2 snapshots/version/events + shared counters/leases
local RAM  = per-gateway request-path L1
```

After startup, ordinary inference resolves credentials, Usage Groups, request-rate policy, logical models and deployment/node routes without synchronous PostgreSQL reads. Redis is used for shared rate/capacity coordination when enabled, not as a mandatory remote configuration lookup for every request.

## Runtime configuration publication

At startup, PostgreSQL rebuilds local runtime snapshots and canonical state is published to Redis.

For Redis-enabled live mutations the validated path is:

```text
Admin / health mutation
  -> EF tracks Node / Model / Deployment / Credential / RatePolicy
  -> RuntimeStateOutboxSaveChangesInterceptor adds runtime_state_outbox row
     in the SAME GatewayDbContext / PostgreSQL transaction
  -> PostgreSQL commit
  -> local post-save interceptors update originating replica L1

RuntimeStateOutboxWorker
  -> acquire PostgreSQL session advisory publisher lock
  -> read oldest pending rows ordered by Id
  -> if oldest row is backing off, stop: never overtake it
  -> IRuntimeStateDurablePublisher.PublishAsync
       -> Redis state/hash persistence
       -> Redis global runtime version increment
       -> Redis pub/sub change event
       -> apply acknowledged event to publishing replica's own L1
  -> mark ProcessedAtUtc only after all durable Redis work succeeds
```

This closes the former process-crash window between a committed database mutation and an in-memory outbound event.

When Redis is disabled, the outbox save interceptor and worker are not registered. Single-instance deployments therefore do not accumulate undeliverable Redis-publication rows.

## Why the publishing replica applies its own acknowledged event

Any gateway replica can win the PostgreSQL advisory lock, including a replica that did not originate the control-plane mutation. Redis pub/sub messages carry an origin instance id and the publisher ignores its own notification to avoid duplicate subscription work.

Therefore the durable publisher explicitly applies the acknowledged change to its own local L1 after Redis persistence/pub/sub succeeds. Without this step, a non-originating outbox worker could publish the correct distributed event yet remain locally stale.

Full Stack `34976465149` proves this case by stopping the originating gateway and requiring the surviving peer to replay and then enforce the new rate policy from its own L1.

## Ordering, retries and idempotency

Only one outbox publisher holds the PostgreSQL advisory lock at a time. Rows are processed by monotonically increasing `Id`. A failed or backoff-delayed oldest event blocks later events.

Failure metadata:

```text
AttemptCount
NextAttemptAtUtc
LastError
```

Retry delay is bounded exponential backoff. A success clears retry/error state and writes `ProcessedAtUtc`.

Delivery is at-least-once: Redis may have accepted a publication immediately before a worker process crashes and before PostgreSQL records `ProcessedAtUtc`. Replaying the row is safe because runtime persistence uses idempotent upsert/delete semantics and strict global ordering prevents an old replay overtaking a newer event.

## Runtime outbox diagnostics

`GET /api/admin/runtime-sync` returns normal Redis synchronization status plus:

```text
outbox.pendingCount
outbox.failedPendingCount
outbox.oldestPendingAtUtc
outbox.oldestPendingAgeSeconds
outbox.maxPendingAttemptCount
outbox.lastProcessedAtUtc
outbox.lastError
```

These are PostgreSQL control-plane queries executed only when the operator requests runtime diagnostics; they are not part of inference hot-path decisions.

Recommended operational signals:

- `pendingCount > 0` briefly can be normal during publication;
- sustained `oldestPendingAgeSeconds` growth indicates delivery lag;
- `failedPendingCount > 0` / non-null `lastError` indicates Redis publication failure;
- a growing backlog while Redis reports connected requires investigation.

The Full Stack smoke asserts diagnostics are clean before fault injection, show failed pending work during Redis outage and drain after recovery.

## Outbox retention

Processed outbox history is operational metadata and is retained separately from request/audit history. Default:

```text
Retention:RuntimeStateOutboxDays = 30
```

Only records with `ProcessedAtUtc != null` and a processed timestamp older than the cutoff are eligible for deletion. Pending records are never retention-deleted, regardless of age or retry state. The standard Docker retention smoke validates this explicitly.

## Shared caller governance

When Redis is enabled, request-rate counters are global across gateway replicas. Policy definitions remain local L1 for fast lookup; only shared admission counters require Redis coordination.

When Redis is disabled, the in-memory provider preserves single-instance behavior.

## Shared physical capacity

Redis-enabled node/deployment admission uses shared leases. Acquisition is atomic across deployment and physical-node capacity keys. Each request owns a TTL-backed lease with renewal; local load tracking remains available for same-process routing telemetry.

Fail-closed behavior:

```text
Redis unavailable during admission
  -> 503 capacity_coordination_unavailable

active Redis lease becomes unsafe
  -> cancel upstream/read/write before TTL expiry
  -> if response not started: 503 + Retry-After: 1 + capacity_lease_lost
  -> if SSE already started: abort connection
  -> persist + trace capacity_lease_lost
```

The safety watchdog uses monotonic time and polls substantially faster than the renewal interval so timer-boundary jitter cannot postpone cancellation to the Redis expiry itself.

## Why local L1 remains mandatory

```text
local L1   = no network hop, low latency, survives short Redis config-sync outages
Redis L2   = shared synchronization and coordination
PostgreSQL = durable recovery + outbox authority
```

Do not redesign route/credential/policy lookup into Redis-on-every-inference without an explicit architecture decision.

## Security-sensitive consistency

Credential revocation uses the same transactional runtime publication path. If a future requirement demands a stricter immediate-global-revocation SLA than the outbox worker latency provides, add a credential-specific mechanism rather than weakening all request-path caching.

Never store raw API keys, prompts, generated code or bearer tokens in Redis/outbox payloads.

## Operator configuration

Full-stack deployment exposes:

```text
REDIS_OUTBOX_BATCH_SIZE=50
REDIS_OUTBOX_POLL_MILLISECONDS=500
RETENTION_RUNTIME_STATE_OUTBOX_DAYS=30
```

Code safety clamps remain authoritative for out-of-range values.

## Validation

Canonical evidence:

```text
commit     79de2dfd7c995b5a6cac7e87fcf89e3e991d9d72
CI         34976465066 SUCCESS
Full Stack 34976465149 SUCCESS
```

The validation set proves cross-replica runtime sync, shared request-rate admission, distributed physical capacity, lease-loss safety, transactional outbox failure/replay, non-originating peer L1 application, outbox diagnostics and processed-only outbox retention.
