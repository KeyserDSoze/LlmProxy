# Operations: DGX health, safe maintenance, build identity and administrative audit

## Health state model

LlmProxy treats DGX health as a state machine rather than a single last-probe boolean.

Defaults:

```text
HEALTH_INTERVAL_SECONDS=10
HEALTH_HEALTHY_AFTER_SUCCESSES=2
HEALTH_UNHEALTHY_AFTER_FAILURES=3
```

Administrative `Draining` and `Disabled` states are not overwritten by background health probes. `Healthy`, `Degraded` and `Unknown` may remain routing-eligible subject to capacity; `Unhealthy`, `Draining` and `Disabled` are excluded.

## Safe model/runtime maintenance

Supported workflow:

```http
GET  /api/admin/nodes/{nodeId}/maintenance
POST /api/admin/nodes/{nodeId}/maintenance/drain
POST /api/admin/nodes/{nodeId}/maintenance/resume
```

### Begin drain

`POST .../maintenance/drain` establishes the new-admission block before committing the node to `Draining` and auditing `node.maintenance.drain`.

In Redis mode the maintenance marker is checked inside atomic capacity admission, so a peer with stale local route state still cannot admit new work. Existing work is allowed to finish.

Typical outcomes:

```text
202 drain started / existing work may remain
409 node_disabled
503 maintenance_coordination_unavailable
```

### Observe drain

Poll maintenance status until:

```text
nodeStatus = Draining
admissionBlocked = true
activeRequests = 0
drained = true
```

Do not restart/replace vLLM before drain completion unless interruption is explicitly accepted. LlmProxy never fails over an already-started downstream stream.

### Perform external upgrade

Once drained, perform the DGX/vLLM/model/driver/container operation outside LlmProxy. The gateway owns traffic safety and validation around the external runtime operation; it does not execute host upgrades itself.

### Resume with validation

`POST .../maintenance/resume` requires zero distributed active work, then checks:

1. `GET <service-root>/health`;
2. `GET <service-root>/v1/models`;
3. one non-streaming Chat Completions warm-up with one output token for each enabled provider model on the node.

Only successful validation returns the node to `Healthy` and clears the maintenance block.

Failure responses include:

```text
409 node_not_draining
409 node_still_draining
503 maintenance_coordination_unavailable
503 node_validation_failed
```

The legacy direct endpoint `POST /api/admin/nodes/{nodeId}/drain` is deprecated and must not be restored as a maintenance bypass.

Validated HA behavior is included in Full Stack `35075387186`; the original dedicated maintenance validation was Full Stack `35021524019`.

## Manual connection test

Admin `Test` calls:

```http
POST /api/admin/nodes/{nodeId}/test-connection
```

It probes the configured service root with derived health/OpenAI-compatible endpoints. Service roots may include host, port and a path prefix, for example:

```text
http://localhost:3450/primopath
http://10.0.0.25:8000
http://10.0.0.25:8000/vllm
https://dgx-01.internal:8443/inference
```

The manual test is diagnostic; persisted background health and maintenance resume have their own semantics.

## Product/build identity

Current product version:

```text
0.2.0-preview.2
```

Operators can inspect identity through:

```http
GET /healthz
GET /api/admin/product
```

and in Admin at:

```text
/admin/releases
```

The product object includes release channel/date, build revision/date and versioned patch notes.

Production images carry:

```text
LLMPROXY_BUILD_SHA
LLMPROXY_BUILD_DATE
org.opencontainers.image.version
org.opencontainers.image.revision
org.opencontainers.image.created
```

Publishing rules:

```text
green main CI       -> main + sha-<7>
matching Git tag    -> exact SemVer + sha-<7>
stable Git tag      -> may also publish major.minor alias
prerelease Git tag  -> never updates stable-looking alias
```

## Verify a published image

`0.2.0-preview.2` adds registry-native SBOM/provenance verification to the publish workflow. Operators should prefer the immutable digest over a mutable tag when recording or deploying a known build.

Validated example:

```text
image        ghcr.io/keyserdsoze/llmproxy
version      0.2.0-preview.2
source       d134603672f361474bac9ea330f3bd1a142b5dfa
digest       sha256:cc26617a5860e126957da2d0e59c8cd8accd1cd3280576d991819ddc2001d880
```

The publish gate reads the OCI index back from GHCR, follows `attestation-manifest` descriptors and requires in-toto layers containing:

```text
https://spdx.dev/Document
https://slsa.dev/provenance/...
```

Validated predicates for the current baseline:

```text
attestation manifest  sha256:83457ab3eb69c4aac638874daed1f2cf396fa157001f2be9257e48d0d067253d
SBOM predicate        https://spdx.dev/Document
provenance predicate  https://slsa.dev/provenance/v1
```

The successful publish also uploads `release-manifest.json` as Actions artifact `10440082178`. It records image, digest, version, source SHA, build timestamp and the verified attestation descriptors. Artifact retention is 30 days; the immutable image/attestations live in GHCR according to registry/package retention policy.

Current release/build validation:

```text
0.2.0-preview.2  d134603672f361474bac9ea330f3bd1a142b5dfa
CI               35080118201 SUCCESS
Publish GHCR     35080565404 SUCCESS
runtime FullStack 35075387186 SUCCESS
```

The runtime Full Stack predates `preview.2` because this slice changes release engineering only; no runtime behavior changed.

See `docs/versioning.md` and root `CHANGELOG.md`.

## Usage retention operational note

Raw request metrics default to 90 days; daily historical usage rollups default to 730 days. Retention compacts complete expired UTC days before deleting raw rows. Reporting then merges rollups with newer raw metrics.

Read `docs/data-retention.md` before changing retention or historical reporting behavior.

## Administrative audit trail

Administrative changes are stored in `audit_events`:

```http
GET /api/admin/audit?take=100
```

Representative audited actions include routing/node/model/deployment changes, connection tests, maintenance drain/resume, credential creation/rotation/revoke, governance changes and manual retention cleanup.

With Entra ID enabled the actor comes from the authenticated principal. Development mode without Entra records the local administrator identity.

### Sensitive-data rule

Audit must never contain raw inference API secrets, Entra client secrets, Cloudflare tokens, prompts, source code, generated code or model responses. Credential audit contains safe metadata such as name/prefix/expiry only.

## Integration-test behavior

Repository CI/Full Stack covers service-root prefixes, health/models probes, routing changes, SSE delivery, health hysteresis, physical capacity, distributed runtime/outbox behavior, caller governance, historical rollups, backup/restore, credential rotation and cross-replica maintenance. Release publication separately proves GHCR digest + SPDX/SLSA attestation verification.