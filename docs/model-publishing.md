# Publishing: registered names, running processes and API activation

Publishing is configuration, not public model discovery. In each model-kind workspace, **Publishing** always lists every registered logical name of that kind, including stopped, failed and unrouted installations. Public `/v1/models`, `/v1/models/details`, `/v1/modalities` and Playground remain active-only.

## Operator flow

1. Install a model, then **Start**. The Agent owns the long start job; closing Admin does not cancel it. The gateway records routing intent before sending the request and completes activation in the background after the matching start and `/health` succeed.
2. A running installation has **Check health & activate** when routing is off. This does not reinstall it and never enables a stopped/unhealthy runtime.
3. **Publish / rename model**, also linked from an Installed card, opens a selector of registered models with their server and process state. The current public name is prefilled; the provider is read-only. Text, images, TTS, STT, embeddings, realtime and System One stay separated in every selector.
4. Keep the name or change it. Optional health/activation applies only to running Agent-managed installations. **Connect external runtime** is the advanced path for a runtime not installed by the gateway, with the workspace's kind fixed.

**Running** describes the Agent-observed process. **API on/off** describes deployment routing; **Activation pending** means routing is wanted but readiness is not established. Missing installations, runtime errors and unreachable Agents are distinct states, not silently labelled stopped. Enabled model/server and maintenance state still determine final public availability.

## Rename contract

`PUT /api/admin/models/{id}/name` (`AdminWrite`, body `{"publicName":"new-name"}`) renames the existing `ModelDefinition`. Model/deployment IDs, files, process, provider and capabilities remain unchanged. Names must be 1–160 characters without control characters. Duplicate names or conflicting policies return 409 without a partial database save. Audit action: `model.rename`.

Credential, Entra-user and Usage Group model-specific policies move with their existing IDs, preserving request/token counters. The content-summary default follows the name. Historical metrics, rollups and encrypted content keep the name used at request time. **Clients must switch to the new public name.** A replayed Agent installation job cannot restore the old name or undo a routing disable.

`GET /api/admin/model-publications/runtime-status` (`AdminRead`) returns deployment/node IDs and observed Agent status. It does not enable routing.

## Recovery and upgrade

Migration `20261115080000_AddRoutingIntent` separates `RoutingDesired` (operator intent) from `Enabled` (routing readiness), and adds `ActivationStartJobId`. Existing deployments backfill intent from `Enabled`; already-disabled installations are deliberately not auto-enabled.

`ManagedRoutingRecoveryWorker` periodically observes registered Agent installations and probes their individual `/health`. It suspends routes for stopped/unhealthy/unobservable installations without clearing intent; restores them after verified recovery; skips disabled/draining servers; and never undoes an explicit deployment disable or Stop. Optimistic concurrency tokens on intent/job identity prevent stale readiness from overwriting newer operator actions. Observation is periodic, not instantaneous.

Intent and a caller-generated start-job ID commit **before** contacting the Agent. Only that successful job can pass the activation barrier; a historical success cannot. After an interrupted/failed start, a newer successful guarded bootstrap job on the same Agent can complete recovery. No gateway/Agent wall-clock comparison is required. If the required job has aged out of the Agent's retained history, recovery stays off: use **Check health & activate** explicitly.

**Upgrade the Node Agent with the gateway.** It must accept `{"jobId":"<uuid>"}` on `/v1/models/{installationId}/start-jobs` and `/restart-jobs` (idempotent for the same ID/installation/action). An older Agent cannot provide durable job correlation; routing stays off with an upgrade message. Agent restarts preserve desired-running state; operator cancellation/Stop clears it.

Capacity changes (`docs/model-capacity.md`) use the same Agent restart job with an attached `profile`; the activation barrier, drain and recovery rules are the same, and a job the Agent rolled back (`rolledBack`) counts as recovered because the model runs again with its earlier limits. The published context follows the runtime only on request.

Engine updates use the Agent-owned restart job so its stop/start gap does not erase desired-running state. The gateway drains requests on its local replica first and refuses to restart if its 90-second drain times out. **Distributed multi-replica restart drain and a durable whole-engine restart queue remain open**: an accepted individual start survives a gateway restart, but later models in the sequence are not automatically resumed. See `docs/model-bootstrap.md`.

Real Spark GPU/ARM64 reboot and engine-update acceptance requires an upgraded Agent and remains external validation; local mock, Docker and PostgreSQL evidence does not replace it.
