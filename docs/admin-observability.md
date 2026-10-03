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

The Admin UI exposes this as **Request Audit**. It supports server-side paging and filters for user ownership, credential, logical model, surface, status, request ID and time range, with optional two-second live refresh. Selecting a row loads/decrypts the detail and shows request/response bodies, correlated node/deployment/attempt/TTFT/token data when a request metric exists, and copy controls.

Global request-audit APIs require `LlmProxy.Admin`. Configured super admins receive that role through the existing claims transformation. `LlmProxy.Reader` cannot read payload logs. A separate normal-user self-service surface under `/api/me/content-logs*` permits an admitted user to inspect only rows linked to personal credentials owned by the same stable Entra `tid + oid`; other users' rows and organization/shared credentials are excluded.

Headers are not copied into the payload store. In particular client `Authorization` and upstream bearer credentials are never persisted there.

## Content-log retention

Retention is administrator-controlled from 10 through 4015 days (11 x 365 days), default 30. A hosted cleanup worker runs at startup and every four hours. A manual cleanup action is also available from the UI and is audited. Decrypted detail responses use `Cache-Control: no-store`.

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
