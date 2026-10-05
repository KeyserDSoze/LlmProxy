# inference hardware telemetry

LlmProxy treats GPU hardware telemetry as an **optional operational signal** that is independent from vLLM health and inference routing. A broken or unreachable hardware metrics endpoint must never make a inference node unavailable for inference by itself.

## Why it is separate from vLLM telemetry

vLLM exposes request/runtime pressure at the inference service root (`/metrics`), including running requests, waiting requests and KV-cache utilization. NVIDIA DCGM exporter is a different service and commonly runs on a different port or service root. A node therefore stores two independent roots:

```text
BaseAddress                -> vLLM / OpenAI service root
HardwareMetricsBaseAddress -> NVIDIA/DCGM exporter service root (optional)
```

Examples:

```text
BaseAddress                = http://10.0.0.21:8000/vllm
HardwareMetricsBaseAddress = http://10.0.0.21:9400
```

LlmProxy appends `/metrics` to the hardware service root while preserving any path prefix.

## Supported DCGM signals

The first collector recognizes the standard Prometheus names used by NVIDIA DCGM exporter:

```text
DCGM_FI_DEV_GPU_UTIL
DCGM_FI_DEV_FB_USED
DCGM_FI_DEV_FB_FREE
DCGM_FI_DEV_FB_TOTAL
DCGM_FI_DEV_GPU_TEMP
DCGM_FI_DEV_POWER_USAGE
```

The in-memory snapshot exposes:

- detected GPU count;
- average and maximum GPU utilization percentage;
- total framebuffer memory used/free in MiB;
- framebuffer usage ratio when enough memory information is available;
- maximum GPU temperature;
- summed GPU power usage;
- last successful collection time;
- last collection attempt and error.

For multi-GPU systems utilization is averaged/maximized across GPU series while framebuffer and power values are summed. Temperature uses the maximum observed GPU temperature because it is more useful for operator diagnostics than a mean.

## Configuration

Global collector configuration:

```text
HardwareMetrics__Enabled=true
HardwareMetrics__IntervalSeconds=10
```

Docker aliases:

```text
HARDWARE_METRICS_ENABLED=true
HARDWARE_METRICS_INTERVAL_SECONDS=10
```

The bootstrap node can receive an optional hardware root:

```text
INFERENCE_NODE_HARDWARE_METRICS_BASE_ADDRESS=http://10.0.0.10:9400
```

Existing nodes can be configured or cleared at runtime:

```http
PUT /api/admin/nodes/{nodeId}/hardware-metrics
Content-Type: application/json

{"baseAddress":"http://10.0.0.21:9400"}
```

Clear the configuration with:

```json
{"baseAddress":null}
```

Changes are persisted in PostgreSQL and audited as `node.hardware_metrics.update`.

Current in-memory snapshots are exposed through:

```http
GET /api/admin/hardware
```

## Failure and clear behavior

Hardware telemetry is best effort:

- HTTP failures mark the hardware snapshot unavailable but preserve the last successful numeric values for diagnostics;
- unrelated Prometheus payloads are rejected as `No recognized NVIDIA DCGM Prometheus metrics were exposed.`;
- collection errors do not update `InferenceNode.Status`;
- inference traffic continues when DCGM is absent or unavailable;
- the collector only polls enabled nodes that have a hardware metrics root configured;
- explicitly clearing `HardwareMetricsBaseAddress` removes the in-memory snapshot, so stale hardware data is not presented as a current configured source.

The distinction is intentional: a temporary exporter outage retains the last sample for troubleshooting, while an administrator deliberately removing the endpoint also removes its runtime state.

## Admin UI

The React console exposes telemetry under **Infrastructure → Capacity & telemetry**. It shows current collector availability, GPU count, utilization, framebuffer usage, temperature and power, and allows the independent DCGM service root to be configured or cleared per physical node. The same workspace also exposes the node-wide simultaneous-request ceiling, making the distinction between observed hardware data and enforced physical capacity explicit.

The page deliberately labels this data as observational and includes a **Routing isolation** note. Aggregate cards use only currently available snapshots; retained values from a transient failed collector are shown only as diagnostics for that node.

## Security/networking

The recommended production network shape is:

```text
LlmProxy VM
    -> TCP 8000 (example) vLLM on inference node
    -> TCP 9400 (example) DCGM exporter on inference node
```

The DCGM exporter endpoint should not be publicly exposed. Firewall it so only the gateway/monitoring network can reach it. Cloudflare Tunnel should expose the gateway, not raw inference node telemetry endpoints.

## Routing policy

Hardware signals are **not part of the routing score yet**. This is deliberate. High GPU utilization is often a sign that batching is working efficiently, while queue depth/KV-cache pressure are usually more directly related to inference latency. We will only introduce hardware-aware routing after representative GPU inference hardware benchmarks demonstrate useful thresholds.

The benchmark should correlate at least:

- concurrency;
- TTFT p50/p95/p99;
- output tokens/sec;
- vLLM running/waiting requests;
- KV-cache pressure;
- GPU utilization;
- framebuffer usage;
- temperature/power where operationally useful.

See `docs/benchmarking.md` for the benchmark protocol and capacity-profile format.

## Automated validation

The hardware increment is covered at multiple levels:

- backend unit tests for service-root normalization, DCGM parsing and snapshot state;
- Vitest/Testing Library for the React hardware view and endpoint updates;
- Playwright for the administrator hardware workflow;
- `tests/backend/integration/hardware_smoke.sh`, which starts a fake path-prefixed DCGM exporter separately from the fake vLLM runtime, validates multi-GPU aggregation, simulates a DCGM `503`, proves inference health remains `Healthy`, verifies audit persistence and verifies explicit endpoint clearing removes the runtime snapshot.

The complete quality gate passed on commit `6c238a095273843e713a72fb2e26b2c7c434fc62` on 2026-09-10.
