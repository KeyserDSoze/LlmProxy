# Testing strategy

LlmProxy uses a layered test strategy designed to catch routing, security, UI, container and performance-tooling regressions without turning every test into an expensive end-to-end environment test.

## Test pyramid

### Unit tests

Fast tests run against the Domain and Application layers. Domain entities are tested directly. Application services use fakes at ports/interfaces such as `IDeploymentCatalog`, `IRouteSelector` and `IRequestLoadTracker`.

Mocking rule: **mock boundaries, not business rules**. We do not mock `InferenceNode`, `ApiCredential`, routing scores or other domain behavior merely to make a test easier.

### Infrastructure-focused tests

Deterministic infrastructure components such as API-key hashing, vLLM/DCGM Prometheus parsing and in-memory telemetry trackers are tested with controlled inputs. Network calls, Entra ID and real inference node inference are not invoked from unit tests.

### Backend integration

The Docker smoke suites start the real LlmProxy image together with PostgreSQL, apply EF Core migrations at application startup and verify readiness, health, authentication, OpenAI-compatible endpoints, routing, persistence and streaming.

Two controllable fake inference runtimes run on distinct ports/path prefixes so service-root composition, weighted routing, live routing-policy updates, failover and SSE behavior are exercised over real HTTP.

A dedicated hardware smoke suite starts a fake DCGM Prometheus endpoint independently from vLLM. It verifies path-prefixed hardware roots, multi-GPU aggregation, audit, the invariant `DCGM failure != inference-node failure`, retention of the last successful diagnostic sample after a transient exporter failure, and removal of the runtime snapshot after an explicit endpoint clear.

This validates actual container wiring and PostgreSQL connectivity rather than replacing the database with an in-memory implementation that behaves differently from production.

### Frontend unit/component

Vitest and Testing Library validate React behavior and the browser API client. `fetch` is mocked only at the HTTP boundary. Component tests use the real React components while the API module is replaced with deterministic test doubles.

### Browser E2E

Playwright runs Chromium against the real Vite-served React application. Admin API calls are intercepted at the browser network layer so flows such as navigation, inference node creation, hardware telemetry administration, routing tuning and authentication errors can be exercised deterministically.

### Visual, layout and accessibility regression

The Playwright `visual` project (`tests/frontend/e2e/visual.spec.ts`) renders every Admin and user-portal screen against the shared deterministic mock in `e2e/support/mockAdmin.ts` with a frozen clock. Each screen fails on uncaught page errors, console errors, failed API calls, page-level or unintended inner horizontal overflow (desktop and 390 px phone) and serious/critical axe-core WCAG 2.1 A/AA violations. A dedicated test covers the failed llama.cpp start card and the phone navigation drawer.

Pixel comparison runs only with `LLMPROXY_VISUAL_COMPARE=1`, which `npm run test:visual:docker` sets inside `mcr.microsoft.com/playwright:v<@playwright/test version>-noble`. Baselines live in `tests/frontend/e2e/__screenshots__/` without an OS suffix because they are never rendered on the host. Budget: 100 differing pixels per screenshot. After an intentional UI change run `npm run test:visual:update`, review the changed PNGs and commit them. `npm run test:visual` on the host still runs the overflow, console and axe checks and attaches screenshots without comparing them. If another project already serves port 4173, set `LLMPROXY_E2E_PORT` (for example `4373`) so Playwright starts its own dev server instead of attaching to the foreign one; on a memory-constrained workstation add `--workers=2`.

### Live Admin smoke

`tests/backend/integration/admin_ui_live_smoke.sh` starts the Compose stack (LlmProxy + PostgreSQL, Entra disabled) and runs `npm run test:live` (`tests/frontend/live`, no API mocks): every workspace must render without page errors or 404/405/5xx API responses, an organization key created in the UI must authenticate and then be rejected after revocation, and a routing strategy chosen in the UI must be persisted by the API. CI runs it at the end of the Docker/PostgreSQL integration job and uploads the Playwright report on failure.

### Performance tooling

`tests/performance/` contains a .NET 10 benchmark console and its unit tests. CI compiles and tests the harness itself but deliberately does **not** run load against an inference endpoint.

Benchmark-tool unit coverage includes:

- option/range validation;
- path-prefixed service-root endpoint construction;
- percentile calculations;
- Chat Completions and Responses usage extraction;
- SSE first-output detection;
- report serialization safety.

Real concurrency sweeps are environment/performance tests and must be started deliberately. See `docs/benchmarking.md`.

### Environment acceptance

The following scenarios require the real target environment and are not part of every pull request:

1. Entra ID login and application-role authorization.
2. Cloudflare Tunnel public-domain reachability.
3. GitHub Copilot BYOK against the public OpenAI-compatible endpoint.
4. Real inference node/vLLM streaming and tool calling.
5. Multi-node failover and drain under concurrent real-model load.
6. Capacity and latency benchmarks against the intended inference node/model/runtime profile.

## CI quality gate

A change is considered CI-valid only when all of the following succeed:

- .NET restore/build;
- backend unit tests;
- benchmark-harness unit tests;
- React production build;
- Vitest frontend tests;
- Playwright Chromium E2E tests (`--project=chromium`);
- Playwright visual regression, layout and accessibility job (`visual`);
- production Docker image build;
- Docker/PostgreSQL backend smoke tests;
- live Admin UI smoke against the real gateway (`admin_ui_live_smoke.sh`);
- dedicated DCGM hardware smoke tests.

Playwright traces/reports are retained when browser tests fail so a UI failure can be reproduced without guessing.

CI uses `cancel-in-progress` for superseded runs on the same branch/PR. Integration scripts are invoked through `bash` so their execution does not depend on executable mode surviving repository-content API writes.

## Performance-test safety

CI must never gain a hard-coded remote inference node target. A future self-hosted benchmark workflow, if added, must be manual (`workflow_dispatch`), environment-scoped and explicit about target/model/concurrency. Credentials must come from protected secrets/environment variables and benchmark artifacts must continue to exclude prompt bodies and bearer tokens.
