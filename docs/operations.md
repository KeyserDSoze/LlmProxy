# Operations: DGX health and administrative audit

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

For every node PostgreSQL stores:

- current health state;
- last health check timestamp;
- last successful health timestamp;
- last probe latency in milliseconds;
- last health error (bounded to 1000 characters);
- consecutive successful probes;
- consecutive failed probes.

`Healthy`, `Degraded`, and `Unknown` nodes remain eligible for routing subject to capacity rules. `Unhealthy`, `Draining`, and `Disabled` nodes are excluded.

## Manual connection test

The Admin UI `Test` action calls:

```http
POST /api/admin/nodes/{nodeId}/test-connection
```

The probe validates the complete configured service root and derives:

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

The manual test is diagnostic: the background health monitor remains responsible for the persisted routing health state.

## Administrative audit trail

Administrative changes are written to `audit_events` in PostgreSQL. The current API is:

```http
GET /api/admin/audit?take=100
```

Each event contains:

- UTC timestamp;
- actor;
- action;
- entity type and identifier;
- source IP when available;
- bounded JSON details containing safe operational metadata.

Current audited actions include:

```text
routing.update
node.create
node.update
node.test_connection
node.drain
node.enable
node.disable
model.create
deployment.create
deployment.update
credential.create
credential.revoke
```

With Entra ID enabled the actor is resolved from the authenticated principal, preferring `preferred_username`/email. Development mode without Entra records `local-admin`.

### Sensitive-data rule

Audit records must never contain:

- raw inference API secrets;
- Entra client secrets;
- Cloudflare tokens;
- prompts;
- generated code or model responses.

Credential audit events contain only safe metadata such as credential name, prefix and expiration.

## Integration-test behavior

The Docker integration suite runs two controllable mock inference runtimes. It verifies:

- service roots with path prefixes;
- `/health` and `/v1/models` probes;
- weighted and round-robin routing;
- live routing-policy changes and persistence after restart;
- real SSE delivery without buffering;
- health transition `Healthy -> Degraded -> Unhealthy -> Degraded -> Healthy`;
- persisted health latency/error/streak diagnostics;
- persisted administrative audit events after restart.
