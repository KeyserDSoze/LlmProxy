# LlmProxy

Enterprise OpenAI-compatible gateway for routing GitHub Copilot and other AI clients to on-premises LLMs running on NVIDIA DGX infrastructure.

> Status: initial product bootstrap.

## Vision

LlmProxy is designed as an internal AI gateway that hides the physical inference infrastructure from clients. Consumers such as GitHub Copilot see a stable OpenAI-compatible API and logical model names, while the gateway handles authentication, routing, health, load balancing, telemetry and administration of one or more DGX nodes.

The initial deployment target is a single on-premises VM running Docker containers. The architecture starts with one DGX node and is intentionally designed to scale to multiple DGX nodes and multiple models without changing client configuration.

## High-level architecture

```text
GitHub Copilot / OpenAI-compatible client
                |
                v
        Cloudflare Tunnel
                |
                v
+---------------------------------------------+
| On-prem VM                                  |
|                                             |
|  +---------------------------------------+  |
|  | LlmProxy (.NET 10)                    |  |
|  |                                       |  |
|  | /v1/*        OpenAI-compatible API    |  |
|  | /api/admin/* Administration API       |  |
|  | /admin/*     React administration UI |  |
|  | /healthz     Health endpoint          |  |
|  +-------------------+-------------------+  |
|                      |                      |
|              +-------v-------+              |
|              | PostgreSQL    |              |
|              +---------------+              |
|                                             |
|  cloudflared                                |
+----------------------+----------------------+
                       |
                       | private LAN
              +--------+---------+
              |                  |
              v                  v
          DGX Spark 01       DGX Spark N
             vLLM               vLLM
        OpenAI API          OpenAI API
```

## Core principles

1. **OpenAI-compatible contract**: clients integrate once against `/v1`; physical model/runtime details stay internal.
2. **Logical models**: clients request names such as `agic-code-fast` or `agic-code-reasoning`; administrators decide which physical model and DGX deployment serves them.
3. **Single-domain DDD**: the backend uses a pragmatic Domain-Driven Design structure around one bounded context: **AI Inference Gateway**. No unnecessary microservices.
4. **Start simple, scale safely**: V1 supports one DGX, while the domain model already supports N nodes, N models and N deployments.
5. **Security by default**: administration is intended to use Microsoft Entra ID and application roles; inference endpoints use dedicated API credentials compatible with GitHub Copilot BYOK.
6. **No prompt logging by default**: operational telemetry records metadata, not prompts or generated code.
7. **Immutable deployments**: GitHub Actions builds versioned images and production deployment supports straightforward rollback.

## Repository structure

```text
.
├── docs/                         # Product, architecture and operational documentation
│   ├── architecture.md
│   ├── roadmap.md
│   ├── api-contract.md
│   ├── deployment.md
│   └── security.md
│
├── src/                          # All application source code
│   ├── LlmProxy.Domain/          # Domain entities, value objects and domain rules
│   ├── LlmProxy.Application/     # Use cases, ports/interfaces and orchestration
│   ├── LlmProxy.Infrastructure/  # EF Core, PostgreSQL, vLLM and external adapters
│   ├── LlmProxy.Api/             # .NET 10 API, auth, OpenAI endpoints, admin endpoints
│   └── LlmProxy.Admin/           # React + TypeScript administration UI
│
├── docker/                       # Docker and VM deployment assets
│   ├── Dockerfile
│   ├── docker-compose.yml
│   ├── docker-compose.prod.yml
│   ├── .env.example
│   └── scripts/
│       ├── deploy.sh
│       └── rollback.sh
│
├── .github/
│   └── workflows/                # GitHub Actions pipelines
│       ├── ci.yml
│       ├── container.yml
│       └── deploy.yml
│
├── Directory.Build.props
├── Directory.Packages.props
├── global.json
└── LlmProxy.slnx
```

## Domain model

The first version models four primary concepts: `Node`, `ModelDefinition`, `Deployment` and `ApiCredential`.

- A **Node** is a physical inference machine, initially a DGX Spark.
- A **ModelDefinition** is a logical model visible to clients.
- A **Deployment** maps a logical model to a node/runtime endpoint and capacity.
- An **ApiCredential** represents a revocable bearer credential allowed to call `/v1/*`.

This keeps public model names independent from physical runtimes. For example, `agic-code-fast` may move from one quantized model to another without requiring client reconfiguration.

## Request flow

```text
POST /v1/chat/completions
        |
        v
Authentication
        |
        v
Resolve logical model
        |
        v
Find eligible deployments
        |
        v
Remove disabled/unhealthy/draining nodes
        |
        v
Select destination
        |
        v
Forward request to vLLM
        |
        v
Stream OpenAI-compatible response
```

The first routing implementation uses weighted least-active-request selection. The design can later include GPU load, queue depth, TTFT and tokens/sec without changing the public API.

## Planned API surface

### OpenAI-compatible

```http
GET  /v1/models
POST /v1/chat/completions
```

Planned next:

```http
POST /v1/responses
```

