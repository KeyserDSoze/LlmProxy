# Runtime cache architecture

LlmProxy keeps durable configuration/history in PostgreSQL, distributed runtime L2/coordination in Redis when enabled, and latency-sensitive configuration decisions in local RAM.

## Validated topology

```text
PostgreSQL = durable source of truth + transactional runtime-state outbox
Redis      = distributed L2 snapshots/version/events + shared counters/leases/budgets
local RAM  = per-gateway request-path configuration L1
```

After startup, ordinary inference resolves credentials, Usage Groups, caller policy, logical models and deployment/node routes without synchronous PostgreSQL reads.

Redis is deliberately not a mandatory remote configuration lookup on every inference request. It is used for cross-replica configuration propagation plus coordination that must be globally atomic: request-rate counters, physical-capacity leases and output-token budget state.

## Runtime configuration publication

Redis-enabled live mutations use:

```text
Admin / health mutation
  -> EF tracks Node / Model / Deployment / Credential / RatePolicy
  -> RuntimeStateOutboxSaveChangesInterceptor adds runtime_state_outbox row
     in the SAME PostgreSQL transaction
  -> PostgreSQL commit
  -> local post-save interceptor updates originating replica L1

RuntimeStateOutboxWorker
  -> acquire PostgreSQL advisory publisher lock
  -> process oldest pending rows strictly by Id
  -> acknowledged Redis state/hash write
  -> global runtime version increment + pub/sub
  -> publishing replica applies acknowledged event to its own L1
  -> mark ProcessedAtUtc only after Redis acknowledgement
```

A failed/backing-off oldest event blocks later events, preserving global mutation order. Delivery is at-least-once and Redis upsert/delete publication is idempotent.

When Redis is disabled the outbox interceptor/worker is not registered, so a single-instance deployment does not accumulate undeliverable Redis events.

## Publishing replica self-L1 rule

Any replica can win the PostgreSQL advisory lock. Because a publisher ignores its own Redis pub/sub origin event, the durable publisher must apply an acknowledged event to its own L1 explicitly. Otherwise a non-originating worker could publish the correct distributed state yet remain locally stale.

Full Stack outage coverage stops the mutation-originating gateway and proves the surviving peer can publish the pending event and enforce it from its own L1.

## Runtime diagnostics and retention

`GET /api/admin/runtime-sync` returns Redis sync state plus:

```text
outbox.pendingCount
outbox.failedPendingCount
outbox.oldestPendingAtUtc
outbox.oldestPendingAgeSeconds
outbox.maxPendingAttemptCount
outbox.lastProcessedAtUtc
outbox.lastError
```

Processed outbox history defaults to 30-day retention. Only `ProcessedAtUtc != null` rows can be deleted. Pending rows are never age-deleted.

## Shared caller request-rate governance

Policy definitions remain local L1. When Redis is enabled, request-rate fixed-window counters are global across gateway replicas. Redis-disabled deployments use the in-memory counter provider.

The request-rate Redis store currently has a degraded local fallback if Redis fails. Do not silently copy that behavior to harder governance boundaries without an explicit product decision.

## Shared output-token budget governance

Output-token budget definitions live on the same credential/model `RateLimitPolicy` runtime snapshot and therefore propagate through the transactional outbox and peer L1 synchronization.

The budget counter itself is coordination state, not configuration state:

```text
local L1 policy definition
  -> reserve request output cap
  -> IOutputTokenBudgetStore
       local atomic fixed-window store when Redis disabled
       Redis atomic fixed-window store when Redis enabled
  -> inference
  -> settle/refund against observed output usage
```

Redis keys use policy id + configured token budget + window size. Redis server time defines the shared fixed window. A hash tracks the window start and charged/reserved `used` amount with TTL.

Distributed token-budget admission is intentionally fail closed:

```text
Redis unavailable during token reservation
  -> do not use an independent local counter
  -> 503 token_budget_coordination_unavailable
```

Settlement is conservative. Successful 2xx responses with observed output usage refund unused reservation. No upstream attempt refunds fully. Once upstream work may have generated output, missing/uncertain usage keeps the full reservation charged. If Redis settlement fails, the successful Redis reservation remains charged rather than expanding the budget unsafely.

Full Stack `34987407169` proves a policy is propagated live to a peer that existed before the policy, shared usage settles from `7` to `14` across two gateways, the next request is globally rejected, Redis outage fails closed and recovery preserves the shared window.

See `docs/usage-governance.md` for the complete request-cap and settlement contract.

## Shared physical capacity

Redis-enabled deployment/node admission uses shared TTL-backed capacity leases. Acquisition is atomic across deployment and physical-node keys.

```text
Redis unavailable during capacity admission
  -> 503 capacity_coordination_unavailable

active Redis lease becomes unsafe
  -> cancel upstream/read/write before TTL expiry
  -> response not started: 503 + Retry-After:1 + capacity_lease_lost
  -> SSE already started: abort connection
```

Local load tracking remains useful for same-process routing telemetry; Redis owns the distributed physical-capacity boundary.

## Why local L1 remains mandatory

```text
local L1   = zero network hop for configuration, low latency
Redis L2   = cross-replica sync + globally atomic runtime coordination
PostgreSQL = durable recovery/configuration/outbox authority
```

Do not redesign route/credential/policy-definition lookup into Redis-on-every-request without an explicit architecture decision.

## Security-sensitive consistency

Credential revocation and caller-policy changes use the transactional runtime publication path. If a future requirement needs a stricter immediate-global revocation SLA than normal outbox latency, add a targeted mechanism rather than weakening the cache architecture.

Never store raw API keys, prompts, generated code/output or bearer tokens in Redis/outbox payloads.

## Operator configuration

Full-stack outbox knobs:

```text
REDIS_OUTBOX_BATCH_SIZE=50
REDIS_OUTBOX_POLL_MILLISECONDS=500
RETENTION_RUNTIME_STATE_OUTBOX_DAYS=30
```

The output-token Redis store uses the configured `Redis:ConnectionString` / key prefix and the policy's `WindowSeconds`; V1 has no separate token-window configuration.

## Validation

Current distributed quota/runtime evidence:

```text
commit     887ebfac98389c0115eaf9c102a60133ede745ff
CI         34987407172 SUCCESS
Full Stack 34987407169 SUCCESS
```

This validation proves Redis runtime sync, request-rate coordination, physical capacity safety, transactional-outbox failure/replay, outbox diagnostics/retention and shared fail-closed output-token reservation/settlement.
