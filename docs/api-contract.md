# API Contract

## Compatibility goal

The public inference API follows the OpenAI-compatible shape required by GitHub Copilot custom model/BYOK integrations. Compatibility is treated as a product contract, not as an implementation detail.

## `GET /v1/models`

Returns logical models currently enabled for clients.

Example:

```json
{
  "object": "list",
  "data": [
    {
      "id": "agic-code-fast",
      "object": "model",
      "created": 0,
      "owned_by": "llmproxy"
    }
  ]
}
```

## `POST /v1/chat/completions`

The gateway accepts an OpenAI-style Chat Completions request. The incoming logical `model` value is rewritten to the provider model configured on the selected deployment before forwarding the request to the selected inference runtime.

Example request:

```json
{
  "model": "agic-code-fast",
  "messages": [
    { "role": "user", "content": "Explain this method" }
  ],
  "stream": true
}
```

## `POST /v1/responses`

The gateway also exposes the OpenAI Responses API path. Routing, authentication, failover, concurrency accounting and request metrics are shared with Chat Completions; only the upstream path differs.

Example request:

```json
{
  "model": "agic-code-fast",
  "input": "Review this method",
  "stream": true
}
```

Responses API fields such as structured `input`, tools, metadata and future compatible properties are passed through to the inference runtime. As with Chat Completions, the public logical model is replaced only for the upstream call.

## System One routed models

System One classifiers are first-class logical models and deployments. They use the same durable model → deployment → node topology, health eligibility, weighted routing, distributed capacity admission, failover and request-rate governance as OpenAI-facing models, while keeping their typed payload contract separate.

`GET /v1/systemone/models` lists enabled logical models whose surface is `SystemOne`. System One models are intentionally excluded from `GET /v1/models`, which remains the OpenAI-compatible model catalog.

### `POST /v1/systemone`

The typed classifier request body is forwarded unchanged. The logical System One model is selected in this order:

1. `X-LlmProxy-Model: <logical-name>`;
2. configured `SystemOne:DefaultModel`;
3. automatic selection when exactly one enabled System One logical model exists.

If more than one System One model is enabled and no model is selected, the gateway returns `400 systemone_model_required`.

Example:

```http
POST /v1/systemone
Authorization: Bearer <gateway-api-key>
X-LlmProxy-Model: systemone-laya
Content-Type: application/json
```

```json
{
  "state": {
    "document": "I was charged twice. Please fix this ASAP."
  },
  "questions": {
    "billing": {
      "type": "noul",
      "instructions": "Is this ticket about billing?"
    }
  }
}
```

The selected deployment may use the node service root or its own deployment-specific runtime service root. LlmProxy replaces the client credential with a deployment-specific encrypted upstream bearer when configured, otherwise with the selected node's encrypted upstream bearer, forwards to `<runtime-root>/v1/systemone`, and can fail over to another eligible deployment on transport errors or upstream 5xx responses before the downstream response starts.

Request-rate governance can be scoped to the System One logical model. Output-token budgets are not applied to System One because its typed classifier response is not token-generation accounting.

### Legacy configuration import

The historical `SYSTEM_ONE_ENABLED`, `SYSTEM_ONE_BASE_ADDRESS`, `SYSTEM_ONE_API_KEY` and timeout variables remain accepted as an upgrade bridge. When enabled and no System One logical model exists yet, startup imports that configuration once into a normal System One model/deployment, encrypting the upstream bearer with the existing upstream-credential key. If a registered node has the same hostname, the deployment is attached to that physical hardware and keeps the legacy runtime root/bearer at deployment scope; otherwise a node is created for compatibility. Afterwards administrators manage placement/routing through **Infrastructure** and **Models & Deployments** instead of a separate global classifier configuration.

Optional import metadata:

```text
SYSTEM_ONE_DEFAULT_MODEL=systemone-default
SYSTEM_ONE_PROVIDER_MODEL_NAME=systemone-classifier
SYSTEM_ONE_NODE_NAME=systemone-classifier
SYSTEM_ONE_MAX_CONCURRENCY=8
```

## Payload preservation

The gateway deliberately does not deserialize inference requests into restrictive endpoint-specific DTOs. It validates only the logical `model`, preserves unknown compatible JSON fields and rewrites that one property before forwarding.

This is important for:

- streaming;
- tool/function calling;
- structured Responses API input;
- metadata;
- future OpenAI-compatible fields that the gateway does not yet know about.

## Routing and failover

Both `POST /v1/chat/completions` and `POST /v1/responses` use the same routing pipeline:

