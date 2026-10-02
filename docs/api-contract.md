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

## `POST /v1/systemone`

LlmProxy can optionally expose a Jev-compatible System One decision/classification surface backed by a private classifier such as Laya. This endpoint is intentionally separate from the OpenAI-compatible model surfaces because System One returns typed decisions rather than generated text.

When `SystemOne:Enabled=true`, the gateway forwards the request body transparently to `<SystemOne:BaseAddress>/v1/systemone`, replaces the client Authorization header with the configured upstream bearer, and returns the upstream status, content type and response body unchanged.

Example request:

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

The endpoint uses the same LlmProxy bearer API credentials as the other `/v1` surfaces. It does not advertise the classifier through `GET /v1/models`, and it does not treat the classifier as an OpenAI chat model.

Configuration:

```text
SystemOne__Enabled=true
SystemOne__BaseAddress=http://host.docker.internal:8090
SystemOne__ApiKey=<private-classifier-bearer>
SystemOne__TimeoutSeconds=30
```

Request-rate/model-token governance remains specific to the generative model surfaces in this release; `/v1/systemone` receives the common API-key authentication and credential-usage tracking provided for `/v1` requests.

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

Authenticated user self-service endpoints live under `/api/me`; they expose only the current Entra identity, credentials owned by that identity, usage attributable to those credentials and read-only aggregate user request-limit metadata. Administrative user request-limit CRUD lives under `/api/admin/user-rate-limits`; user quota scope is stable Entra `(tid, oid)` with optional logical-model scope.

## Administrator diagnostic and content-log APIs

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

The GET surface reports whether System One is enabled, the configured public/upstream endpoint, upstream bearer presence and timeout. The POST surface accepts:

```json
{
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

The payload object is forwarded as the exact JSON request body to the configured private System One endpoint. This is suitable for deployments hosting classifiers such as `convaiinnovations/laya`.

### Full-body log inspection

```http
GET /api/admin/content-logs?take=100
GET /api/admin/content-logs/{id}
```

The list endpoint returns safe metadata only. The detail endpoint decrypts and returns the exact captured request and response bodies and correlates OpenAI requests with routing metrics where available. Decrypted responses use `Cache-Control: no-store`.

### Administrator API-key recovery

```http
GET /api/admin/api-credentials/{id}/secret
```

For newly created/rotated credentials, the endpoint decrypts the administrator recovery copy and returns the secret with `Cache-Control: no-store`. Reveal actions are audited without secret material. Pre-feature keys return `409 secret_not_recoverable` until rotated, except a configured bootstrap key that can be backfilled at startup.
