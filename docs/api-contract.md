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
