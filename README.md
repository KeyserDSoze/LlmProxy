# LlmProxy

Enterprise OpenAI-compatible gateway for routing GitHub Copilot and other AI clients to on-premises LLMs running on NVIDIA DGX infrastructure.

> Status: V1 under active development.

## What this product is

LlmProxy is the internal control plane between AI clients and the physical inference fleet. GitHub Copilot or any OpenAI-compatible client sees one stable endpoint and logical model names; the gateway decides which DGX/model deployment should serve each request.

The first production target is deliberately simple: one on-premises VM running Docker, PostgreSQL and Cloudflare Tunnel, connected over the private LAN to one or more DGX Spark nodes running an OpenAI-compatible inference runtime such as vLLM.

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
- **Logical models**: clients request aliases such as `agic-code-fast`; physical model names remain internal.
- **Complete node service roots**: a DGX address may be `localhost`, DNS, IPv4/IPv6, a custom port and an optional path prefix such as `http://localhost:3450/primopath`.
- **Single-domain DDD**: one bounded context, **AI Inference Gateway**, split into Domain, Application, Infrastructure and API layers.
- **Multi-DGX from day one**: V1 can start with one node but the domain already supports N nodes, N models and N deployments.
- **Multiple routing strategies**: `WeightedLeastLoaded`, `RoundRobin` and `WeightedRoundRobin`.
- **Streaming is a first-class contract**: SSE is forwarded incrementally and never retried after response bytes have started.
- **Enterprise security**: Entra ID protects administration; revocable bearer credentials protect inference.
- **No prompt logging by default**: telemetry stores operational metadata, not prompts or generated source code.
- **Testable boundaries**: external systems are mocked/faked in unit tests, while PostgreSQL, Docker, multi-runtime routing and SSE are exercised for real in integration tests.
- **Immutable delivery**: validated images are published to GHCR and deployed to the VM by GitHub Actions.

## Repository structure

```text
.
├── docs/                         # Architecture, security, deployment and product documentation
│   ├── architecture.md
│   ├── api-contract.md
│   ├── deployment.md
│   ├── dgx-vllm.md
│   ├── github-copilot.md
│   ├── roadmap.md
│   ├── security.md
│   └── testing.md
│
├── src/                          # Product code only
│   ├── LlmProxy.Domain/          # Entities, invariants and domain rules
│   ├── LlmProxy.Application/     # Use cases, ports and orchestration
│   ├── LlmProxy.Infrastructure/  # PostgreSQL, EF Core, routing adapters, health, telemetry
│   ├── LlmProxy.Api/             # .NET 10 HTTP/API host
│   └── LlmProxy.Admin/           # React + TypeScript administration UI
│
├── tests/                        # All automated test code
│   ├── backend/
│   │   ├── LlmProxy.UnitTests/   # xUnit domain/application/infrastructure tests
│   │   └── integration/          # Real Docker + PostgreSQL + mock inference runtimes
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
├── .github/workflows/            # CI, image publication and production deployment
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

The core product concepts are:

- **InferenceNode**: a physical DGX/inference machine with a complete service-root URL, node weight, capacity, enabled state and health state.
- **ModelDefinition**: the logical model exposed to clients and the underlying provider model name.
- **Deployment**: maps a logical model to a node and defines deployment routing/capacity settings.
- **ApiCredential**: a revocable inference credential; only its secure hash is persisted.
- **RequestMetric**: operational telemetry for an inference request without prompt/response content.

A node service root can be, for example:

```text
http://localhost:3450/primopath
http://localhost:3451/altropath
http://127.0.0.1:8000
http://10.0.0.25:8000/vllm
https://dgx-01.internal:8443/inference
```

LlmProxy appends `/health`, `/v1/chat/completions`, or `/v1/responses` while preserving the configured prefix.

## Request flow

```text
POST /v1/chat/completions or /v1/responses
        |
        v
Validate bearer credential
        |
        v
Resolve logical model
        |
        v
Load eligible deployments
        |
        v
Remove disabled / unhealthy / draining / full nodes
        |
        v
Apply configured routing strategy
        |
        v
Forward OpenAI-style request to selected service root
        |
        +---- failure before response commit? -------+
        |                                            |
        |                                  exclude deployment and retry
        v
Stream/copy response to client
        |
        +---- stream already started? no failover ---+
        |
        v
