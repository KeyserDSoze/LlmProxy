# Development log

This is the chronological engineering trace for LlmProxy. For canonical current state and exact resume point use `docs/project-status.md`.

## 2026-09-09 — Repository, gateway and multi-DGX foundation

Created the .NET 10 layered solution, React/TypeScript admin, PostgreSQL persistence, Docker packaging and GitHub Actions foundations. Added logical client-facing models, internal DGX nodes/deployments, `/v1/models`, `/v1/chat/completions`, SSE streaming and `/v1/responses`.

Added bearer credentials, Entra administration plumbing, node/model/deployment management, health hysteresis, drain/disable, audit, weighted least loaded / round robin / weighted round robin and pre-response-only failover. Added metadata-only request metrics, vLLM pressure signals and smart-routing tuning. Prompts/source/generated content remain excluded from telemetry.

## 2026-09-09 — Repository-first handover discipline

Introduced root `AGENTS.md` and the rule that meaningful increments update focused docs, canonical project status, development log and roadmap with actual validation evidence.

## 2026-09-10 — DGX/DCGM hardware telemetry — VALIDATED

Added optional per-node NVIDIA/DCGM telemetry independent from vLLM health. GPU utilization, framebuffer memory, temperature and power remain observational.

Checkpoint: `6c238a095273843e713a72fb2e26b2c7c434fc62`.

## 2026-09-10 — Architecture decision: custom LlmProxy, not NVIDIA PAIR

NVIDIA Personal AI Router was evaluated. The project owner chose custom LlmProxy + vLLM.

## 2026-09-10 — Benchmark harness — VALIDATED

Added the .NET benchmark harness under `tests/performance/` for direct-vLLM vs gateway measurements, streaming/non-streaming, Chat/Responses and concurrency sweeps.

Checkpoint: `49e7932f14118be403eec042a1393946143776ae`.

## 2026-09-13 — Capacity Profiles + physical-node admission — VALIDATED

Added persisted benchmark-derived Capacity Profiles and aggregate physical-node concurrency admission. Defined saturation as `429 capacity_exhausted` with `Retry-After: 1`.

Checkpoint: `600ad42cc53ad1e97a259819654ca5cf5480e1db`.

## 2026-09-14 — Operator onboarding / GHCR deployment

Added Linux/Windows quickstart, private GHCR path and minimal/full Compose installation shapes.

## 2026-09-14 — Caller governance + Usage Groups — VALIDATED

Implemented persisted Usage Groups, request-time group snapshots, credential/model request-rate policies, governance audit and usage reporting/UI.

```text
commit 798f0a460dcc4f89b17e2ce89df66f511d324241
CI     34859931084
```

## 2026-09-14 — Runtime credential authentication — VALIDATED

Removed API-credential SQL lookup from `/v1`; added local runtime credential cache, startup rebuild, live publication and buffered last-used persistence.

```text
commit 1f607c8433fe2ca08a1c243b68d87587204f35ee
CI     34860662747
```

## 2026-09-14 — Runtime route/model/deployment catalog — VALIDATED

Removed synchronous route-catalog SQL lookup from ordinary inference and added PostgreSQL-outage coverage for already-published state.

```text
commit 42c44753cd00d679a81bf065f410b7a497cdc000
CI     34871542047
```

## 2026-09-14 — Retention foundation

Added independent request-metric and audit retention, background batched cleanup and manual audited cleanup. Defaults: request metrics 90 days, audit 365 days.

## 2026-09-15 — Redis L2 synchronization + observability stack

Promoted Redis to shared runtime/coordination L2 while preserving local L1 and PostgreSQL durable authority. Added Redis snapshot/version/event synchronization, reconciliation and bundled OTEL Collector + Tempo + Loki + Prometheus + Grafana.

## 2026-09-15 — Shared request-rate counters + distributed DGX capacity leases

Request-rate policies stay in local runtime state while distributed counters use Redis. Added Redis atomic deployment + physical-node capacity leases and fail-closed acquisition.

Key capacity milestone:

```text
cc454c325810b39a419107f49ad42a3ab7b70769
feat: coordinate physical capacity through redis leases
```

## 2026-09-15 — Active lease-loss cancellation and hardening — VALIDATED

