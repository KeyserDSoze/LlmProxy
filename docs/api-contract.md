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

## `GET /v1/models/details`

Same catalog as `/v1/models`, with what an application needs to use each model well. `/v1/models` is unchanged so OpenAI clients keep working; this is the richer companion.

```json
{
  "object": "list",
  "data": [
    {
      "id": "muse",
      "object": "model",
      "created": 0,
      "owned_by": "llmproxy",
      "llmproxy_surface": "OpenAi",
      "context_window": 131072,
      "max_output_tokens": 8192,
      "input_modalities": ["text", "image"],
      "output_modalities": ["text"],
      "endpoints": ["/v1/chat/completions", "/v1/responses"],
      "capabilities": { "streaming": true, "tools": true, "vision": true, "reasoning": true }
    }
  ]
}
```

- `context_window` is the longest prompt plus answer one request can use on this deployment (for llama.cpp, the shared pool divided by the parallel slots). `max_output_tokens` is the largest completion to ask for. Both are `null` when not declared.
- Values are **declared by the administrator** (suggested from the curated catalog and the model's Hugging Face metadata, then reviewed). They describe the model; the gateway never blocks or rewrites a request because of them. Capabilities are `false` for non-chat surfaces.
- `type` is the kind of model: `chat`, `embedding`, `image`, `speech`, `transcription` or `realtime`. `GET /v1/models/details?type=embedding` returns only that kind.
- Authentication is the same API key as the rest of `/v1`. The response carries an `ETag` and `Cache-Control: private, max-age=30`; send `If-None-Match` to get `304`.
- Administrators edit the values at install (Models → Install) or later with `PUT /api/admin/models/{id}/properties` (`AdminWrite`; body `supportsStreaming`, `supportsTools`, `supportsVision`, `supportsReasoning`, `contextWindowTokens`, `maxOutputTokens`; output may not exceed context).

## One endpoint per kind of model

Every kind of model has its own workspace in Admin and its own OpenAI-style endpoint. The `model` field is always the public (logical) name, which is routed only to deployments of the same kind: a chat alias never answers `/v1/embeddings`.

| Kind (`type`) | Endpoint | Notes |
| --- | --- | --- |
| Text (`chat`) | `POST /v1/chat/completions`, `POST /v1/responses` | SSE streaming, tools, vision, reasoning as declared on the model |
| Images (`image`) | `POST /v1/images/generations` | `b64_json` PNG |
| Text to speech (`speech`) | `POST /v1/audio/speech` | |
| Speech to text (`transcription`) | `POST /v1/audio/transcriptions`, `POST /v1/audio/translations` | multipart upload, 25 MiB |
| Embeddings (`embedding`) | `POST /v1/embeddings` | see below |
| Voice, streaming (`realtime`) | `GET /v1/realtime?model=<name>` | WebSocket, see below |
| System One | `POST /v1/systemone[/{model}]` | typed classifier |

### `POST /v1/embeddings`

OpenAI-compatible: `{"model": "bge-m3", "input": "text"}`. `input` is a string, an array of non-empty strings, an array of token ids or an array of token-id arrays (at most 2048 items); `encoding_format` may be `float` or `base64`. The body may be up to 4 MiB; a larger or malformed request answers `400 invalid_embedding_input` / `413`. The runtime (llama.cpp with `--embedding`) returns the vectors and `usage`. Request bodies are not kept in the content log (metadata only), like the other non-chat endpoints. Errors use the same routing codes as chat (`model_not_found`, `model_not_running`, `capacity_exhausted`).

### `GET /v1/realtime?model=<name>` (streaming voice)

An OpenAI-Realtime-style WebSocket. The gateway authenticates the API key (header `Authorization: Bearer ...`), applies the caller's rate limits and the deployment's capacity, selects a deployment of a model of kind `realtime`, connects to the runtime's `/v1/realtime` and relays frames in both directions without interpreting them (audio and JSON events pass through unchanged). Errors before the upgrade are JSON: `426 websocket_required`, `400 model_required`, `404`/`503` routing errors, `429`. **Only runtimes the gateway reaches directly are served**: the server Agent relay carries HTTP, not WebSocket, so a model hosted behind an Agent answers `501 realtime_not_supported_through_agent_relay`. No runtime is bundled yet; publish a model of this type under *Voice (streaming) → Publishing* and point it at a runtime that implements the protocol.

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

Routing failures are told apart so a client can react correctly. Messages name only the logical model the caller asked for; servers, addresses and provider identifiers never appear in a public response (they are in the internal log and in the Admin request metrics).

| Situation | Status | `code` |
| --- | --- | --- |
| No enabled logical model with that name for this endpoint (typo, disabled or wrong surface) | `404` | `model_not_found` |
| The model exists but no deployment is routable (stopped, starting, not yet activated) | `503` + `Retry-After: 30` | `model_not_running` |
| Deployments exist but none is operational (unhealthy or draining server) | `503` | `no_healthy_deployment` |
| Physical capacity saturated | `429` + `Retry-After: 1` | `capacity_exhausted` |
| Model name longer than 160 characters | `400` | `model_name_too_long` |
| `n` greater than 1 while an output-token budget applies | `400` | `unsupported_n` |
| Inference node sent no response headers within the limit | `504` | `upstream_timeout` |
| JSON body larger than `Gateway:MaxRequestBodyBytes` (default 16 MiB) on chat, responses or System One | `413` | `request_too_large` |

`Gateway:UpstreamResponseHeaderTimeoutSeconds` (default 900) and `Gateway:UpstreamIdleTimeoutSeconds` (default 300, silence between streamed chunks) bound a stalled node; values outside 5 to 86400 seconds fall back to the default. A timeout before the first byte fails over to another deployment; after the stream has started the connection is aborted, never retried. A request path with a trailing slash (`/v1/chat/completions/`) is treated exactly like the path without it by every governance layer.

### Admin: background operations

Stop, force stop, uninstall, capability verification and host preparation do not hold the HTTP request. They answer `202 Accepted` with an operation and finish on the gateway:

| Request (`AdminWrite`) | Operation `kind` |
| --- | --- |
| `POST /api/admin/model-management/deployments/{id}/stop-jobs` | `model.stop` |
| `POST .../deployments/{id}/force-stop-jobs` | `model.force_stop` |
| `DELETE`-equivalent `POST .../deployments/{id}/remove-jobs` | `model.remove` |
| `POST .../deployments/{id}/verify-jobs` | `model.verify` (result in `data`) |
| `POST .../nodes/{id}/prepare-jobs` | `node.prepare` |

`GET /api/admin/operations` and `GET /api/admin/operations/{id}` (`AdminRead`) return `status` (`Running`, `Succeeded`, `Failed`), `detail`, `error` (the Agent's own message) and timestamps. A second operation on the same deployment while one runs answers `409 operation_in_progress`. State is kept in memory per replica (last 100); each outcome is also in the audit log. The synchronous `stop`, `force-stop`, `DELETE deployments/{id}`, `verify` and `prepare` endpoints remain for scripts.

Admin and self-service routes (`/api/admin`, `/api/me`) report an upstream `502`/`504` as `424` with `X-LlmProxy-Original-Status`, because Cloudflare would replace the body of a `502`/`504` with an HTML page. `/v1` keeps its documented statuses.

### Admin: verify a running model

`POST /api/admin/model-management/deployments/{id}/verify` (`AdminWrite`) sends a few tiny requests directly to the runtime and returns `{ deploymentId, model, checkedAtUtc, durationMilliseconds, checks: [{ id, status, detail }] }` with `id` in `chat`, `reasoning`, `streaming`, `tools` and `status` in `ok`, `warning`, `failed`, `inconclusive`. A stopped model answers `409 runtime_not_running`; a model on another surface answers `400 probe_only_for_chat_models`. Details: `docs/model-profiles.md`.

## Authentication

Inference endpoints require:

```http
Authorization: Bearer <gateway-api-key>
```

Credentials are database-backed, HMAC-hashed, revocable and may have an expiry date. Administrators can create service credentials; authenticated Entra users with `LlmProxy.User` or `LlmProxy.Admin` can create personal credentials through `/api/me/api-credentials`. Personal ownership is the stable Entra `(tid, oid)` pair. Raw secrets are returned only once at creation or rotation.

## Browser access (CORS)

Web apps may call the `/v1` endpoints directly from the browser when their origin is allowed. Two lists exist and are combined:

| List | Scope | Managed by |
| --- | --- | --- |
| Site-wide origins | every API key | administrators: `GET/PUT /api/admin/cors-settings` (`{ "allowedOrigins": [...], "maxAgeSeconds": 600 }`) |
| Key origins | one API key | administrators: `PUT /api/admin/api-credentials/{id}/allowed-origins`, or `allowedOrigins` when creating a key; users for their own keys: `PUT /api/me/api-credentials/{id}/allowed-origins` |

An origin is `scheme://host[:port]` without a path (`https://app.example.com`, `http://localhost:5173`); `*` allows any origin. Entries are normalized (lower case, default ports removed) and validated; at most 50 per list.

- **Preflight** (`OPTIONS` with `Access-Control-Request-Method`) carries no API key, so it is answered when the origin is in the site-wide list or in the list of any *enabled* key. Allowed answers are `204` with `Access-Control-Allow-Origin` (the origin is echoed), `Allow-Methods: GET, POST, OPTIONS`, the requested headers (only well-formed names, else `authorization, content-type`) and `Max-Age`. Anything else is `403 origin_not_allowed` without CORS headers.
- **Actual requests** with an `Origin` header receive `Access-Control-Allow-Origin` when the origin is allowed site-wide or by the authenticated key (or, for an invalid key, by any key, so a browser can read the `401`). `X-LlmProxy-Request-Id`, `X-LlmProxy-Trace-Id` and `Retry-After` are exposed. SSE streams carry the headers too.
- **Enforcement.** A key that lists origins is rejected with `403 origin_not_allowed` when it is used from a browser origin that is neither in its own list nor site-wide. A key with no list is not restricted (CORS headers only follow the site-wide list).
- **Scope.** Requests without an `Origin` header (servers, curl) are never affected. CORS limits which web pages can *use* a key in a browser; it does not make a key safe to publish.

Site-wide changes are persisted and picked up by every replica within about 20 seconds; per-key changes propagate through the normal credential runtime-state path. Changes are audited (`cors.settings.update`, `credential.origins.update`) with origins only.

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
