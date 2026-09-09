# DGX Spark and vLLM runtime contract

## Scope

LlmProxy does not install or own the LLM runtime on each DGX. It expects each registered DGX node to expose a private OpenAI-compatible vLLM endpoint reachable from the gateway VM.

## Network contract

Example:

```text
DGX01  http://10.0.0.21:8000
DGX02  http://10.0.0.22:8000
DGX03  http://10.0.0.23:8000
```

The gateway appends:

```text
/health
/v1/chat/completions
```

The DGX endpoints should be reachable only on the trusted network. Do not expose them through Cloudflare or directly to clients.

## vLLM startup expectations

The physical model identifier exposed by vLLM must match `ProviderModelName` configured for the logical model in LlmProxy.

Example conceptual mapping:

```text
Logical model:       agic-code-fast
Provider model name: Qwen/<physical-model>
Node:                dgx-01
Base address:        http://10.0.0.21:8000
```

## Health

Every 10 seconds LlmProxy checks:

```http
GET <node-base-address>/health
```

A successful HTTP status marks the node Healthy. Network errors/non-success statuses mark it Unhealthy. Draining nodes are intentionally skipped by the health worker and receive no new traffic.

## Capacity

`MaxConcurrency` is a gateway guardrail, not a claim about physical DGX capacity. Determine its production value through benchmark runs using the real model, quantization, context sizes and expected Copilot workloads.

Start conservatively, measure TTFT/tokens-per-second/OOM behavior, then raise concurrency. Different models may need different deployment-level concurrency overrides.

## Failover semantics

LlmProxy may retry a request against another eligible deployment if the selected upstream fails before response streaming starts. Once response bytes have begun streaming to the client, transparent failover is unsafe and is not attempted.

## Initial commissioning checklist

1. Configure static/reserved addressing or resolvable DNS for each DGX.
2. Start vLLM and verify `/health` from the gateway VM.
3. Verify a direct `POST /v1/chat/completions` from the gateway VM.
4. Register the node in LlmProxy.
5. Publish a logical model and create a deployment.
6. Wait for Healthy state in the admin console.
7. Call LlmProxy `/v1/models` and `/v1/chat/completions`.
8. Only then connect GitHub Copilot through the public Cloudflare hostname.