Persist metadata-only request metric
```

## Public API

Current baseline:

```http
GET  /v1/models
POST /v1/chat/completions
POST /v1/responses
GET  /healthz
GET  /readyz
```

The proxy preserves OpenAI-compatible request payloads so streaming and tool/function calling can pass through to the inference runtime.

For `text/event-stream`, LlmProxy disables server-side buffering where supported and flushes upstream chunks incrementally to the caller.

## Routing

Routing is selected with:

```text
Routing__Strategy
```

or in Docker:

```text
ROUTING_STRATEGY
```

Supported values:

```text
WeightedLeastLoaded  # default, optimized for long-running concurrent LLM requests
RoundRobin           # equal sequential rotation
WeightedRoundRobin   # sequential rotation proportional to effective weight
```

For weighted strategies the candidate weight is:

```text
node weight × deployment weight
```

Health, drain state and concurrency limits are always enforced first.

## Administration

The React administration UI is served by the same application container and manages:

- gateway overview;
- DGX nodes;
- logical models;
- model deployments;
- inference API credentials;
- request metrics.

Administration is designed for Entra ID OIDC with the initial application roles:

```text
LlmProxy.Admin
LlmProxy.Reader
```

## Inference authentication

Inference uses static bearer credentials suitable for GitHub Copilot custom OpenAI-compatible providers:

```http
Authorization: Bearer lp_xxxxxxxxxxxxxxxxxxxxxxxxx
```

The raw secret is shown once at creation time and is never stored in PostgreSQL. Validation uses a keyed hash with a production-only pepper supplied through configuration.

## DGX runtime

Each registered node exposes an OpenAI-compatible private service root. A bare vLLM node may be:

```text
http://dgx-01:8000
```

while a reverse-proxied or local test runtime may be:

```text
http://localhost:3450/primopath
http://10.0.0.25:8000/vllm
```

Clients never receive these addresses. Health, routing and failover are managed by LlmProxy.

## Persistence

PostgreSQL is a separate container. EF Core migrations are applied by the application at startup so application/container upgrades can evolve the schema reproducibly.

Persisted areas currently include nodes, logical models, deployments, API credentials and request metrics. Prompt content and generated code are not persisted by default.

## Docker

Local full stack:

```bash
cp docker/.env.example docker/.env
docker compose -f docker/docker-compose.yml up --build
```

Production composition adds the published GHCR image and Cloudflare Tunnel:

```bash
docker compose \
  -f docker/docker-compose.yml \
  -f docker/docker-compose.prod.yml \
  up -d
```

## Testing

All test code is kept outside `src/` in `tests/`.

The CI quality gate includes:

1. .NET restore and Release build;
2. backend xUnit unit tests;
3. React production build;
4. Vitest + Testing Library frontend tests;
5. Playwright Chromium E2E tests;
6. production Docker image build;
7. real PostgreSQL + LlmProxy integration;
8. two local mock inference runtimes on different ports and path prefixes;
9. path-aware multi-node weighted routing;
10. actual SSE first-chunk delivery before stream completion.

Mocking policy: **mock external boundaries, not domain behavior**. Application ports and browser HTTP calls are replaced with deterministic test doubles in fast tests. PostgreSQL/container wiring and streaming proxy behavior are tested with real components rather than an in-memory substitute.

See [`tests/README.md`](tests/README.md), [`docs/testing.md`](docs/testing.md), and [`docs/dgx-vllm.md`](docs/dgx-vllm.md).

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

Backend Docker integration:

```bash
tests/backend/integration/smoke.sh
```

## CI/CD

### CI

Pull requests and pushes to `main` execute the complete automated quality gate. Backend and frontend tests run in parallel; Docker/PostgreSQL/inference-runtime integration starts only when both have succeeded.

### Container publication

A `main` image is published to GHCR **only after the CI workflow for that exact commit succeeds**. Version tags (`v1.2.3`) can also publish immutable versioned images.

Example tags:

```text
ghcr.io/<owner>/llmproxy:main
ghcr.io/<owner>/llmproxy:sha-abc1234
ghcr.io/<owner>/llmproxy:1.2.3
```

### Production deployment

The target VM hosts a GitHub Actions self-hosted runner. The production workflow runs locally on that VM, pulls the requested GHCR image and executes Docker Compose. The VM therefore does not need a public SSH port merely for CI/CD.

## Configuration

Secrets are supplied at runtime and must never be committed. Important production values include:

```text
ConnectionStrings__Postgres
Authentication__ApiKeyPepper
Routing__Strategy
EntraId__TenantId
EntraId__ClientId
EntraId__ClientSecret
CLOUDFLARE_TUNNEL_TOKEN
```

## Local prerequisites

- .NET 10 SDK
- Node.js 22+
- Python 3 for backend mock-runtime integration tests
- Docker Engine / Docker Desktop
- Docker Compose v2

## Local backend

```bash
dotnet restore LlmProxy.slnx
dotnet build LlmProxy.slnx
dotnet test tests/backend/LlmProxy.UnitTests/LlmProxy.UnitTests.csproj
```

## Local admin UI

```bash
cd src/LlmProxy.Admin
npm install
npm run dev
```

## Roadmap

### Milestone 1 — Real Copilot spike

```text
GitHub Copilot
  -> Cloudflare domain
  -> LlmProxy
  -> one DGX Spark
  -> vLLM
  -> model
```

Validate `/v1/models`, Chat Completions, Responses, SSE streaming, tool calling, cancellation and authentication with an actual Copilot client.

### Milestone 2 — Multi-DGX hardening

Benchmark and harden health, drain, capacity, failover and configurable routing across multiple Spark nodes.

### Milestone 3 — Enterprise administration

Complete Entra ID setup, RBAC, audit trail, dynamic routing-policy configuration and richer operational dashboards.

### Milestone 4 — Capacity and observability

Add TTFT, token throughput, queue metrics, DGX/GPU telemetry and capacity benchmarks under realistic Copilot concurrency.

## Definition of done for V1

V1 is complete when GitHub Copilot can select a logical model exposed by LlmProxy; requests stream through the gateway to vLLM on DGX; full node URLs with host/IP/port/path are supported; adding/removing DGX nodes does not require client reconfiguration; administrators can manage the platform through the React UI using Entra ID; inference uses revocable credentials; unhealthy/draining nodes stop receiving traffic; PostgreSQL survives container replacement; the full automated quality gate is green; and GitHub Actions can publish, deploy and roll back validated images.

## License

Internal project. Licensing and external distribution terms will be defined before productization.
