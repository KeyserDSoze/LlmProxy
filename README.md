# LlmProxy

Enterprise OpenAI-compatible gateway for routing GitHub Copilot and other AI clients to on-premises LLMs running on NVIDIA DGX infrastructure.

> Status: V1 under active development.

## What this product is

LlmProxy is the control plane between AI clients and the physical inference fleet. GitHub Copilot or any OpenAI-compatible client sees one stable endpoint and logical model names; the gateway decides which DGX/model deployment serves every request.

The first production target is intentionally compact: one on-premises VM running Docker, PostgreSQL and Cloudflare Tunnel, connected over the private LAN to one or more DGX Spark nodes running an OpenAI-compatible runtime such as vLLM.

## High-level architecture

```text
GitHub Copilot / OpenAI-compatible clients
                  |
                  v
          Cloudflare Tunnel
                  |
                  v
+--------------------------------------------------+
| On-prem VM                                       |
|                                                  |
|  LlmProxy .NET 10                                |
|  ├─ /v1/*          OpenAI-compatible inference   |
|  ├─ /api/admin/*   Administration API            |
|  ├─ /admin/*       React administration UI       |
|  ├─ /healthz       Liveness                      |
|  └─ /readyz        Readiness / PostgreSQL        |
|                  |                               |
|             PostgreSQL                           |
|                                                  |
|             cloudflared                          |
+------------------+-------------------------------+
                   |
                   | private LAN
          +--------+---------+
          |                  |
          v                  v
      DGX Spark 01        DGX Spark N
         vLLM                vLLM
       /v1 API             /v1 API
```

## Design principles

- **OpenAI-compatible contract**: clients integrate once against `/v1`.
- **Logical models**: clients request aliases such as `agic-code-fast`; physical model names stay internal.
- **Complete service-root URLs**: a DGX can use localhost, DNS, IPv4/IPv6, arbitrary ports and optional path prefixes.
- **Single-domain DDD**: one bounded context, **AI Inference Gateway**, split into Domain, Application, Infrastructure and API layers.
- **Multi-DGX from day one**: one-node startup, N-node domain model.
- **Dynamic routing**: `WeightedLeastLoaded`, `RoundRobin` and `WeightedRoundRobin`, switchable live without restart.
- **Streaming first**: SSE is forwarded incrementally and is never retried after response bytes have started.
- **Health hysteresis**: transient probe failures degrade a node before removing it from service; recovery requires a success streak.
- **Enterprise security**: Entra ID protects administration; revocable bearer credentials protect inference.
- **Administrative accountability**: configuration changes are persisted in an audit trail.
- **No prompt logging by default**: operational telemetry excludes prompts and generated code.
- **Testable boundaries**: unit tests mock external boundaries; PostgreSQL, Docker, routing and SSE are exercised with real integration components.
- **Immutable delivery**: validated images are published to GHCR and deployed by GitHub Actions.

## Repository structure

```text
.
├── docs/                         # Architecture, security, deployment and operations documentation
│   ├── architecture.md
│   ├── api-contract.md
│   ├── deployment.md
│   ├── dgx-vllm.md
│   ├── github-copilot.md
│   ├── operations.md
│   ├── roadmap.md
│   ├── security.md
│   └── testing.md
│
├── src/                          # Product code only
│   ├── LlmProxy.Domain/          # Entities, invariants and domain rules
│   ├── LlmProxy.Application/     # Use cases, ports and routing orchestration
│   ├── LlmProxy.Infrastructure/  # PostgreSQL, EF Core, health, telemetry and adapters
│   ├── LlmProxy.Api/             # .NET 10 HTTP/API host
│   └── LlmProxy.Admin/           # React + TypeScript administration UI
│
├── tests/                        # All automated test code
│   ├── backend/
│   │   ├── LlmProxy.UnitTests/   # xUnit domain/application/infrastructure tests
│   │   └── integration/          # Docker + PostgreSQL + mock inference runtimes
│   └── frontend/
│       ├── unit/                 # Vitest + Testing Library
│       └── e2e/                  # Playwright Chromium tests
│
├── docker/                       # Container and VM deployment assets
│   ├── Dockerfile
│   ├── docker-compose.yml
│   ├── docker-compose.prod.yml
│   ├── .env.example
│   └── scripts/
│
├── .github/workflows/            # CI, container publication and production deployment
│   ├── ci.yml
│   ├── container.yml
│   └── deploy.yml
│
├── Directory.Build.props
├── Directory.Packages.props
├── global.json
└── LlmProxy.slnx
```

## Domain model

Core concepts:

- **InferenceNode**: physical DGX/inference service root, node weight, capacity, administrative state and health diagnostics.
- **ModelDefinition**: logical client-facing model name plus provider model name.
- **ModelDeployment**: maps a logical model to a node and supplies deployment capacity/weight.
- **RoutingPolicy**: persisted active routing strategy.
- **ApiCredential**: revocable inference credential; only its secure hash is stored.
- **RequestMetric**: metadata-only inference telemetry.
- **AuditEvent**: administrative change with actor, action, entity, source IP and safe details.

## DGX service roots

A node stores the **complete inference service root**, not separate host/port fields. Valid examples include:

```text
http://localhost:3450/primopath
http://localhost:3451/altropath
http://127.0.0.1:8000
http://10.0.0.25:8000/vllm
https://dgx-01.internal:8443/inference
```

LlmProxy derives endpoints while preserving the prefix:

```text
<root>/health
<root>/v1/models
<root>/v1/chat/completions
<root>/v1/responses
```

The Admin UI includes a **Test connection** action that probes `/health` and `/v1/models` and reports status code, effective URL and latency.

## Public API

Current baseline:

```http
GET  /v1/models
POST /v1/chat/completions
POST /v1/responses
GET  /healthz
GET  /readyz
```

The gateway preserves OpenAI-compatible payload fields rather than binding them to a brittle closed DTO, allowing streaming, tools/function calling and future compatible fields to pass through.

## Request and streaming flow

```text
request
  -> validate bearer credential
  -> resolve logical model
  -> load eligible deployments
  -> exclude unhealthy/draining/disabled/full nodes
  -> select route
  -> rewrite logical model to provider model
  -> call selected DGX
  -> stream/copy response
  -> persist metadata-only request metric
```

For `text/event-stream` LlmProxy reads with response-header completion, forwards chunks immediately and flushes downstream. If a backend fails **before** downstream response bytes have started, another eligible backend may be attempted. Once streaming has started, the response is never continued from another model/node.

## Routing

Supported strategies:

```text
WeightedLeastLoaded  # default; recommended for long-running LLM requests
RoundRobin           # equal sequential rotation
WeightedRoundRobin   # rotation proportional to effective weight
```

Effective weight is:

```text
node weight × deployment weight
```

The initial policy can be bootstrapped with:

```text
Routing__Strategy
ROUTING_STRATEGY
```

After PostgreSQL is initialized, the persisted policy is authoritative. Administrators can change the strategy live through the React UI or:

```http
GET /api/admin/routing
PUT /api/admin/routing
```

No container restart is required.

## DGX health management

Health is a state machine, not a last-probe boolean. Defaults:

```text
Health__IntervalSeconds=10
Health__HealthyAfterSuccesses=2
Health__UnhealthyAfterFailures=3
```

Docker equivalents:

```text
HEALTH_INTERVAL_SECONDS=10
HEALTH_HEALTHY_AFTER_SUCCESSES=2
HEALTH_UNHEALTHY_AFTER_FAILURES=3
```

Typical transition:

```text
Healthy
   |
   | first failed probe
   v
Degraded
   |
   | failure threshold
   v
Unhealthy
   |
   | successful recovery probes
   v
Degraded
   |
   | success threshold
   v
Healthy
```

Each node persists last check time, last healthy time, last latency, last error and consecutive success/failure counters. `Draining` and `Disabled` are administrative states and are not overwritten by health probes.

See [`docs/operations.md`](docs/operations.md).

## Administration and audit

The React control plane manages:

- gateway overview;
- DGX nodes and connection tests;
- health diagnostics;
- logical models;
- deployments;
- live routing policy;
- inference API credentials;
- request metrics;
- administrative audit trail.

Administration is designed for Entra ID OIDC with:

```text
LlmProxy.Admin
LlmProxy.Reader
```

Audited actions currently include routing changes, node creation/update/test/drain/enable/disable, model creation, deployment creation/update and credential creation/revocation.

```http
GET /api/admin/audit?take=100
```

With Entra enabled the actor comes from the authenticated principal. Local development records `local-admin`. Audit detail payloads are bounded and must never include raw credentials, prompts, generated code, Entra secrets or Cloudflare tokens.

## Inference authentication

Inference uses static bearer credentials suitable for GitHub Copilot custom OpenAI-compatible providers:

```http
Authorization: Bearer lp_xxxxxxxxxxxxxxxxxxxxxxxxx
```

The raw secret is shown once and never stored. PostgreSQL stores a keyed hash using a server-side pepper.

## Persistence

PostgreSQL runs as a separate container. EF Core migrations are applied during application startup.

Persisted areas include:

