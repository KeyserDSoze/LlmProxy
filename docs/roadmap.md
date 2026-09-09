# Roadmap

## M0 - Repository bootstrap

- DDD single-domain structure.
- .NET 10 solution.
- React/TypeScript admin shell.
- PostgreSQL persistence skeleton.
- Docker Compose.
- GitHub Actions CI/container/deploy workflows.
- Architecture/security/deployment documentation.

## M1 - Copilot -> gateway -> one DGX

- Seed one DGX node, one logical model and one deployment.
- OpenAI-compatible `/v1/models`.
- OpenAI-compatible `/v1/chat/completions`.
- SSE streaming.
- Preserve tool/function-calling payload fields.
- Bootstrap API-key authentication.
- Validate with real GitHub Copilot BYOK.

## M2 - Multi-DGX

- Admin CRUD for nodes/models/deployments.
- Active health monitor.
- Weighted least-loaded routing.
- Failover.
- Drain mode.
- Per-node concurrency controls.

## M3 - Enterprise administration

- Entra ID production sign-in.
- `Admin` and `Reader` app roles.
- React operational dashboard.
- Database-backed API credentials.
- Audit log.

## M4 - Observability

- Request metrics.
- TTFT and latency percentiles.
- Runtime token throughput.
- DGX GPU/memory telemetry.
- OpenTelemetry export.
- Prometheus/Grafana option.

## M5 - Capacity and smart routing

- Load tests representative of Copilot coding traffic.
- Queue-depth-aware routing.
- GPU-aware routing.
- Model-specific concurrency.
- Capacity planning for 200 assigned developers and measured concurrency profiles.

## M6 - Product hardening

- Automated DB migrations.
- Backup/restore runbook.
- Credential rotation.
- Rate limits/quotas.
- High availability for the gateway if required.
- Upgrade strategy for model/runtime changes.
