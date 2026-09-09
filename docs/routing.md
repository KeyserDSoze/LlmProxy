# Routing control plane

LlmProxy exposes runtime routing configuration that is persisted in PostgreSQL and published into in-memory state so new inference requests pick up changes without restarting the gateway.

## Strategies

- `WeightedLeastLoaded` (recommended production default): selects the eligible deployment using configured capacity/weight, current gateway load, node health, recent inference performance and, when available, live vLLM runtime pressure.
- `RoundRobin`: cycles evenly through eligible deployments. Useful for homogeneous runtimes and deterministic tests.
- `WeightedRoundRobin`: cycles proportionally to effective weight.

Effective weight is `node weight × deployment weight`. Every selector excludes disabled, draining, unhealthy and saturated deployments.

## Performance-aware least-loaded routing

`WeightedLeastLoaded` keeps its decision signals in memory; PostgreSQL is not queried on the inference hot path. Its score combines configured capacity/load with operational penalties.

Per-deployment request feedback uses exponentially weighted moving averages (EWMA):

- TTFT / time to first byte;
- completed request duration;
- infrastructure failure score.

The first configurable number of samples are treated as warm-up and do not affect performance penalties.

When vLLM exposes Prometheus metrics at `<service-root>/metrics`, LlmProxy also consumes node-level signals:

- requests currently running in vLLM;
- requests waiting in the vLLM queue;
- KV-cache utilization;
- cumulative prompt and generated token counters;
- the runtime-reported model label.

The gateway compares its own active request count with vLLM's running count. Excess running work can indicate traffic from another client and contributes to an external-load penalty. Queue depth and high KV-cache pressure can add further penalties.

Runtime telemetry is optional. If `/metrics` is unavailable, routing continues with configured capacity, gateway load, health and recent request performance. Metrics collection failure does **not** make a node unhealthy by itself.

## Smart-routing tuning

The `WeightedLeastLoaded` coefficients are not hard-coded operational policy. They are stored in PostgreSQL and mirrored into a thread-safe in-memory `RoutingTuningState`.

```http
GET /api/admin/routing/tuning
PUT /api/admin/routing/tuning
Content-Type: application/json
```

The tuning profile contains:

| Setting | Meaning | Default |
| --- | --- | ---: |
| `warmupSamples` | minimum deployment samples before performance penalties apply | 3 |
| `ttftTargetMilliseconds` | TTFT normalization target | 2000 |
| `ttftPenaltyWeight` | maximum normalized TTFT penalty weight | 0.25 |
| `failurePenaltyWeight` | infrastructure-failure penalty weight | 1.50 |
| `externalLoadPenaltyWeight` | penalty for vLLM running work not accounted for by this gateway | 0.40 |
| `queuePenaltyWeight` | vLLM waiting-queue penalty weight | 0.75 |
| `kvCacheThreshold` | KV-cache ratio above which pressure begins to count | 0.70 |
| `kvCachePenaltyWeight` | maximum KV-cache pressure penalty | 0.60 |
| `degradedNodePenalty` | penalty for a degraded node | 0.35 |
| `unknownNodePenalty` | penalty while node health is still unknown | 0.10 |

These are conservative bootstrap values, not claims about DGX Spark capacity. They must be calibrated from representative Copilot traffic and real model/concurrency benchmarks.

Updates are range-validated, persisted first, audited as `routing.tuning.update`, then published to the in-memory selector. A container restart reloads the persisted profile.

The React Routing page exposes the complete profile plus a reset-to-default action.

## Diagnostics

```http
GET /api/admin/routing/performance
GET /api/admin/routing/runtime
```

`/routing/performance` exposes per-deployment EWMA feedback. `/routing/runtime` exposes the latest vLLM runtime snapshot per node.

Runtime polling configuration:

```text
RuntimeMetrics__Enabled=true
RuntimeMetrics__IntervalSeconds=5
```

Docker aliases:

```text
RUNTIME_METRICS_ENABLED=true
RUNTIME_METRICS_INTERVAL_SECONDS=5
```

## Strategy administration

```http
GET /api/admin/routing
PUT /api/admin/routing
Content-Type: application/json

{"strategy":"WeightedRoundRobin"}
```

The strategy survives restarts. `Routing__Strategy` / `ROUTING_STRATEGY` is only a first-start default when the database contains no routing policy yet.

## Node service roots

A node address is a complete HTTP(S) service root. Examples:

```text
http://localhost:3450/primopath
http://127.0.0.1:8000
http://10.0.0.25:8000/vllm
https://dgx-01.internal:8443/inference
```

LlmProxy appends OpenAI/vLLM paths while preserving any prefix:

```text
http://localhost:3450/primopath/health
http://localhost:3450/primopath/metrics
http://localhost:3450/primopath/v1/chat/completions
```

## Connection probe

```http
POST /api/admin/nodes/{nodeId}/test-connection
```

The probe reports the resolved root and URLs for `/health`, `/v1/models`, `/v1/chat/completions`, and `/v1/responses`, plus status and latency for health/OpenAI probes. With Entra enabled it requires write/admin authorization because it causes outbound network requests.

## Hardware telemetry boundary

NVIDIA/DCGM hardware telemetry is intentionally separate from vLLM runtime telemetry. GPU utilization, memory, temperature and power are initially operational diagnostics only. Do not add them to the routing score until real DGX Spark benchmarks establish thresholds that improve throughput/latency rather than merely reacting to noisy utilization samples.
