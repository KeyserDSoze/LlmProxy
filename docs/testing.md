# Testing strategy

LlmProxy uses a layered test strategy designed to catch routing, security, UI and container regressions without turning every test into an expensive end-to-end environment test.

## Test pyramid

### Unit tests

Fast tests run against the Domain and Application layers. Domain entities are tested directly. Application services use fakes at ports/interfaces such as `IDeploymentCatalog`, `IRouteSelector` and `IRequestLoadTracker`.

Mocking rule: **mock boundaries, not business rules**. We do not mock `InferenceNode`, `ApiCredential`, routing scores or other domain behavior merely to make a test easier.

### Infrastructure-focused tests

Deterministic infrastructure components such as API-key hashing are tested with controlled configuration and host environment values. Network calls, Entra ID and DGX inference are not invoked from unit tests.

### Backend integration

The Docker smoke suite starts the real LlmProxy image together with PostgreSQL, applies EF Core migrations at application startup and verifies readiness, health, authentication and the OpenAI-compatible model catalog.

This validates the actual container wiring and PostgreSQL connectivity rather than replacing the database with an in-memory implementation that behaves differently from production.

### Frontend unit/component

Vitest and Testing Library validate React behavior and the browser API client. `fetch` is mocked only at the HTTP boundary. Component tests use the real React components while the API module is replaced with deterministic test doubles.

### Browser E2E

Playwright runs Chromium against the real Vite-served React application. Admin API calls are intercepted at the browser network layer so flows such as navigation, DGX creation and authentication errors can be exercised deterministically.

### Environment acceptance

The following scenarios require the real target environment and are not part of every pull request:

1. Entra ID login and application-role authorization.
2. Cloudflare Tunnel public-domain reachability.
3. GitHub Copilot BYOK against the public OpenAI-compatible endpoint.
4. Real DGX/vLLM streaming and tool calling.
5. Multi-DGX failover and drain under concurrent load.
6. Capacity and latency benchmarks.

## CI quality gate

A change is considered CI-valid only when all of the following succeed:

- .NET restore/build;
- backend unit tests;
- React production build;
- Vitest frontend tests;
- Playwright Chromium E2E tests;
- production Docker image build;
- Docker/PostgreSQL backend smoke tests.

Playwright traces/reports should be retained when browser tests fail so a UI failure can be reproduced without guessing.