```text
nodes
models
deployments
routing_policy
api_credentials
request_metrics
audit_events
```

Prompts and generated code are not persisted by default.

## Docker

Local stack:

```bash
cp docker/.env.example docker/.env
docker compose -f docker/docker-compose.yml up --build
```

Production overlays the validated GHCR image and Cloudflare Tunnel:

```bash
docker compose \
  -f docker/docker-compose.yml \
  -f docker/docker-compose.prod.yml \
  up -d
```

## Testing

All automated test code is under `tests/`; `src/` contains product code only.

Quality gate:

1. .NET 10 restore and Release build;
2. xUnit domain/application/infrastructure tests;
3. React production build;
4. Vitest + Testing Library;
5. Playwright Chromium E2E;
6. production Docker image build;
7. real PostgreSQL + migrations;
8. two controllable local inference runtimes on distinct ports/path prefixes;
9. service-root-aware `/health` and `/v1/models` probes;
10. weighted routing and live round-robin switching;
11. routing-policy persistence after process restart;
12. actual SSE first-chunk delivery before completion;
13. real health transition `Healthy -> Degraded -> Unhealthy -> Degraded -> Healthy`;
14. persisted health diagnostics;
15. persisted administrative audit events.

Mocking rule: **mock external boundaries, not domain behavior**. PostgreSQL/container wiring, service-root composition, routing, health state transitions and streaming proxy behavior are tested with real components where that gives meaningful confidence.

See [`tests/README.md`](tests/README.md), [`docs/testing.md`](docs/testing.md), [`docs/operations.md`](docs/operations.md), and [`docs/dgx-vllm.md`](docs/dgx-vllm.md).

Backend unit tests:

```bash
dotnet test tests/backend/LlmProxy.UnitTests/LlmProxy.UnitTests.csproj -c Release
```

Frontend tests:

```bash
cd tests/frontend
npm install
npm test
npx playwright install chromium
npm run test:e2e
```

Docker integration:

```bash
tests/backend/integration/smoke.sh
```

## CI/CD

PRs and pushes to `main` execute the complete quality gate. Backend and frontend checks run in parallel; Docker/PostgreSQL/runtime integration runs only after both succeed.

A `main` container is published to GHCR only after CI for that exact commit succeeds. Version tags can publish immutable versioned images:

```text
ghcr.io/<owner>/llmproxy:main
ghcr.io/<owner>/llmproxy:sha-abc1234
ghcr.io/<owner>/llmproxy:1.2.3
```

Production deployment uses a GitHub Actions self-hosted runner on the target VM, so the VM can pull and deploy containers using outbound GitHub connectivity rather than requiring a public inbound SSH port.

## Important configuration

Secrets and production-specific settings must not be committed:

```text
ConnectionStrings__Postgres
Authentication__ApiKeyPepper
EntraId__TenantId
EntraId__ClientId
EntraId__ClientSecret
CLOUDFLARE_TUNNEL_TOKEN
```

Operational configuration includes:

```text
Routing__Strategy
Health__IntervalSeconds
Health__HealthyAfterSuccesses
Health__UnhealthyAfterFailures
```

## Local prerequisites

- .NET 10 SDK
- Node.js 22+
- Python 3
- Docker Engine / Docker Desktop
- Docker Compose v2

## Roadmap

### Milestone 1 — Real Copilot spike

```text
GitHub Copilot
  -> Cloudflare domain
  -> LlmProxy
  -> DGX Spark
  -> vLLM
  -> model
```

Validate `/v1/models`, Chat Completions, Responses, SSE, tool calling, cancellation and authentication with an actual GitHub Copilot client.

### Milestone 2 — DGX/GPU observability

Add TTFT, token throughput, queue depth, GPU utilization/memory and capacity-aware routing inputs.

### Milestone 3 — Enterprise hardening

Complete Entra deployment, role assignment, richer audit filtering/export, operational alerts, backup/restore and HA design for the control plane.

### Milestone 4 — Capacity benchmark

Benchmark realistic Copilot concurrency per model and DGX, then derive production limits from measured TTFT, token rate, memory pressure and error behavior.

## Definition of done for V1

V1 is complete when GitHub Copilot can select a logical model exposed by LlmProxy; requests stream through the gateway to vLLM on DGX; full node URLs with host/IP/port/path are supported; routing is configurable live; unhealthy/draining nodes stop receiving traffic; health recovery is stable; administrators can manage and audit the platform through the React UI using Entra ID; inference uses revocable credentials; PostgreSQL survives container replacement; the automated quality gate is green; and GitHub Actions can publish, deploy and roll back validated images.

## License

Internal project. Licensing and external distribution terms will be defined before productization.
