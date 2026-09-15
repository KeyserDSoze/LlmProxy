# Runtime cache architecture

LlmProxy keeps durable configuration/history in PostgreSQL, distributed coordination/runtime L2 state in Redis when enabled, and latency-sensitive request-path decisions in local RAM.

## Current validated topology

```text
PostgreSQL = durable source of truth
Redis      = distributed L2 snapshots/version/events + shared counters/leases
local RAM  = per-gateway request-path L1
```

After startup, ordinary inference resolves the following without synchronous PostgreSQL reads:

```text
Bearer/API credential
  -> credential snapshot / UsageGroup
  -> request-rate policy
  -> shared rate counter when Redis is enabled
  -> logical model
  -> deployment/node route catalog
  -> routing/performance state
  -> shared capacity lease when Redis is enabled
  -> vLLM
```

PostgreSQL remains required for durable configuration, migrations/startup rebuild, control-plane changes, history/reporting, retention and background persistence.

## Runtime abstractions

Inference code depends on provider-neutral contracts rather than directly on cache technology. Important runtime abstractions include:

```text
IApiCredentialCache
IRouteCatalog / IDeploymentCatalog
IRateLimitCounterStore
IRequestCapacityGate / IRequestCapacityLease
IRuntimeStateEventSink
IRuntimeStateSyncStatus
IDeploymentPerformanceTracker
INodeRuntimeMetricsTracker
IRequestLoadTracker
```

Redis-disabled deployments use local providers. Redis-enabled deployments replace only the shared coordination pieces while preserving local L1 request-path state.

## Runtime configuration synchronization

At startup:

```text
PostgreSQL
  -> rebuild local route / credential / rate-policy snapshots
  -> publish canonical runtime snapshots to Redis
```

For live mutations today:

```text
Admin / health mutation
  -> EF tracked entity
  -> PostgreSQL SaveChanges succeeds
  -> SavedChanges interceptor
       -> update local L1
       -> enqueue runtime change to Redis coordinator
  -> coordinator persists shared Redis state
  -> publish change/version event
  -> peer replicas update local L1
```

Redis pub/sub is not the only recovery mechanism. The coordinator also maintains version/snapshot state and periodic reconciliation so a replica can heal missed notifications after reconnect.

`GET /api/admin/runtime-sync` exposes synchronization/provider diagnostics including connectivity and event/version state.

## Shared caller governance

When Redis is enabled, request-rate counters are global across gateway replicas rather than process-local. A policy admitted through gateway A is therefore visible to gateway B against the same credential/model/window.

The runtime policy definition still lives in local L1 for fast lookup; only the counter coordination needs the Redis round trip.

When Redis is disabled, the in-memory counter provider preserves single-instance behavior.

## Shared physical capacity

When Redis is enabled, node/deployment capacity admission uses Redis-backed leases rather than process-local counters alone.

The lease operation is atomic across the shared deployment and physical-node keys. Each active request owns a lease with a Redis expiry and a renewal loop. Local request-load tracking is still maintained for same-process routing telemetry and is released together with the distributed lease.

Important fail-closed behavior:

```text
Redis unavailable during admission
  -> do not guess capacity
  -> 503 capacity_coordination_unavailable

Redis lease renewal becomes unsafe during active inference
  -> signal CoordinationLost
  -> cancel upstream/read/write before the Redis lease can expire
  -> if response not started: 503 + Retry-After: 1 + capacity_lease_lost
  -> if SSE already started: abort connection
  -> persist/trace capacity_lease_lost
```

The safety watchdog uses monotonic elapsed-time tracking and polls substantially faster than the renewal interval so scheduler/timer boundary jitter cannot defer cancellation until the Redis TTL itself.

## Why local L1 remains mandatory

Redis is deliberately not used as a mandatory remote lookup for every configuration decision. Route, credential and policy snapshots remain local because:

```text
local L1 lookup = no network hop, low latency, survives short Redis outages
Redis L2        = shared synchronization and coordination
PostgreSQL      = durable recovery authority
```

The request path should not be redesigned into Redis-on-every-request for route/credential lookup without an explicit architecture decision.

## Current durability boundary

The remaining important correctness gap is the PostgreSQL commit -> Redis publication crash window.

Today, configuration publication occurs after `SaveChanges` has committed. The EF SavedChanges interceptor updates local L1 and calls `IRuntimeStateEventSink.Publish...`; `RedisRuntimeStateCoordinator` puts the outbound message on an in-memory channel and later persists/publishes it to Redis.

This means:

```text
DB commit succeeds
process crashes before Redis durable publication
=> peer replicas may temporarily miss the committed mutation
```

Periodic reconciliation/startup rebuild can repair state, but this is not the same guarantee as a durable transactional publication pipeline.

## Next phase — PostgreSQL transactional outbox

The next implementation should close that window without introducing a distributed transaction:

```text
PostgreSQL transaction
  - update Node/Model/Deployment/Credential/RatePolicy
  - insert RuntimeStateOutbox row
commit

Outbox worker
  -> perform one durable Redis publication attempt
     - persist required shared state/snapshot/version
     - publish change notification
  -> mark outbox row delivered only after Redis acknowledges success
```

Design requirements:

- outbox row and configuration mutation must commit in the same PostgreSQL transaction;
- retries must be idempotent;
- failed Redis attempts remain pending with retry/backoff metadata;
- delivery/lag/failure must be observable;
- workers must safely handle restart/replay;
- integration tests must cover Redis outage followed by recovery;
- the existing fire-and-forget `IRuntimeStateEventSink` call is **not** a durable acknowledgement boundary;
- do not mark an outbox row processed merely because a message was enqueued to the coordinator's in-memory channel.

The clean architecture is to introduce/refactor a durable Redis dispatcher method that completes only after the Redis state write and pub/sub publication have succeeded, and let the outbox worker use that method.

## Optional cold-start evolution

A future replica may load a complete versioned snapshot from Redis for faster scale-out, while falling back to PostgreSQL if Redis is unavailable or the snapshot/version is invalid. PostgreSQL remains the recovery authority.

## Security-sensitive consistency

Credential revocation is synchronized through the same runtime pipeline. If product requirements later demand an even stricter immediate-global-revocation SLA than the outbox/reconciliation pipeline provides, add a credential-specific mechanism rather than weakening all request-path caching.

Never store raw API keys, prompts, generated code or bearer tokens in Redis.

## Current validation

The runtime/Redis architecture is validated by both the standard repository CI and the dedicated full-stack smoke.

Canonical evidence:

```text
commit     6ec3c29176584f2e0bffd98b5d8cbbb0e833e76f
CI         34961566507 SUCCESS
Full Stack 34961566463 SUCCESS
```

The Full Stack Smoke proves:

- Redis runtime-state synchronization across replicas;
- global rate-limit counters;
- distributed node/deployment capacity leases;
- lease release/recovery;
- fail-closed cancellation before lease expiry when Redis disappears;
- `capacity_lease_lost` request metric and trace visibility;
- normal LlmProxy application spans exported through OTLP/Tempo.