1. validate the bearer credential;
2. resolve the logical model;
3. load eligible deployments;
4. exclude disabled, unhealthy, draining or saturated capacity;
5. select a deployment using weighted least-loaded routing;
6. rewrite the logical model to the deployment provider model;
7. forward the request to the corresponding upstream path;
8. retry another eligible deployment for transport failures or upstream 5xx responses before the downstream response has started;
9. persist metadata-only request metrics.

## Errors

Gateway-generated errors use an OpenAI-like envelope:

```json
{
  "error": {
    "message": "No healthy deployment is available for model 'agic-code-fast'.",
    "type": "gateway_unavailable",
    "code": "no_healthy_deployment"
  }
}
```

## Authentication

Inference endpoints require:

```http
Authorization: Bearer <gateway-api-key>
```

Credentials are database-backed, HMAC-hashed, revocable and may have an expiry date. Administrators can create service credentials; authenticated Entra users with `LlmProxy.User` or `LlmProxy.Admin` can create personal credentials through `/api/me/api-credentials`. Personal ownership is the stable Entra `(tid, oid)` pair. Raw secrets are returned only once at creation or rotation.

## Administration

Administration endpoints live under `/api/admin`. They are not OpenAI-compatible and may evolve independently of `/v1`.

Authenticated user self-service endpoints live under `/api/me`; they expose only the current Entra identity, credentials owned by that identity, usage attributable to those credentials, read-only aggregate user request-limit metadata, and request-audit payloads attributable to the caller's own personal credentials. Administrative user request-limit CRUD lives under `/api/admin/user-rate-limits`; user quota scope is stable Entra `(tid, oid)` with optional logical-model scope.

## Administrator diagnostic and request-audit APIs

These endpoints are control-plane APIs. When Entra is enabled they require `LlmProxy.Admin`; `LlmProxy.Reader` cannot access them.

### Model chat diagnostic

```http
POST /api/admin/testing/chat
Content-Type: application/json
```

```json
{
  "model": "qwen3-coder-next-256k",
  "systemPrompt": "You are a concise assistant.",
  "userPrompt": "Reply with exactly: LlmProxy model test OK",
  "maxTokens": 256,
  "temperature": 0.2
}
```

The diagnostic resolves the logical model through normal routing, acquires the same node/deployment capacity gate used by inference, sends a non-streaming Chat Completions request to the selected runtime, and returns the selected node plus raw upstream body. Caller rate limits are bypassed because this is an administrator diagnostic.

### System One / classifier diagnostic

```http
GET  /api/admin/testing/systemone
POST /api/admin/testing/systemone
```

The GET surface reports routed System One availability, the first eligible runtime endpoint for operator diagnostics, protected upstream-credential presence, available logical model names, default model and timeout. The POST surface accepts an optional logical `model` plus the typed payload:

```json
{
  "model": "systemone-laya",
  "payload": {
    "state": { "document": "duplicate card charge" },
    "questions": {
      "billing": {
        "type": "noul",
        "instructions": "Is this request about billing?"
      }
    }
  }
}
```

The diagnostic resolves the System One logical model through normal routing/capacity admission, reports the selected deployment/node/provider metadata and forwards the payload object as the exact JSON request body. This is suitable for private classifiers such as `convaiinnovations/laya`.

### Full-body request audit

```http
GET /api/admin/content-logs/query
GET /api/admin/content-logs/{id}
GET /api/admin/content-logs/settings
PUT /api/admin/content-logs/settings
POST /api/admin/content-logs/retention/run
```

The Admin query endpoint returns safe metadata with paging/filtering by owner, credential, model, surface, status, request ID and time range. The detail endpoint decrypts and returns the exact captured request and response bodies and correlates routing metrics where available. Retention is administrator-controlled from 10 through 4015 days (11 x 365), default 30 days. Normal-user equivalents are `GET /api/me/content-logs` and `GET /api/me/content-logs/{id}`; they are restricted to rows linked to personal credentials owned by the same authenticated Entra `tid + oid`, and never expose shared/organization credentials. Decrypted responses use `Cache-Control: no-store`.

### Administrator API-key recovery

```http
GET /api/admin/api-credentials/{id}/secret
```

For newly created/rotated credentials, the endpoint decrypts the administrator recovery copy and returns the secret with `Cache-Control: no-store`. Reveal actions are audited without secret material. Pre-feature keys return `409 secret_not_recoverable` until rotated, except a configured bootstrap key that can be backfilled at startup.
