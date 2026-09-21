# Production environment acceptance

This runbook captures the environment-specific evidence that repository CI cannot prove: the actual Linux host, Docker/Compose, VM-to-DGX connectivity, intended vLLM model surfaces and the deployed LlmProxy gateway.

Use this after the supported production deployment in `docs/linux-production-deployment.md` is running.

## Acceptance command

From a complete repository checkout on the production host:

```bash
sudo -E bash docker/scripts/environment-acceptance.sh
```

By default the script reads `/opt/llmproxy/.env`, probes the locally published gateway and the configured initial DGX/vLLM service root, and writes evidence under:

```text
/opt/llmproxy/acceptance/<UTC timestamp>/
  summary.md
  checks.tsv
```

The evidence directory and files are owner-restricted. Override the location when required:

```bash
sudo -E bash docker/scripts/environment-acceptance.sh \
  --evidence-dir /opt/llmproxy/acceptance/customer-uat-01
```

## GitHub Actions acceptance

After the production host also has the dedicated self-hosted runner used by deployment, the same acceptance can be launched manually through:

```text
.github/workflows/environment-acceptance.yml
```

The workflow uses the existing runner labels:

```text
self-hosted
linux
x64
llmproxy-prod
```

It has no workflow-dispatch inputs for API keys or other secrets. The gateway credential is read by the acceptance script from the protected host-owned `/opt/llmproxy/.env`. If direct DGX bearer authentication is required, use the manual acceptance path with `LLMPROXY_ACCEPTANCE_DGX_API_KEY` in the process environment until an approved host-secret injection mechanism is configured.

The workflow runs the acceptance script with non-interactive `sudo`, copies only the generated metadata evidence to the runner account, uploads only:

```text
summary.md
checks.tsv
```

and removes the runner-local evidence directory afterward. The Actions artifact retention is 14 days. The artifact can still contain infrastructure metadata such as distro/kernel and service-root addresses, so access remains governed by repository/environment permissions.

A workflow run is successful only when the acceptance script exits `0`. Evidence is still uploaded on functional acceptance failure when the metadata files were produced, so operators can inspect the failed checks without persisting prompts, responses or credentials.

## What is validated

Host/runtime checks:

- Linux distribution, kernel and architecture are recorded;
- Docker Engine is reachable;
- Docker Compose v2 is available.

Direct VM -> DGX/vLLM checks:

```text
GET  /health
GET  /v1/models
POST /v1/chat/completions
POST /v1/chat/completions   stream=true
POST /v1/responses
POST /v1/responses          stream=true
```

The provider model configured by `PROVIDER_MODEL_NAME` must be advertised by `/v1/models`.

Canonical vLLM `/health` may return HTTP `200` with an empty response body and no JSON content type. Acceptance therefore treats `/health` as a status-only probe. This is intentional; requiring JSON there would incorrectly reject a healthy standard vLLM server.

Gateway checks:

```text
GET  /healthz
GET  /readyz
GET  /v1/models
POST /v1/chat/completions
POST /v1/chat/completions   stream=true
POST /v1/responses
POST /v1/responses          stream=true
```

The logical alias configured by `PUBLIC_MODEL_NAME` must be advertised by the gateway. Streaming probes require `text/event-stream`.

Each HTTP probe records only:

- PASS/FAIL;
- HTTP status;
- response content type;
- time to first byte;
- total request time;
- a non-content diagnostic reason when a check fails.

## Privacy / credential handling

The acceptance bundle is deliberately metadata-only. It does **not** copy:

- request bodies or prompts;
- source code;
- generated model output;
- response bodies;
- gateway bearer credentials;
- optional DGX bearer credentials.

Synthetic request bodies are written only into a temporary owner-only directory and are deleted when the script exits. Response bodies are also temporary and are deleted rather than copied into evidence.

Gateway secrets are not accepted as command-line flags. The script reads the inference credential from either:

```text
LLMPROXY_ACCEPTANCE_API_KEY
```

or `LLM_PROXY_API_KEY` in the production env file. If the DGX/vLLM runtime itself requires bearer authentication, provide it only through:

```text
LLMPROXY_ACCEPTANCE_DGX_API_KEY
```

The script performs a final guard that rejects an evidence bundle if either secret value appears in the generated evidence files.

## Overrides

A production `.env` should normally provide all required non-secret target values. Explicit overrides are available for staged or alternate endpoints:

```bash
bash docker/scripts/environment-acceptance.sh \
  --env-file /opt/llmproxy/.env \
  --gateway-url http://127.0.0.1:8080 \
  --dgx-url http://10.0.0.21:8000 \
  --public-model agic-code-fast \
  --provider-model '<exact-vllm-model-id>' \
  --evidence-dir /opt/llmproxy/acceptance/manual-01
```

`--skip-docker` exists for controlled test harnesses and should not be used for normal target-host acceptance.

A no-change repository/syntax check is available through:

```bash
bash docker/scripts/environment-acceptance.sh --validate-only
```

## Pass criteria

The environment acceptance command exits successfully only when every enabled check passes. A normal production acceptance therefore requires:

1. working Docker Engine + Compose v2;
2. VM reachability to the configured DGX service root;
3. the exact provider model visible directly from vLLM;
4. direct Chat, Responses and SSE requests working against vLLM;
5. LlmProxy liveness and readiness healthy;
6. the logical public model visible through LlmProxy;
7. Chat, Responses and SSE requests working through LlmProxy.

The resulting evidence proves connectivity and functional surfaces. It does **not** establish production concurrency. Capacity Profiles must still be calibrated with `docs/benchmarking.md` against the real DGX/model combination and representative workload.

## Entra personal-key acceptance

Personal-key acceptance must prove the real tenant identity path rather than only repository mocks:

1. sign in as an assigned `LlmProxy.User`;
2. open `/admin/me` without administrative control-plane access;
3. create a named personal API key and copy the one-time secret;
4. call the intended `/v1/*` inference surface with that key;
5. verify the key is attributed to the correct Entra `tid + oid` in administrator identity inventory and own usage;
6. rotate the key, verify the old secret stops working after runtime propagation, then revoke the replacement and verify it is rejected.

Do not persist the raw acceptance secret in evidence.

## Follow-on acceptance

After the private-LAN environment acceptance is green, continue with the remaining external work:

1. benchmark intended models and apply evidence-backed Capacity Profiles;
2. configure and validate real Entra `LlmProxy.Admin` / `LlmProxy.User` / `LlmProxy.Reader` roles, including `/admin/me` personal-key create/rotate/revoke and a `/v1/*` call using the personal key;
3. configure the intended Cloudflare Tunnel/public hostname;
4. validate GitHub Copilot BYOK through the public gateway;
5. validate deployment and acceptance through the self-hosted GitHub Actions runner;
6. finalize PostgreSQL/Redis/observability HA/storage and backup destination/encryption/retention.

Do not commit real environment evidence into the repository. The GitHub Actions artifact path is intentionally short-lived and metadata-only, but it can still reveal infrastructure metadata and must be treated as operational evidence rather than product source.