Introduced active lease coordination-loss signaling and inference cancellation, then hardened the watchdog sampling so cancellation occurs before Redis TTL reuse.

```text
a12afa877e6b44538f45bc60061a56c34a5895b2
5d49f464829525c69621504321c95209002c9884
12535e439b6b17413a01942ebeb8504ad655a5ca
3740692ffa3afa140e1a8f0ade5440e430838599
edb7008ca1e3548f80dde2f7242ed30f779b13a7
9d00c74c6ce50bf25004ea443f10e46fe0c43d2f
6ec3c29176584f2e0bffd98b5d8cbbb0e833e76f
```

Final lease-hardening validation:

```text
CI         34961566507 SUCCESS
Full Stack 34961566463 SUCCESS
```

## 2026-09-15 — Transactional runtime-state outbox — IMPLEMENTED

Closed the PostgreSQL-commit -> Redis-publication process-crash window.

```text
9c6289ef172bed0502068df112b6e6aec4ee8521
feat: add transactional runtime-state outbox
```

Added same-transaction outbox capture for runtime Node/Model/Deployment/Credential/RatePolicy mutations, one globally serialized publisher via PostgreSQL advisory lock, strict Id ordering, retry/backoff and acknowledged Redis state/version/pubsub publication before marking rows processed.

A non-originating gateway can win the publisher lock; therefore the durable publisher applies its acknowledged event to its own L1 because same-origin Redis pub/sub is intentionally ignored.

## 2026-09-15 — Transactional outbox fault validation — VALIDATED

```text
4d9e241f8f8feee5afdaae6f7926cb6bdca70439
test: validate transactional outbox recovery across replicas

e5b3bad2d46d7c61997f29f1b840b2f9ac601283
```

Fault smoke stops Redis, commits a rate policy, proves pending/retry state, stops the origin gateway, recovers Redis and requires the surviving peer to publish and enforce the change from its own L1.

```text
CI         34968324786 SUCCESS
Full Stack 34968114492 SUCCESS
```

## 2026-09-15 — Outbox diagnostics + retention hardening — VALIDATED

Added outbox backlog/retry/error diagnostics to `/api/admin/runtime-sync` and processed-outbox retention with a hard rule that pending rows are never deleted.

```text
97e3b8f6b909156cbd58363d12f2fcbaf0627f5a
cfc742db84223a7bed2f8a80cab3e5680615efad
CI         34969410867 SUCCESS
Full Stack 34969410860 SUCCESS
```

## 2026-09-15 — Outbox deployment wiring — VALIDATED

Exposed `REDIS_OUTBOX_BATCH_SIZE`, `REDIS_OUTBOX_POLL_MILLISECONDS` and `RETENTION_RUNTIME_STATE_OUTBOX_DAYS`. Strengthened runtime-sync fault diagnostics.

```text
79de2dfd7c995b5a6cac7e87fcf89e3e991d9d72
chore: wire outbox operations into deployment
CI         34976465066 SUCCESS
Full Stack 34976465149 SUCCESS
```

## 2026-09-15 — Output-token budget reservation/settlement — IMPLEMENTED

Implemented the first quota slice as an **output-token** budget attached to the existing credential/model `RateLimitPolicy`:

```text
ff9af90144a19159d3c8208d8cedd95500b3b984
feat: add output token budget reservations
```

A first CI compile exposed a missing namespace for the buffered metrics sink. The only backend compile fix was:

```text
3bd81bc80d1f0976ef9398e4b860318d91a27d75
fix: reference buffered metrics sink for token budgets
```

Implementation contract:

- `OutputTokensPerWindow` + `MaxOutputTokensPerRequest` are persisted and runtime-published with rate policies;
- request-rate updates preserve token-budget fields unless explicitly changed;
- Chat `max_completion_tokens` / `max_tokens` and Responses `max_output_tokens` are capped/injected before forwarding;
- an atomic reservation occurs before inference;
- 2xx responses with observed output usage refund unused reservation;
- no-upstream-attempt paths refund fully;
- cancellation/failure/interrupted-stream/missing-usage paths retain the full reservation conservatively;
- Redis-enabled token-budget admission is shared and fail closed; there is no local fallback;
- dedicated errors are `token_budget_exceeded` and `token_budget_coordination_unavailable`;
- quota policy changes reuse the transactional outbox + peer L1 pipeline.

