# Capacity control and backpressure

LlmProxy enforces physical inference capacity at both deployment and node level. Benchmark evidence is stored separately from live limits, and Redis-enabled deployments coordinate capacity across gateway replicas with renewable leases. Capacity values count **simultaneous inference requests, not people**: one Copilot user may have several requests in flight at once.

## Capacity layers

A deployment may define its own `MaxConcurrency`. If it is null, the node limit is used as its effective deployment ceiling.

The physical `InferenceNode.MaxConcurrency` is an aggregate ceiling shared by **all** deployments on that inference node:

```text
inference node-01 physical max = 8

agic-code      active = 5
agic-reasoning active = 3
                       ---
inference node-01 total active = 8 -> saturated
```

A request to either deployment cannot bypass the physical ceiling merely because that deployment still has local headroom.

An inference node represents one physical machine. If the same server exposes vLLM on `:8080` and a System One runtime on `:8090`, those are deployment runtime roots on the same hardware and must share one physical ceiling. They must not be modeled as independent physical-capacity pools merely because the ports differ.

## Capacity provider abstraction

Inference uses `IRequestCapacityGate` rather than directly depending on local counters or Redis.

```text
Redis disabled -> LocalRequestCapacityGate
Redis enabled  -> RedisRequestCapacityGate
```

Both return an `IRequestCapacityLease`. Redis leases additionally expose a `CoordinationLost` cancellation token so active inference can be terminated if the distributed lease can no longer be trusted.

## Single-instance admission

The local request-load tracker acquires deployment and node active counters under one synchronization boundary:

```text
check deployment limit
+ check node aggregate limit
+ increment deployment counter
+ increment node counter
= one local capacity lease
```

The lease decrements both counters exactly once when the request finishes, fails or is cancelled.

## Distributed Redis admission

With Redis enabled, capacity is coordinated globally across gateway replicas. Admission atomically checks and claims both a deployment key and a physical-node key using Redis server time.

Conceptually:

```text
remove expired lease members
check deployment active < deployment limit
check node active < node limit
add lease id to deployment + node sorted sets with expiry
```

The lease is renewable. Redis key TTLs are longer than individual lease expiry so stale data can self-heal while preserving the sorted-set coordination state.

Local request-load tracking remains active for same-process routing/load telemetry, but the Redis lease is the authoritative multi-replica admission boundary.

## Backpressure contract

If healthy infrastructure exists but all eligible routes are at deployment/node capacity, LlmProxy returns:

```http
HTTP/1.1 429 Too Many Requests
Retry-After: 1
```

with:

```json
{
  "error": {
    "type": "rate_limit_error",
    "code": "capacity_exhausted"
  }
}
```

`503 no_healthy_deployment` remains reserved for the different case where no operational backend is available.

If Redis is configured as the distributed provider but admission cannot safely consult it, LlmProxy fails closed instead of falling back to an unsafe local guess:

```text
503 capacity_coordination_unavailable
```

## Active lease loss / fail-closed cancellation

A Redis capacity lease is renewed periodically. A monotonic `CapacityLeaseValidityTracker` records the last successful renewal. A safety watchdog must signal loss **before** the Redis lease can expire and become available to another replica.

Current behavior:

```text
renewal succeeds
  -> move safety deadline forward

renewal fails transiently
  -> keep watchdog armed
  -> retry renewal

lease disappears or safe renewal deadline is crossed
  -> signal IRequestCapacityLease.CoordinationLost
  -> cancel active inference
```

The watchdog polls at most every 1 second (0.5 seconds when the renewal interval is 1 second) so timer-boundary jitter cannot consume the entire safety margin and delay cancellation until the Redis TTL.

If coordination is lost before response headers/body start:

```http
HTTP/1.1 503 Service Unavailable
Retry-After: 1
```

with error code:

```text
capacity_lease_lost
```

If SSE/downstream bytes already started, LlmProxy aborts the connection. It must never continue generating tokens until/after the distributed lease can be reused elsewhere, and it cannot replace an already-started streaming response with a new JSON error body.

Request metrics record `capacity_lease_lost`; OpenTelemetry marks the request and emits a dedicated lease-loss span/event so the condition is visible in Tempo.

## Benchmark Capacity Profile

A `ModelDeployment` can persist benchmark evidence:

```text
RecommendedMaxConcurrency
BenchmarkP95TtftMilliseconds
BenchmarkP95DurationMilliseconds
SustainableOutputTokensPerSecond
BenchmarkSource
BenchmarkMeasuredAtUtc
```

The profile is advisory evidence. It does not change live `MaxConcurrency` when saved.

Example:

```text
benchmark recommendation = 8
active deployment limit  = 4
physical node limit        = 6
```

Saving `8` is allowed as evidence. Applying it is not allowed while the physical node limit is `6`.

## Explicit apply

Applying the recommendation remains a separate audited administrator command:

```http
POST /api/admin/deployments/{deploymentId}/capacity-profile/apply
```

The operation requires a recommendation, refuses values greater than the physical node limit, changes deployment `MaxConcurrency` explicitly and creates an audit event.

## Administration API

```http
GET    /api/admin/capacity
PUT    /api/admin/deployments/{id}/capacity-profile
DELETE /api/admin/deployments/{id}/capacity-profile
POST   /api/admin/deployments/{id}/capacity-profile/apply
```

`GET /api/admin/capacity` exposes the current distributed node active count when coordination is available, the local gateway count for diagnostics, the capacity provider/admission-block state and persisted limit/profile metadata. This is a live admission view, not historical usage reporting.

The Admin UI exposes this under **Infrastructure → Capacity & telemetry → Physical capacity**. `Edit capacity` changes `InferenceNode.MaxConcurrency` at runtime and is audited through the normal node update API. Deployment-specific concurrency can be edited separately in **Models & Deployments**; an empty deployment limit inherits the hardware ceiling.

Caller population and quotas remain separate controls:

- **Users & Access** controls which Entra users are admitted; a desired population such as 30 people is represented by admitting those users, not by setting node concurrency to 30.
- **Infrastructure** controls the physical simultaneous-request ceiling, for example 10 requests in flight across the machine.
- **Usage & Governance** controls request/token rate limits per user, group or credential.

There is intentionally no conversion such as `30 users = 30 concurrency`: the safe simultaneous-request value comes from benchmark evidence for the actual model/runtime/hardware combination.

## Test strategy

Unit/integration coverage verifies local aggregate node capacity, deployment ceilings, idempotent release, saturation vs unavailable semantics and Capacity Profile behavior.

The dedicated Full Stack Smoke additionally proves multi-replica behavior:

1. gateway A holds the single inference node slot with a real SSE stream;
2. gateway B sees the shared Redis lease and returns `429 capacity_exhausted`;
3. Redis contains capacity lease keys while inference is active;
4. after release, traffic recovers;
5. a long-running stream is started and Redis is stopped;
6. inference is aborted before configured lease expiry and never reaches `[DONE]`;
7. PostgreSQL records `capacity_lease_lost`;
8. Tempo shows `capacity_lease_lost` for the request trace.

Canonical validation:

```text
commit     6ec3c29176584f2e0bffd98b5d8cbbb0e833e76f
CI         34961566507 SUCCESS
Full Stack 34961566463 SUCCESS
```

## What this is not

Capacity control is not a per-user commercial quota. Request-rate governance is a separate layer, and token/budget quotas still require reservation/settlement semantics.

Production concurrency limits must be derived from real inference node/vLLM benchmark evidence. Values used in CI/local environments are test values, not capacity promises.
