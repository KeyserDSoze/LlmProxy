# Capacity control and backpressure

LlmProxy enforces capacity at two distinct levels: the logical model deployment and the physical inference node. Benchmark evidence is stored separately from the live limit so measurement never changes production behavior implicitly.

## Capacity layers

A deployment may define its own `MaxConcurrency`. If it is null, the node limit is used as its effective deployment ceiling.

The physical `InferenceNode.MaxConcurrency` is an aggregate ceiling shared by **all** deployments on that DGX:

```text
DGX-01 physical max = 8

agic-code      active = 5
agic-reasoning active = 3
                       ---
DGX-01 total active = 8 -> saturated
```

A fourth request to either deployment cannot bypass the physical ceiling merely because that deployment still has local headroom.

## Atomic lease acquisition

The in-memory request-load tracker owns both deployment and node active counters. A request acquires both under the same synchronization boundary:

```text
check deployment limit
+ check node aggregate limit
+ increment deployment counter
+ increment node counter
= one atomic capacity lease
```

The lease decrements both counters exactly once when the request finishes, fails or is cancelled. This avoids check-then-increment races where simultaneous requests could both observe the last slot as free.

PostgreSQL is not involved in acquiring or releasing inference capacity.

## Backpressure contract

When there are otherwise operational deployments but every eligible route is at its deployment or node-wide capacity, LlmProxy returns:

```http
HTTP/1.1 429 Too Many Requests
Retry-After: 1
Content-Type: application/json
```

with an OpenAI-shaped error:

```json
{
  "error": {
    "message": "Inference capacity is temporarily exhausted for model 'agic-code-fast'. Retry shortly.",
    "type": "rate_limit_error",
    "code": "capacity_exhausted"
  }
}
```

`503 no_healthy_deployment` remains reserved for the different case where no operational backend is available. A capacity race discovered after route selection is also converted to 429 and the request is **not** sent to an overcommitted DGX.

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

The profile is advisory evidence. It does not change the live `MaxConcurrency` when saved.

This separation is intentional:

```text
benchmark recommendation = 8
active production limit  = 4
physical DGX limit        = 6
```

Saving `8` is allowed because it may represent evidence from another runtime configuration or a planned node limit. Applying it is not allowed while the physical node limit is `6`.

## Explicit apply

Applying the recommendation is a separate administrator command:

```http
POST /api/admin/deployments/{deploymentId}/capacity-profile/apply
```

The operation:

1. requires an existing recommendation;
2. refuses to apply a value greater than the current physical node limit;
3. changes the deployment `MaxConcurrency` explicitly;
4. creates an audit event.

An operator must increase the physical node limit separately before applying a higher deployment recommendation.

## Administration API

Current endpoints:

```http
GET    /api/admin/capacity
PUT    /api/admin/deployments/{id}/capacity-profile
DELETE /api/admin/deployments/{id}/capacity-profile
POST   /api/admin/deployments/{id}/capacity-profile/apply
```

`GET /api/admin/capacity` exposes current in-memory active counts together with persisted limits/profile metadata. It is an administration endpoint; it is not used by the inference hot path.

Capacity mutations are audited as:

```text
deployment.capacity_profile.update
deployment.capacity_profile.clear
deployment.capacity_profile.apply
```

## Admin UI

The **DGX Hardware** view includes a physical-capacity section because node capacity is a hardware/runtime concern. It shows:

- node active requests;
- physical node ceiling;
- remaining slots;
- deployment effective active limit;
- benchmark recommendation;
- P95 TTFT and sustainable output throughput evidence;
- benchmark source/timestamp;
- explicit Save, Apply and Clear actions.

The UI deliberately labels recommendation and active limits separately.

## Test strategy

Unit coverage verifies:

- aggregate node capacity across multiple deployments;
- deployment-specific ceilings;
- atomic capacity acquisition and idempotent release;
- distinction between saturation and unavailable nodes;
- Capacity Profile validation and explicit apply semantics.

Frontend coverage verifies that saving a recommendation does not implicitly apply it.

The Docker capacity smoke suite starts a real gateway/PostgreSQL stack and a path-prefixed mock inference runtime with node concurrency `1`. It then:

1. persists benchmark evidence;
2. verifies explicit apply and over-limit rejection;
3. restarts LlmProxy and verifies profile persistence;
4. holds the only physical slot with a real SSE stream;
5. sends a concurrent request and expects 429 + `Retry-After: 1` + `capacity_exhausted`;
6. waits for the SSE lease to release and verifies the next request succeeds;
7. verifies audit events.

## What this is not

Capacity control is not a per-user quota or commercial rate limiter. Future credential/model quotas are a separate product-hardening concern. This layer protects physical inference capacity and provides deterministic backpressure under saturation.

Production limits must still be derived from real DGX/vLLM benchmark evidence. The values configured in CI/local environments are test values, not capacity promises.
