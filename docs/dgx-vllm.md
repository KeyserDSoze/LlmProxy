# DGX Spark and inference-runtime contract

## Scope

LlmProxy does not install or own the LLM runtime on each DGX in the current product milestone. It expects each registered node to expose an HTTP(S), OpenAI-compatible inference service reachable from the gateway VM.

A node endpoint is deliberately modeled as a **complete service root**, not merely a host name. This is important both for real DGX deployments and local development/mocking.

## Supported node addresses

All of these are valid:

```text
http://localhost:3450/primopath
http://localhost:3451/altropath
http://127.0.0.1:8000
http://10.0.0.21:8000
http://10.0.0.25:8000/vllm
https://dgx-01.internal:8443/inference
```

The service root may contain:

- DNS host name or `localhost`;
- IPv4 or IPv6 address;
- explicit port;
- optional path prefix;
- HTTP or HTTPS.

Query strings, fragments and credentials embedded in the URL are intentionally rejected because they make endpoint composition/security ambiguous.

## Endpoint composition

LlmProxy preserves the complete configured service root and appends the runtime API path.

Examples:

```text
Base:     http://localhost:3450/primopath
Health:   http://localhost:3450/primopath/health
Chat:     http://localhost:3450/primopath/v1/chat/completions
Responses:http://localhost:3450/primopath/v1/responses

Base:     http://10.0.0.21:8000
Health:   http://10.0.0.21:8000/health
Chat:     http://10.0.0.21:8000/v1/chat/completions
Responses:http://10.0.0.21:8000/v1/responses
```

This lets local test servers live behind arbitrary prefixes such as `/primopath` and `/altropath` while production DGX nodes can use a bare IP/port or a reverse-proxy prefix.

## Network contract

Typical production configuration:

```text
DGX01  http://10.0.0.21:8000
DGX02  http://10.0.0.22:8000
DGX03  http://10.0.0.23:8000/vllm
```

DGX endpoints should be reachable only on the trusted network. Do not expose them through Cloudflare or directly to clients.

## vLLM startup expectations

The physical model identifier exposed by vLLM must match `ProviderModelName` configured for the logical model in LlmProxy.

Example conceptual mapping:

```text
Logical model:       agic-code-fast
Provider model name: Qwen/<physical-model>
Node:                dgx-01
Base address:        http://10.0.0.21:8000
```

## Routing strategies

The gateway currently supports three startup-selectable strategies through `Routing:Strategy` / `ROUTING_STRATEGY`:

```text
WeightedLeastLoaded  recommended default for long-running concurrent LLM requests
RoundRobin           equal sequential rotation across eligible deployments
WeightedRoundRobin   sequential rotation proportional to effective weight
```

For weighted routing the effective candidate weight is:

```text
node weight × deployment weight
```

Health, drain state and concurrency limits are always applied before a candidate is eligible, regardless of routing strategy.

## Health

Every 10 seconds LlmProxy checks:

```http
GET <complete-node-service-root>/health
```

A successful HTTP status marks the node Healthy. Network errors/non-success statuses mark it Unhealthy. Draining nodes are intentionally skipped by the health worker and receive no new traffic.

## Streaming

Both Chat Completions and Responses can return streaming responses. For `text/event-stream` LlmProxy disables ASP.NET response buffering where supported, copies upstream bytes incrementally and flushes each received chunk to the client.

The failover boundary is strict:

- connection failures or upstream 5xx responses may be retried on another eligible deployment **before** a stream is committed;
- after response bytes have started, LlmProxy never retries on another DGX because concatenating two independent model streams would create a corrupt OpenAI response;
- interrupted committed streams are terminated and recorded as `upstream_stream_interrupted`.

## Local integration test contract

The repository contains real local mock inference runtimes under `tests/backend/integration/`.

CI starts, among others:

```text
http://host.docker.internal:3450/primopath
http://host.docker.internal:3451/altropath
```

and verifies:

- path-prefixed endpoint composition;
- node health;
- OpenAI model-name rewriting;
- WeightedRoundRobin distribution across two runtimes;
- `/v1/responses` pass-through;
- actual SSE first-chunk delivery before the mock stream completes.

This gives us a repeatable local approximation of the future multi-DGX topology without requiring physical DGX hardware in CI.

## Capacity

`MaxConcurrency` is a gateway guardrail, not a claim about physical DGX capacity. Determine its production value through benchmark runs using the real model, quantization, context sizes and expected Copilot workloads.

Start conservatively, measure TTFT/tokens-per-second/OOM behavior, then raise concurrency. Different models may need different deployment-level concurrency overrides.

## Initial commissioning checklist

1. Configure static/reserved addressing or resolvable DNS for each DGX.
2. Start vLLM and verify `<service-root>/health` from the gateway VM.
3. Verify a direct `POST <service-root>/v1/chat/completions` from the gateway VM.
4. Register the complete service root in LlmProxy.
5. Publish a logical model and create a deployment.
6. Wait for Healthy state in the admin console.
7. Call LlmProxy `/v1/models`, `/v1/chat/completions`, and `/v1/responses`.
8. Verify SSE streaming with a real streaming model request.
9. Only then connect GitHub Copilot through the public Cloudflare hostname.