The compatibility layer must support at least streaming via SSE, non-streaming responses, system/user/assistant messages, tool/function calling, request cancellation, OpenAI-style errors and logical model aliases.

### Platform

```http
GET /healthz
GET /readyz
```

### Administration

```http
GET/POST/PUT/DELETE /api/admin/nodes
GET/POST/PUT/DELETE /api/admin/models
GET/POST/PUT/DELETE /api/admin/deployments
GET/POST/DELETE     /api/admin/api-credentials
GET                 /api/admin/metrics
GET                 /api/admin/audit
```

## Authentication model

### Administration

The administration UI will use Microsoft Entra ID with OpenID Connect. Authorization will be based on application roles, initially `LlmProxy.Admin` and `LlmProxy.Reader`.

The intended web architecture is a backend-for-frontend style flow where ASP.NET Core owns the OIDC session so that the React application does not need to persist privileged tokens in browser storage.

### Inference

The `/v1/*` API uses static bearer credentials compatible with GitHub Copilot custom OpenAI-compatible providers.

```http
Authorization: Bearer lp_xxxxxxxxxxxxxxxxxxxx
```

Only a secure hash of each credential is persisted.

## DGX runtime

The baseline runtime on DGX Spark is **vLLM**, exposed only on the private network with an OpenAI-compatible endpoint.

```text
http://dgx-01:8000/v1
http://dgx-02:8000/v1
http://dgx-03:8000/v1
```

Clients never receive or depend on these addresses.

## Persistence

PostgreSQL runs as a separate container on the VM. Initial persistence areas are nodes, models, deployments, API credentials, routing policies, health history, request metrics and audit events.

Production PostgreSQL data lives on a persistent Docker volume and is never baked into the application image.

## Observability

Initial telemetry should include total requests, active requests, selected logical model, selected deployment/node, time to first token, end-to-end latency, token counts when available, status/error category and node health. Prompt content and generated code are not persisted by default.

## Docker model

The VM initially runs three containers:

```text
llmproxy
postgres
cloudflared
```

The application container contains the published .NET API and the compiled React static assets. PostgreSQL and Cloudflare Tunnel remain separate containers.

Local development:

```bash
docker compose -f docker/docker-compose.yml up --build
```

Production:

```bash
docker compose -f docker/docker-compose.yml -f docker/docker-compose.prod.yml up -d
```

## CI/CD strategy

Pull requests and pushes to `main` execute backend restore/build/tests, frontend install/build and Docker build validation.

A successful push to `main` or a version tag publishes an immutable image to GHCR, for example:

```text
ghcr.io/<owner>/llmproxy:sha-<commit>
ghcr.io/<owner>/llmproxy:main
ghcr.io/<owner>/llmproxy:1.0.0
```

The target production VM hosts a GitHub Actions **self-hosted runner**. Deployment happens locally on the VM by pulling the desired image and running Docker Compose, avoiding a public SSH ingress solely for CI/CD.

## Configuration

Configuration uses environment variables and ASP.NET Core configuration. Secrets must never be committed. The repository contains only placeholders in `docker/.env.example`.

Expected production values include:

```text
ConnectionStrings__Postgres
Authentication__ApiKeyPepper
EntraId__TenantId
EntraId__ClientId
EntraId__ClientSecret
CLOUDFLARE_TUNNEL_TOKEN
```

## Development roadmap

### Milestone 1 - End-to-end spike

```text
GitHub Copilot -> LlmProxy -> one DGX Spark -> vLLM -> model
```

Deliverables: `/v1/models`, `/v1/chat/completions`, streaming, tool/function calling compatibility, API key authentication, one configured DGX deployment, Cloudflare Tunnel validation and a real Copilot test.

### Milestone 2 - Multi-node gateway

Dynamic node/deployment registry, health checks, weighted least-load routing, capacity, failover and drain mode.

### Milestone 3 - Administration

React UI, Entra ID, RBAC, CRUD for nodes/models/deployments, API credential lifecycle and audit trail.

### Milestone 4 - Observability and capacity

Detailed metrics, GPU/runtime telemetry, capacity-aware routing, benchmark suite and DGX fleet capacity planning.

## Local prerequisites

- .NET 10 SDK
- Node.js 22+
- Docker Engine / Docker Desktop
- Docker Compose v2

## Local backend

```bash
dotnet restore LlmProxy.slnx
dotnet build LlmProxy.slnx
```

## Local admin UI

```bash
cd src/LlmProxy.Admin
npm ci
npm run dev
```

## Local full stack

```bash
cp docker/.env.example docker/.env
docker compose -f docker/docker-compose.yml up --build
```

## Definition of done for V1

V1 is complete when GitHub Copilot can select a logical model exposed by LlmProxy; requests stream through the gateway to vLLM on DGX; adding DGX nodes does not change client configuration; administrators can manage nodes/models/deployments through the UI; admin access uses Entra ID; inference access uses revocable credentials; unhealthy nodes stop receiving new traffic; GitHub Actions can deploy and roll back the application; PostgreSQL data survives container replacement; and operational telemetry does not persist prompts or generated code by default.

## License

Internal project. Licensing and distribution terms will be defined before external productization.
