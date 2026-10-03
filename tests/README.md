# Test suite

All automated test code lives under `tests/`. Product code belongs under `src/` only.

## Structure

```text
tests/
├── backend/
│   ├── LlmProxy.UnitTests/       # Domain/application/infrastructure unit tests
│   └── integration/              # Docker/PostgreSQL/API smoke and integration tests
└── frontend/
    ├── unit/                     # Vitest + Testing Library
    └── e2e/                      # Playwright browser tests
```

## Testing strategy

- **Domain tests** do not mock domain objects. They exercise invariants directly.
- **Application tests** replace external ports such as catalogs/selectors/load trackers with small fakes or mocks so orchestration is tested independently.
- **Infrastructure tests** test deterministic infrastructure code directly and isolate external systems when appropriate.
- **Backend integration tests** use the real application container and real PostgreSQL container. vLLM/inference node is not required for the base smoke suite.
- **Frontend unit tests** use Vitest and Testing Library. Browser-independent API behavior is tested with `fetch` mocked at the network boundary.
- **Frontend E2E tests** use Playwright with browser-level API interception. They exercise the real React application without requiring Entra ID, PostgreSQL or inference node for every UI test.
- Full infrastructure acceptance tests against real Entra ID, Cloudflare, inference node and vLLM are executed in the target environment and are intentionally separate from the fast CI suite.

## Commands

Backend unit tests:

```bash
dotnet test tests/backend/LlmProxy.UnitTests/LlmProxy.UnitTests.csproj -c Release
```

Backend integration smoke test:

```bash
tests/backend/integration/smoke.sh
```

Frontend unit tests:

```bash
cd tests/frontend
npm install
npm test
```

Frontend Playwright tests:

```bash
cd tests/frontend
npm install
npx playwright install chromium
npm run test:e2e
```
