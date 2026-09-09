# Routing control plane

LlmProxy exposes a runtime routing policy that is persisted in PostgreSQL and applied in memory without restarting the gateway.

## Strategies

- `WeightedLeastLoaded` (recommended production default): selects the eligible deployment with the best load/capacity/weight score. This is normally the best fit for long-running streaming LLM requests.
- `RoundRobin`: cycles evenly through eligible deployments. Useful for homogeneous runtimes and deterministic local tests.
- `WeightedRoundRobin`: cycles proportionally to effective weight.

Effective weight is `node weight × deployment weight`. Every selector still excludes disabled, draining, unhealthy and saturated deployments.

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

LlmProxy appends the OpenAI/vLLM paths to that complete root, preserving any prefix.

## Connection probe

```http
POST /api/admin/nodes/{nodeId}/test-connection
```

The probe reports the resolved service root and URLs for `/health`, `/v1/models`, `/v1/chat/completions`, and `/v1/responses`, together with status and latency for the health and OpenAI model probes. In Entra-enabled environments this endpoint requires the write/admin role because it causes the gateway to initiate network requests.
