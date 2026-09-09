# Routing control plane

LlmProxy exposes a runtime routing policy that is persisted in PostgreSQL and applied in memory without restarting the gateway.

## Strategies

- `WeightedLeastLoaded` (recommended production default): selects the eligible deployment using configured capacity/weight, current gateway load, node health, recent inference performance and, when available, live vLLM runtime pressure. This is normally the best fit for long-running streaming LLM requests.
- `RoundRobin`: cycles evenly through eligible deployments. Useful for homogeneous runtimes and deterministic local tests.
- `WeightedRoundRobin`: cycles proportionally to effective weight.

Effective weight is `node weight × deployment weight`. Every selector still excludes disabled, draining, unhealthy and saturated deployments.

## Performance-aware least-loaded routing

`WeightedLeastLoaded` keeps its decision signals in memory so PostgreSQL is never queried in the inference hot path. The score starts from configured load/capacity/weight and adds penalties for operational pressure.

Recent request feedback is tracked per deployment with exponentially weighted moving averages (EWMA):

- time to first byte / TTFT;
- completed request duration;
- infrastructure failure score.

The first few samples are treated as warm-up and do not affect the performance penalty, preventing a single cold model load from biasing routing indefinitely.

When a vLLM runtime exposes Prometheus metrics at `<service-root>/metrics`, LlmProxy also consumes live node-level signals:

- requests currently running in vLLM;
- requests waiting in the vLLM queue;
- KV-cache utilization;
- cumulative prompt and generated token counters for diagnostics.

The gateway distinguishes its own active-request counter from vLLM running work. Running requests above the gateway count can therefore reveal load coming from another client and contribute to a routing penalty. Waiting requests and high KV-cache pressure further increase the score.

Runtime telemetry is optional. A node whose `/metrics` endpoint is unavailable continues to be routed using health, configured capacity, gateway load and request-performance feedback. Metrics collection failure does **not** make a node unhealthy by itself.

Admin diagnostics:

```http
GET /api/admin/routing/performance
GET /api/admin/routing/runtime
```

`/routing/performance` exposes the in-memory per-deployment EWMA feedback. `/routing/runtime` exposes the latest vLLM runtime snapshot per node.

Runtime polling is configured with:

```text
RuntimeMetrics__Enabled=true
RuntimeMetrics__IntervalSeconds=5
```

or the Docker environment aliases:

```text
RUNTIME_METRICS_ENABLED=true
RUNTIME_METRICS_INTERVAL_SECONDS=5
```

## Live administration

```http
GET /api/admin/routing
PUT /api/admin/routing
Content-Type: application/json

{"strategy":"WeightedRoundRobin"}
```

The update is written to PostgreSQL first and then published to the in-memory selector state. New inference requests immediately use the new strategy. The policy survives gateway/container restarts. `Routing__Strategy` / `ROUTING_STRATEGY` is a first-start default only when the database has no routing policy yet.

## Node service roots

A node address is a complete HTTP(S) service root. Valid examples include:

```text
http://localhost:3450/primopath
http://localhost:3451/altropath
http://127.0.0.1:8000
http://10.0.0.25:8000/vllm
https://dgx-01.internal:8443/inference
```

LlmProxy appends the OpenAI/vLLM paths to that complete root, preserving any prefix. The same rule is used for health and runtime telemetry, for example:

```text
http://localhost:3450/primopath/health
http://localhost:3450/primopath/metrics
http://localhost:3450/primopath/v1/chat/completions
```

## Connection probe

```http
POST /api/admin/nodes/{nodeId}/test-connection
```

The probe reports the resolved service root and URLs for `/health`, `/v1/models`, `/v1/chat/completions`, and `/v1/responses`, together with status and latency for the health and OpenAI model probes. In Entra-enabled environments this endpoint requires the write/admin role because it causes the gateway to initiate network requests.
