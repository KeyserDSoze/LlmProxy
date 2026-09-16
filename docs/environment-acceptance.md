# Production environment acceptance

This runbook captures the environment-specific evidence that repository CI cannot prove: the actual Linux host, Docker/Compose, VM-to-DGX connectivity, intended vLLM model surfaces and the deployed LlmProxy gateway.

Use this only after the supported production deployment in `docs/linux-production-deployment.md` is running.

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

A production `.env` should normally provide all required values. Explicit non-secret overrides are available for acceptance against staged or alternate endpoints:

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

## Follow-on acceptance

After the private-LAN environment acceptance is green, continue with the remaining external work:

1. benchmark intended models and apply evidence-backed Capacity Profiles;
2. configure and validate real Entra `LlmProxy.Admin` / `LlmProxy.Reader` roles;
3. configure the intended Cloudflare Tunnel/public hostname;
4. validate GitHub Copilot BYOK through the public gateway;
5. validate the self-hosted GitHub Actions deployment runner;
6. finalize PostgreSQL/Redis/observability HA/storage and backup destination/encryption/retention.

Do not upload real environment evidence into the repository by default. It can contain infrastructure metadata such as host and service-root addresses even though it intentionally excludes prompts, generated output and secrets.
