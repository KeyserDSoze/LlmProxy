# Operations: DGX health, safe maintenance and administrative audit

## Health state model

LlmProxy treats DGX health as a state machine rather than a single last-probe boolean. This avoids routing flapping when a runtime has a transient timeout or one successful probe during recovery.

Default values:

```text
Health__IntervalSeconds=10
Health__HealthyAfterSuccesses=2
Health__UnhealthyAfterFailures=3
```

The equivalent Docker environment variables are:

```text
HEALTH_INTERVAL_SECONDS=10
HEALTH_HEALTHY_AFTER_SUCCESSES=2
HEALTH_UNHEALTHY_AFTER_FAILURES=3
```

State transitions are intentionally conservative:

```text
Unknown
  └─ first success/failure -> Degraded

Degraded
  ├─ enough consecutive successes -> Healthy
  └─ enough consecutive failures  -> Unhealthy

Healthy
  └─ first failed probe -> Degraded

Unhealthy
  └─ recovery success streak -> Healthy
```

`Draining` and `Disabled` are administrative states and are not overwritten by background health probes.

For every node PostgreSQL stores current health state, last check/success timestamps, probe latency/error and consecutive success/failure streaks.

`Healthy`, `Degraded`, and `Unknown` nodes remain eligible for ordinary routing subject to capacity rules. `Unhealthy`, `Draining`, and `Disabled` nodes are excluded.

## Safe model/runtime maintenance

Do not use a plain state flip as an upgrade procedure. The supported workflow is the maintenance API:

```http
GET  /api/admin/nodes/{nodeId}/maintenance
POST /api/admin/nodes/{nodeId}/maintenance/drain
POST /api/admin/nodes/{nodeId}/maintenance/resume
```

### 1. Begin maintenance drain

`POST .../maintenance/drain` first establishes a new-admission block, then commits the node to `Draining` and audits `node.maintenance.drain`.

In distributed Redis mode the maintenance marker is checked inside the same atomic capacity-admission Lua used for node/deployment concurrency. This is important: a peer with stale local route state still cannot admit new work after the distributed pre-block is established, and there is no extra standalone Redis lookup on the normal request path.

Possible responses include:

```text
202 Accepted   drain started; existing work may still be active
409 node_disabled
503 maintenance_coordination_unavailable
```

### 2. Observe drain completion

Poll:

```http
GET /api/admin/nodes/{nodeId}/maintenance
```

The payload exposes:

```text
nodeStatus
coordinationAvailable
admissionBlocked
activeRequests
drained
provider
```

`drained=true` means the node is durably `Draining`, new distributed admission is blocked and active coordinated work is zero.

Do not terminate/restart the vLLM runtime before drain completion unless the operator intentionally accepts interruption. Existing streaming work is allowed to finish; LlmProxy does not fail it over after downstream bytes have started.

### 3. Upgrade / restart / replace the runtime

Once drained, perform the external DGX/vLLM/model operation. LlmProxy intentionally does not execute operating-system, container-runtime, model-download or GPU-driver upgrade commands on the DGX. The gateway owns traffic safety and validation around that external operation.

Keep the node in `Draining` while the runtime is unavailable or being warmed locally.

### 4. Resume with validation

Call:

```http
POST /api/admin/nodes/{nodeId}/maintenance/resume
```

Resume is rejected until distributed active work is zero. It then performs, in order:

1. `GET <service-root>/health`;
2. `GET <service-root>/v1/models`;
3. one non-streaming Chat Completions warm-up with `max_tokens=1` for each enabled provider model deployed on the node.

Only after all checks succeed does LlmProxy persist/publish `Healthy` and clear the maintenance admission marker.

Failure semantics:

```text
409 node_not_draining
409 node_still_draining
503 maintenance_coordination_unavailable
503 node_validation_failed
```

A validation failure is audited as `node.maintenance.resume_failed` and leaves the node `Draining`. Successful return is audited as `node.maintenance.resume`.

If durable `Healthy` was committed but clearing the Redis marker temporarily failed, retrying resume repairs the coordination residue without repeating an unsafe state transition.

### Legacy drain endpoint

The historical direct endpoint:

```http
POST /api/admin/nodes/{nodeId}/drain
```

is deprecated as a maintenance entry point and must not be restored as a bypass around the distributed pre-block. New Admin/operator flows use `/maintenance/drain`.

### Validated HA behavior

Full Stack `35063309417` proves:

- a streaming request is already active;
- maintenance is initiated on one gateway;
- another gateway cannot admit new work to the draining node;
- premature resume is rejected while active work remains;
- the existing stream completes rather than being failed over;
- unhealthy runtime validation keeps the node draining;
- runtime recovery plus health/models/warm-up validation succeeds;
- the node safely returns to routing.

## Manual connection test

The Admin UI `Test` action calls:

```http
POST /api/admin/nodes/{nodeId}/test-connection
```

The diagnostic probe derives:

```text
<service-root>/health
<service-root>/v1/models
<service-root>/v1/chat/completions
<service-root>/v1/responses
```

A service root may include hostname/IP, port and an optional prefix, for example:

```text
http://localhost:3450/primopath
http://10.0.0.25:8000
http://10.0.0.25:8000/vllm
https://dgx-01.internal:8443/inference
```

The manual test is diagnostic. Background health monitoring remains responsible for ordinary persisted routing health; maintenance resume has its own stricter validation gate.

## Product/build identity

Operators can inspect the running product identity through:

```http
GET /healthz
GET /api/admin/product
```

and in the Admin UI at:

```text
/admin/releases
```

The current version is `0.1.0-preview.1`. `/api/admin/product` also exposes the release channel, date, optional build revision/build timestamp and versioned patch notes. See `docs/versioning.md` and root `CHANGELOG.md`.

## Administrative audit trail

Administrative changes are written to `audit_events` in PostgreSQL:

```http
GET /api/admin/audit?take=100
```

Each event contains UTC timestamp, actor, action, entity type/identifier, source IP when available and bounded JSON safe metadata.

Representative audited actions include:

```text
routing.update
node.create
node.update
node.test_connection
node.maintenance.drain
node.maintenance.resume_failed
node.maintenance.resume
node.enable
node.disable
model.create
deployment.create
deployment.update
credential.create
credential.rotate
credential.revoke
```

With Entra ID enabled the actor is resolved from the authenticated principal, preferring `preferred_username`/email. Development mode without Entra records `local-admin`.

### Sensitive-data rule

Audit records must never contain raw inference API secrets, Entra client secrets, Cloudflare tokens, prompts, source code, generated code or model responses. Credential audit events contain safe metadata such as name, prefix and expiration only.

## Integration-test behavior

The repository integration/full-stack suites verify service-root prefixes, health/models probes, routing changes, real SSE delivery, health hysteresis, physical capacity admission, distributed runtime/outbox behavior, caller governance, backup/restore, credential rotation and safe cross-replica node maintenance.
