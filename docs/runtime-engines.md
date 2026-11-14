# Runtime engines

An **engine** is the program that runs a model on a server: llama.cpp, vLLM, SGLang, AirLLM, faster-whisper, Kokoro, Diffusers, Laya. On a node each one is a Docker image. Admin → **Servers & hardware → Engines** shows, per server, which engines are installed, at which version, what uses them, and lets an administrator install, update, uninstall and schedule automatic updates.

Model weights are separate from engines (Hugging Face cache, GGUF store). Installing, updating or removing an engine never touches downloaded model files.

## Engines

| Id | Source | Image (default) | Serves |
|---|---|---|---|
| `vllm` | pulled | `vllm/vllm-openai:latest` | Text (chat) |
| `llama.cpp` | pulled | `ghcr.io/ggml-org/llama.cpp:server-cuda` | Text (chat), Embeddings |
| `sglang` | pulled | `lmsysorg/sglang:latest` | Text (chat) |
| `airllm` | built | `llmproxy-airllm:0.3` | Text (chat), experimental |
| `faster-whisper` | built | `llmproxy-faster-whisper:0.1` | Speech to text |
| `kokoro` | built | `llmproxy-kokoro:0.1` | Text to speech |
| `diffusers` | built | `llmproxy-diffusers-ssd1b:0.1` | Images |
| `laya` | built | `llmproxy-laya:0.1` | System One |

*Pulled* engines come from a registry (`docker pull`). *Built* engines are adapters shipped inside the Agent release and built on the server (`docker build --pull`, ARM64 recipe where one exists). The image names follow the Agent options (`DockerImage`, `LlamaCppDockerImage`, `SglangDockerImage`); the inventory resolves them with the same function the model runtime uses, so "installed" cannot disagree with what a model would start.

## What the page shows

- **Status**: Installed / Not installed / Installing / Updating.
- **Version**: the OCI `org.opencontainers.image.version` label if the image has one (a repository path or a long commit hash in it is shortened), otherwise its tag; plus the short image id, an optional revision label, creation date and size. A moving tag such as `latest` or `server-cuda` is shown as the tag; the image id and date are what tell two pulls apart.
- **Used by**: running containers started from the image and installed models that use the engine.
- **Updates**: filled in by *Check for updates*. For a pulled image the Agent asks the registry for the digest the tag points to (HEAD on the manifest with the anonymous token flow, the same request Docker makes; no Docker plugin, no download) and compares it with the digests the image was pulled with: *Update available*, *Up to date*, or *Cannot tell* (private repository, no internet from the server, or an image that was not pulled from a registry). Built engines have no registry; *Update* rebuilds them with fresh base layers.
- **Actions**: Install, Update, Uninstall (refused while a running model uses the engine), Cancel for a running job.

Install and Update are **jobs on the Agent** (persisted in `engine-jobs.json`): closing the browser or losing the tunnel does not cancel them, progress is shown in the row, and the Agent runs one engine operation at a time (a second request answers `409`). A job interrupted by an Agent restart is marked `interrupted`; run it again.

A running model keeps the image it started from; a new image applies when it restarts. The Update dialog (shown when running models use the engine) and each automatic-update policy offer **Restart the running models afterwards**, which restarts them one at a time with routing taken off first (see `docs/model-bootstrap.md`). Uninstalling an engine used by stopped models is allowed; those models set the engine up again when they start.

**Activity panel.** Engine installs and updates appear in the pop-up at the bottom right on every page, like downloads and model starts, so you can move around Admin and return to the engines of that server with one click (`/admin/infrastructure?section=engines&node=<id>`). The gateway-side restart of the models shows as *Restart models · <engine>*.

## Automatic updates

*Auto-update…* per engine and per server stores a daily wall-clock time and an IANA time zone (default: the browser's) in PostgreSQL (`runtime_engine_policies`). The gateway worker `RuntimeEngineUpdateWorker`, every minute:

1. finds policies whose latest slot has passed, was not already run, and is inside the 12-hour catch-up window (a gateway restart or an unreachable server is retried every 15 minutes inside that window, then waits for the next day);
2. claims the policy with a single conditional `UPDATE` so only one gateway replica acts;
3. asks the Agent to update the engine (skipped if the engine is not installed: automatic updates keep installed engines current, they never install one), waits for the job (up to 90 minutes) and records `updated`, `up_to_date`, `skipped`, `failed` or `waiting` with a message, shown in the row and written to the audit log (`engine.auto-update`, actor `scheduler`).

The time follows daylight saving; a wall-clock time skipped by the spring change runs one hour later. Changing the time or enabling the policy clears the "already ran" marker, so a new time chosen today still runs today if it is still ahead.

With **Restart the running models after an update** on (the default), a scheduled update that really changed the image is followed by a one-at-a-time restart of the running models that use the engine; the result is in the policy's *Last* line and in the audit log. With it off, nothing is restarted.

## API

Admin API, `AdminRead` for reads and `AdminWrite` for the rest:

```http
GET    /api/admin/engines/nodes/{nodeId}                      inventory + policies + jobs
POST   /api/admin/engines/nodes/{nodeId}/check-updates
POST   /api/admin/engines/nodes/{nodeId}/{engineId}/jobs      {"action":"install"|"update","restartModels":false}  -> 202
DELETE /api/admin/engines/nodes/{nodeId}/jobs/{jobId}         cancel
DELETE /api/admin/engines/nodes/{nodeId}/{engineId}           uninstall (409 while in use)
GET    /api/admin/engines/nodes/{nodeId}/jobs                 only the Agent's engine jobs (what the activity panel polls)
PUT    /api/admin/engines/nodes/{nodeId}/{engineId}/policy    {"autoUpdate":true,"updateAtLocal":"03:30","timeZone":"Europe/Rome","restartModels":true}
```

Agent API (management port, bearer token; paths are on the relay allow-list for outbound Agents):

```http
GET    /v1/runtime-engines
POST   /v1/runtime-engines/check-updates
GET    /v1/runtime-engines/jobs
POST   /v1/runtime-engines/{engineId}/jobs
DELETE /v1/runtime-engines/jobs/{jobId}
DELETE /v1/runtime-engines/{engineId}
```

## Requirements and limits

- The Agent must be a release that includes these endpoints. An older Agent answers 404 and the page says "Upgrade the Agent first".
- Update checks need the server to reach the registry; pulls need it too. Private registries are reported as *Cannot tell* (no stored registry credentials).
- The llama.cpp *build number* (`llama-server --version`) is not read; use the tag, image id and date.
- Validated against real Docker and a real registry on a development host (install job with progress, concurrent job refused, up-to-date, update available, update that changed the image, remove, unknown engine, bad action, missing token); the target ARM64/GB10 server is still **EXTERNAL**.
