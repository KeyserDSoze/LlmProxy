# Managed catalog: download, startup, and System One default

## Curated install-and-publish flow

In **Admin → Model catalog**, select the Linux Node Agent and one of the curated
Chat, Speech-to-text, Text-to-speech, Images or System One checkpoints.
Review the hardware fit and explicit **Force** acknowledgment when needed.

**Start and publish model automatically after download** is selected by default
for curated entries. The Agent records this choice inside its durable install job:
1. Download/build the serving image and revision-pinned model files.
2. Start the model locally, waiting for the runtime's readiness checks.
3. Mark the durable job completed only when Start returned a running state.
4. Gateway registration (background reconciler or Admin polling) checks that
   **the exact same installation** is still reported as running by that Agent,
   then enables routing. A missing, stopped, or mismatched installation is
   registered stopped instead of silently becoming routable.

The Agent's job survives a disconnected browser; on Agent restart an incomplete
job becomes *interrupted* and can be retried using the same cached weights.
A failed start never claims successful publication. Custom/unverified Hugging
Face models do not support automatic startup; operators must review and start
them explicitly. Clear the checkbox for download-only installations.

As before, a disabled or draining Node cannot be published just because a
container has started. Automatic publication does not bypass rate limits,
node concurrency ceilings, or routing health checks.

## Public endpoints

| Surface | Client API | Model choice |
|---|---|---|
| Chat | `POST /v1/chat/completions` | JSON `model` |
| System One | `POST /v1/systemone` | Administrator-selected default |
| System One (specific) | `POST /v1/systemone/{modelId}` | URL logical alias |
| Image generation | `POST /v1/images/generations` | JSON `model` |
| Speech synthesis | `POST /v1/audio/speech` | JSON `model` |
| Speech transcription | `POST /v1/audio/transcriptions` | Multipart `model` |

All public `/v1` routes require an LLMProxy bearer API key.
The Admin installed-model list shows the public route and logical model.

## Configuring the System One default

In **Admin → System One**, select the default enabled classifier or choose
**Automatic**, then save. This choice is persisted in PostgreSQL, audited, and
shared by all Gateway replicas. It does not depend on a specific browser session.

Requests select the model in this order:
1. Explicit `/v1/systemone/{modelId}` URL or `X-LlmProxy-Model` header.
   When both exist, conflicting names return HTTP 400.
2. The persisted Admin default, if it is still an enabled model.
3. The optional `SystemOne:DefaultModel` configuration fallback, if enabled.
4. The curated `laya-english-cpu` alias, if enabled, otherwise the first
   enabled alias by ordinal alphabetical order.

An unavailable saved choice is *not* automatically deleted; the Gateway
resolves a safe available fallback. A specific unknown alias returns HTTP 404.
An empty System One catalog returns HTTP 503.

`GET /api/admin/systemone/default` returns the configured and resolved
defaults and the available model names. `PUT /api/admin/systemone/default`
with `{"model":"my-model"}` sets the preference. Use `{"model":null}`
to restore automatic mode; write requires AdminWrite when Entra ID is enabled.

The System One diagnostic endpoint now uses the same resolver as public traffic.

## Acceptance gates

Compilation and unit/e2e checks are **not** proof of successful inference
on the target machine. Laya and Kokoro are experimental CPU profiles; SSD-1B
requires working NVIDIA CUDA and GPU memory, including on GB10 ARM64.
Before a production rollout, exercise real inputs through **the public Gateway
endpoint**, measure startup/inference times and memory consumption, and verify
stop/restart, disabled routes, failure recovery, and cached-weight reuse.
