# Admin observability, testing and in-app help

## Purpose

The Admin UI is intended to explain the operational behavior of LlmProxy without requiring an operator to read source code first. Every Admin screen has a collapsed-by-default documentation accordion. The dedicated **Help & Endpoints** page contains copy-ready client examples and a request-path explanation.

## Playground

**Playground** is visible only when the current principal has write/admin capability.

### Model test

The model tester lists enabled logical models and calls `POST /api/admin/testing/chat`.

The backend:

1. resolves the logical model through the production routing service;
2. acquires the normal distributed/local capacity gate;
3. rewrites the logical name to the provider model;
4. applies the configured node upstream bearer;
5. sends a non-streaming Chat Completions request;
6. returns selected node/deployment, latency, request body and raw response.

The diagnostic intentionally bypasses client API-key rate limits because it is a control-plane test.

### System One classifier test

The classifier panel displays:

- enabled/disabled state;
- public endpoint `/v1/systemone`;
- configured private upstream endpoint;
- whether upstream bearer auth is configured;
- timeout.

A JSON editor lets an administrator send the exact classifier payload. The default example is compatible with the System One decision shape used for classifiers such as `convaiinnovations/laya`.

## Request audit / full-body content logs

Authenticated requests to:

- `POST /v1/chat/completions`;
- `POST /v1/responses`;
- `POST /v1/systemone`

are captured at the HTTP gateway boundary. Request and response payloads are stored as AES-GCM ciphertext derived from the deployment API-key pepper and bound to request-specific purposes.

For streaming Chat Completions and Responses, Request Audit **does not store SSE token chunks**. The gateway forwards every upstream SSE byte to the client unchanged while an incremental, request-scoped assembler rebuilds the logical JSON response. It handles text, tool/function arguments, multi-choice chat output, Responses output items, finish reasons, usage when the provider supplies it, and the partial response already received when the stream stops. The encrypted response field contains one `llmproxy.audit.stream.v1` JSON envelope (`state`: `completed`, `cancelled`, `interrupted`, `incomplete`; `complete`; `reason`; `eventsProcessed`; `truncated`; `response`). HTTP 200 at the start of an SSE stream does not prove the generation completed. Confirmed upstream failures and lease loss take priority over client cancellation. Non-streaming JSON remains unmodified.

Streaming audit reconstruction is bounded to 2 million captured characters, and non-streaming body capture is bounded to 2 million bytes; a truncated audit indicates this explicitly, without changing what was sent to the client. The Admin and user detail dialog shows a readable reconstructed response or the JSON envelope, plus stream status. This is a **new pre-production format**: old raw-SSE audit records are intentionally not migrated or reconstructed.

The Admin UI exposes this as **Request Audit**. It supports server-side paging and filters for user ownership, credential, logical model, surface, status, request ID and time range, with optional two-second live refresh. Live refresh keeps the existing table height stable: the refresh state is shown as a compact spinner beside the pagination count rather than by inserting/removing a loading table row.

Selecting **Inspect** opens the request detail in a modal instead of expanding the page. The modal shows request/response bodies plus correlated node/deployment/attempt/TTFT/token metadata when a request metric exists. Authorized operators can copy individual payloads or download a complete audit export as JSON or Markdown. Export is performed in the browser from the already-authorized decrypted detail; the server does not create a second plaintext export copy.

Global request-audit APIs require `LlmProxy.Admin`. Configured super admins receive that role through the existing claims transformation. `LlmProxy.Reader` cannot read payload logs. A separate normal-user self-service surface under `/api/me/content-logs*` permits an admitted user to inspect only rows linked to personal credentials owned by the same stable Entra `tid + oid`; other users' rows and organization/shared credentials are excluded. The normal-user detail also uses the same modal/export UX, but no administrator summary controls are exposed.

Headers are not copied into the payload store. In particular client `Authorization` and upstream bearer credentials are never persisted there.

## Administrator AI summaries

Each retained Request Audit entry may have one persisted administrator-only summary. The summary is stored in `request_audit_summaries` as application-encrypted ciphertext and is linked one-to-one to the corresponding `inference_content_logs` row.

The Request Audit page exposes **Summary** next to **Inspect**:

- if no summary exists, clicking the button generates one, saves it, then opens the summary modal;
- if a summary already exists, the saved summary opens without calling an LLM;
- the summary modal can **Regenerate summary**, optionally overriding the model and node for that run;
- regeneration replaces the saved summary for that audit entry, so every administrator sees the latest persisted result.

Summary generation is a control-plane action available only to `LlmProxy.Admin` / `AdminWrite`. It uses the same logical-model deployment catalog, routing service and capacity gate as normal inference. An administrator can configure a default OpenAI-compatible logical model and optionally pin a default node. With no node pin, normal routing chooses a healthy eligible deployment. A manual or default node is accepted only when the selected logical model is deployed there.

The administrator-only summary policy is stored with the content-log settings and includes the editable system prompt, default logical model and optional node. The default prompt treats the original request/response as untrusted data and asks for an extremely short operational summary containing only the **project type** and **work done**, without inventing missing context or exposing credentials. The prompt is never returned through normal-user APIs.

Generated summaries are returned with `Cache-Control: no-store`, and generation/regeneration plus summary-policy changes are recorded in the administrative audit without storing plaintext summary content there.

## Content-log retention

Retention is administrator-controlled from 10 through 4015 days (11 x 365 days), default 30. A hosted cleanup worker runs at startup and every four hours. A manual cleanup action is also available from the UI and is audited. Decrypted detail responses use `Cache-Control: no-store`.

Request-audit summaries do not have an independent longer lifetime: their foreign key uses cascade deletion, so a summary disappears when its parent full-body Request Audit row is deleted by retention.

This is independent from request-metric/usage-rollup retention.

## Recoverable API keys

Request authentication still uses only the HMAC hash. New/rotated API keys additionally store an AES-GCM encrypted recovery value tied to the credential ID.

Admin UI **Reveal / copy** calls the dedicated secret endpoint. Each reveal is audited and returned with `Cache-Control: no-store`.

Credentials created before encrypted recovery cannot be reversed from their HMAC. Rotate them once to make the replacement key recoverable. The configured bootstrap API key is backfilled automatically when its current hash is found during startup.

## Endpoint help

The **Help & Endpoints** screen documents:

- `GET /v1/models`;
- `POST /v1/chat/completions`;
- `POST /v1/responses`;
- `POST /v1/systemone`;
- bearer authentication;
- current logical/provider model aliases;
- classifier forwarding;
- routing, capacity admission, rate limiting, observability and retention.

The contextual accordion on each page describes only that page's controls and the most important operational consequences.
