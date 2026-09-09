# DGX hardware telemetry

LlmProxy treats GPU hardware telemetry as an **optional operational signal** that is independent from vLLM health and inference routing. A broken or unreachable hardware metrics endpoint must never make a DGX node unavailable for inference by itself.

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
DGX_HARDWARE_METRICS_BASE_ADDRESS=http://10.0.0.10:9400
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

## Failure behavior

Hardware telemetry is best effort:

- HTTP failures mark the hardware snapshot unavailable but preserve the last successful values for diagnostics;
- unrelated Prometheus payloads are rejected as `No recognized NVIDIA DCGM Prometheus metrics were exposed.`;
- collection errors do not update `InferenceNode.Status`;
- inference traffic continues when DCGM is absent or unavailable;
- the collector only polls enabled nodes that have a hardware metrics root configured.

## Security/networking

The recommended production network shape is:

```text
LlmProxy VM
    -> TCP 8000 (example) vLLM on DGX
    -> TCP 9400 (example) DCGM exporter on DGX
```

The DCGM exporter endpoint should not be publicly exposed. Firewall it so only the gateway/monitoring network can reach it. Cloudflare Tunnel should expose the gateway, not raw DGX telemetry endpoints.

## Routing policy

Hardware signals are **not part of the routing score yet**. This is deliberate. High GPU utilization is often a sign that batching is working efficiently, while queue depth/KV-cache pressure are usually more directly related to inference latency. We will only introduce hardware-aware routing after representative DGX Spark benchmarks demonstrate useful thresholds.

The future benchmark should correlate at least:

- concurrency;
- TTFT p50/p95;
- output tokens/sec;
- vLLM running/waiting requests;
- KV-cache pressure;
- GPU utilization;
- framebuffer usage;
- temperature/power where operationally useful.