Unit tests cover concurrency, reservation/refund, uncertain usage, window reset and Chat/Responses payload cap behavior.

## 2026-09-15 — Output-token budget integration validation — VALIDATED

Added local and distributed fault/integration coverage:

```text
887ebfac98389c0115eaf9c102a60133ede745ff
test: validate output token budgets end to end
```

Standard governance smoke proves budget `17`, reservation `10`, mock actual output `7`: first request settles to 7, second succeeds only because 3 tokens were refunded, third returns `429 token_budget_exceeded`. It also verifies invalid cap handling and policy rebuild after gateway restart.

Dedicated Redis smoke starts a peer before policy creation, proves live policy propagation into peer L1, then proves shared settlement `7 -> 14`, cross-gateway rejection, Redis-down fail-closed `503 token_budget_coordination_unavailable` and recovery with the exhausted window preserved.

Validation:

```text
CI         34987407172 SUCCESS
Full Stack 34987407169 SUCCESS
```

## 2026-09-15 — React Admin output-token budget management — VALIDATED

Added token-budget fields to the admin client types/API, budget visibility in the rate-policy table and a dedicated Apply/Clear workflow on `/admin/governance`. Clearing a token budget leaves request-rate policy intact.

Playwright covers create rate policy -> apply token budget -> visible limits -> clear budget.

```text
426c545e841865406615998ca50b28a45c40e6f4
feat: manage output token budgets in admin
CI 34988084106 SUCCESS
```

## 2026-09-15 — Credential rotation — IMPLEMENTED AND VALIDATED

Implemented API-key rotation as an in-place hard cutover on the existing credential identity.

Key implementation commits in the increment:

```text
2491996638185c4b38f477deabcefbf9534733c6  endpoint foundation
def147c2f20f3b7c77a1635bda2ee5c2ee07ca55  domain Rotate semantics
afc1ab6c9ae0eac16410e2bef51f15f01d365864  API wiring
a0699da9d836c3b5201683d512eef4ec7a080397  domain tests
352f8d2060e5f243e6c612216eda3a6f520ef0ee  cross-replica rotation smoke
049e59260db13e7c1a85c8bfc8555e2f78d95a2a  Full Stack gate wiring
82ef575bc2cc52f2cbe8fc3bdd10275fcf7e6105  admin client
a682ca195733bef9ebaa66998a8ad4e237af09bc  governance UI
2f96c27904d6238c5781f50e2c0e0bf0f3425423  Playwright workflow
628fbc15dc2c963db802f9f2d9aca4b324225c99  no-store one-time-secret hardening
```

Contract:

- same credential `Id`, name, timestamps, expiry, Usage Group and caller-policy linkage;
- new random secret, new prefix and HMAC;
- old HMAC is removed from local credential cache on same-ID upsert;
- revoked credentials cannot be rotated;
- raw replacement secret is returned once and never persisted;
- rotation response carries `Cache-Control: no-store`;
- `credential.rotate` audit stores only safe prefix transition/group/expiry metadata;
- Redis/outbox runtime state contains the new HMAC only;
- peers converge through the existing transactional runtime-state publication path.

The dedicated Full Stack smoke starts two gateways before rotation, verifies the old key on both, rotates through the primary, requires new-key 200 / old-key 401 on both, checks Redis for new-HMAC-only state, verifies Usage Group and rate-policy preservation, checks audit secrecy and restarts the peer to prove durable/startup hydration.

Validation:

```text
commit     628fbc15dc2c963db802f9f2d9aca4b324225c99
CI         34996328467 SUCCESS
Full Stack 34996328588 SUCCESS
```

## Next increment — backup/restore verification

Credential rotation is complete. The next production-hardening increment is backup/restore with an **actual clean-target restore test**, not documentation-only commands. Define the backup artifact, restore procedure and verification that durable configuration/credential hashes/governance state recover and a restored gateway can rebuild/publish runtime state and serve authenticated traffic.

Further quota expansion remains requirements-driven because input/total/cost budgets need explicit tokenizer/pricing semantics.
