# Runtime cache architecture

LlmProxy keeps durable configuration/history in PostgreSQL and publishes latency-sensitive runtime decisions into cache state used by the inference path.

## Current design

After startup, the hot path is intended to resolve the following without synchronous PostgreSQL reads:

```text
Bearer/API credential
  -> credential snapshot / UsageGroup
  -> request-rate policy
  -> logical model
  -> deployment/node route catalog
  -> routing/performance state
  -> capacity admission
  -> vLLM
```

PostgreSQL remains the durable source of truth. Runtime cache state is disposable and can be rebuilt from PostgreSQL at gateway startup.

Current runtime-state implementations are intentionally accessed through abstractions such as:

```text
IApiCredentialCache
IRouteCatalog / IDeploymentCatalog
RequestRateLimiter
RoutingStrategyState
RoutingTuningState
IDeploymentPerformanceTracker
INodeRuntimeMetricsTracker
IRequestLoadTracker
```

The inference code should depend on these contracts rather than directly depending on a cache technology.

## Route catalog consistency

`IRouteCatalog` stores immutable/copy-on-write snapshots of:

```text
InferenceNode
ModelDefinition
ModelDeployment
```

The catalog contains only fields needed to resolve public logical models into provider model + DGX route candidates.

At startup:

```text
PostgreSQL
  -> nodes/models/deployments
  -> IRouteCatalog.Replace(...)
```

For live changes:

```text
Admin / health mutation
  -> EF tracked entity
  -> PostgreSQL SaveChanges succeeds
  -> RouteCatalogSaveChangesInterceptor
  -> in-memory catalog publish
```

The runtime state is never intentionally published before durable persistence succeeds.

Node health changes are part of the route snapshot, so health/drain/disable decisions remain visible to subsequent inference without request-time SQL.

## Why not read Redis on every inference request?

Redis can be added, but using it as a mandatory remote lookup for every token-generating request would add a network hop and make inference admission dependent on Redis availability.

For a production multi-instance gateway the preferred topology is therefore **L1 local runtime cache + Redis synchronization + PostgreSQL durable truth**:

```text
                    PostgreSQL
                 durable source
                       |
                 commit / outbox
                       |
                       v
                     Redis
            shared cache + events
                 /           \
                v             v
        Gateway A          Gateway B
        local L1           local L1
        snapshot           snapshot
            |                  |
            +------ inference -+
```

Inference remains local-memory fast. Redis coordinates multiple gateway replicas.

## Recommended Redis evolution

### Phase 1 — current single-instance design

```text
PostgreSQL -> local runtime cache
```

This is sufficient for the current single gateway deployment and keeps the operational footprint small.

### Phase 2 — Redis as distributed L2 + invalidation bus

Introduce a provider-neutral synchronization abstraction, for example:

```text
IRuntimeStateSynchronizer
IRouteCatalogChangePublisher
```

On a successful durable configuration change:

1. persist PostgreSQL transaction;
2. publish a versioned snapshot/change to Redis;
3. publish an invalidation/version event;
4. every gateway instance refreshes its local L1 snapshot.

Suggested Redis key space:

```text
llmproxy:route-catalog:version
llmproxy:route-catalog:snapshot
llmproxy:credentials:version
llmproxy:credentials:<hash-or-id>
llmproxy:rate-limits:version
```

Do not put raw API keys, prompts, generated code or bearer tokens in Redis.

### Phase 3 — transactional outbox for stronger delivery guarantees

Direct `PostgreSQL SaveChanges -> Redis publish` has a small failure window: the DB commit may succeed while Redis publication fails.

For multi-instance/HA operation, use a PostgreSQL transactional outbox:

```text
PostgreSQL transaction
  - update Node/Model/Deployment/Credential/Policy
  - insert RuntimeStateOutbox event
commit

Outbox worker
  -> Redis snapshot/version/event
  -> mark delivered
```

This keeps PostgreSQL authoritative and provides retryable Redis synchronization without distributed transactions.

### Phase 4 — optional Redis-first cold start

A gateway replica may load a complete versioned snapshot from Redis for faster scale-out, while still falling back to PostgreSQL if Redis is unavailable or the snapshot version is invalid.

PostgreSQL remains the recovery authority.

## If we really want no local in-memory cache

A `RedisRouteCatalog` could implement `IDeploymentCatalog` directly and read Redis for every route lookup. The abstraction introduced now makes that technically straightforward.

It is not the recommended default because it changes the failure/performance profile:

```text
local L1 lookup: nanoseconds / microseconds, no network dependency
Redis lookup: network round trip, Redis dependency per inference request
```

A better production architecture is:

```text
PostgreSQL = durable truth
Redis      = distributed synchronization / L2
local RAM  = request-path L1
```

## Consistency model

Configuration is **durably consistent first, runtime eventually consistent immediately after commit**.

For a single gateway process, the current SaveChanges interceptor normally makes publication visible before the admin request returns.

For multiple replicas with Redis, target propagation should be sub-second but must be observable through catalog version/instance diagnostics.

Security-sensitive operations such as credential revocation may eventually require stronger semantics than ordinary route changes. If strict immediate global revocation is required across many replicas, use Redis revocation/version checks or a short-lived L1 policy specifically for credentials rather than weakening all routing performance.

## Current validation

The route-catalog increment is considered validated only when CI proves all of the following:

- unit tests for snapshot routing semantics pass;
- existing routing/capacity/governance tests remain green;
- live EF mutations are published after successful saves;
- startup rebuild works;
- `/v1/models` and inference continue to work after PostgreSQL is deliberately stopped **after startup**.

The last test is explicit evidence that normal inference auth + route resolution no longer requires request-time PostgreSQL reads.
