# Architecture

## Bounded context

LlmProxy intentionally starts with one bounded context: **AI Inference Gateway**.

The gateway owns logical model publication, inference node registration, model deployments, request routing, runtime health, inference credentials and operational telemetry. The system is deployed as one application process plus PostgreSQL. This keeps transactional boundaries and operations simple while preserving clean internal layers.

## Layering

```text
LlmProxy.Domain
      ^
      |
LlmProxy.Application
      ^
      |
LlmProxy.Infrastructure
      ^
      |
LlmProxy.Api
```

- **Domain**: entities, state and invariants. No dependency on ASP.NET, EF Core, PostgreSQL, Docker or vLLM.
- **Application**: gateway use cases, routing policy and ports required from infrastructure.
- **Infrastructure**: PostgreSQL/EF Core, runtime health checks and external adapters.
- **API**: HTTP contract, OpenAI compatibility, Entra/OIDC composition and administration endpoints.

The React application is a client of the administration API and is compiled into the final application image.

## Runtime topology

```text
Internet
  |
  v
Cloudflare Tunnel
  |
  v
LlmProxy VM
  |-- llmproxy container
  |-- postgres container
  `-- cloudflared container
        |
        v
Private network
  |-- GPU inference hardware 01 -> vLLM
  |-- GPU inference hardware 02 -> vLLM
  `-- GPU inference hardware N  -> vLLM
```

No inference node runtime should be directly exposed to the Internet. The gateway is the policy enforcement and observability point.

## Logical model abstraction

Clients use logical names rather than physical model identifiers.

```text
agic-code-fast
   |
   +-- deployment A -> inference node01 -> provider model X
   +-- deployment B -> inference node02 -> provider model X
   `-- deployment C -> inference node03 -> provider model X
```

Changing the provider model behind `agic-code-fast` does not require changing GitHub Copilot configuration.

## Routing

The bootstrap implementation uses a weighted least-loaded strategy based on active requests and configured concurrency. Candidate deployments must be enabled and their nodes must not be disabled, draining or known-unhealthy.

Later routing signals can include queue depth, GPU utilization, memory utilization, TTFT, tokens/sec and rolling failure rate.

## Health

Node health is both active and passive by design.

- Active health periodically calls the vLLM `/health` endpoint.
- Passive health will later increase a failure score when real inference calls fail or time out.
- `Draining` prevents new work while allowing in-flight requests to complete.

## Persistence

PostgreSQL is the configuration source of truth. Runtime counters such as active request count remain in memory in the first single-instance release. If the gateway itself becomes horizontally scaled, these counters and coordination semantics will need a distributed design.

## Streaming

The gateway does not buffer generated output. Upstream vLLM responses are requested with `ResponseHeadersRead` and copied to the client as a stream, preserving SSE behavior and minimizing time-to-first-token overhead.

## Product evolution

The architecture deliberately leaves the inference runtime behind a port. vLLM is the initial target, but a future deployment may point to another OpenAI-compatible runtime, a larger inference node platform or a cloud provider without changing the public client contract.
