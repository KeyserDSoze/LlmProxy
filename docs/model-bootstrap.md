# Model bootstrap: models and engines come back by themselves

A model the operator started is expected to be running. After a reboot of the server, a power cut or a crash it must come back without anyone opening Admin or sending a first request.

## Why they did not come back

GPU containers (vLLM, SGLang, AirLLM, Diffusers and llama.cpp on GPU) are started with `--restart no`. That was deliberate: Docker restarting a heavy GPU container in a loop after a thermal or power incident can make things worse. CPU runtimes (Whisper, Kokoro, Laya, llama.cpp on CPU) use `unless-stopped` and already returned after a reboot. The result for GPU models was a server that was up, a gateway that still routed to it, and requests that failed until somebody started the model by hand.

## What the Agent does now

`ManagedModelBootstrapWorker` (Node Agent) starts the models that should be running. It keeps `--restart no` and takes over the decision.

**What is "should be running".** Each installation records `desiredRunning`: true when a start job begins (and after a verified start), false after an operator cancellation or stop (including force stop and a configuration change). An intentional engine restart preserves it across the stop/start gap. A reboot or a crash leaves it true. Records from an older Agent have no value and count as wanted only if they were running when the Agent was upgraded.

**Every 15 seconds, after a short delay at Agent start (`NodeAgent:BootstrapDelaySeconds`, default 20):**

1. Settle the attempts already in flight.
2. If a model start is already running (an operator's or the Agent's own), wait: one start at a time, so two models never compete for GPU memory.
3. Pick the next wanted model that is not running, in port order, and check the environment first. These waits are **not** counted as failures and are retried every 30 seconds:
   - Docker is not answering yet;
   - the GPU is above `GpuStartMaxTemperatureCelsius` (the same safety gate every start uses);
   - the NVIDIA driver is not up yet (for the first five minutes after the Agent started).
4. Make sure the **engine** is there. A missing image is installed as a normal engine job (visible in Admin) before the start is tried.
5. Enqueue a normal start job. It shows in Admin → Jobs and in the activity panel like any start.

**Failures.** A failed start is retried after 1, 2, 5, 10 and 15 minutes. After 5 failed starts the Agent stops, keeps the error, and the model card says *Automatic restart stopped*. A model that starts and then dies again within 10 minutes keeps its count, so a crash loop (for example out of memory at the first request) ends instead of repeating forever. A start by hand, or a stop, resets everything.

**What the operator sees.** `GET /v1/models` on the Agent adds `desiredRunning`, `autoStart` (`waiting`, `starting`, `retrying`, `gave_up`) and `autoStartDetail`. The model card shows it, and a model the Agent is bringing back is listed under *Installed* even though it is not running yet.

**Switching it off.** `NodeAgent:BootstrapModels=false` restores the old behaviour (no automatic start).

**What it does not do.** It never starts a model the operator stopped, never starts two at once, never starts on a hot GPU, and never installs anything the operator did not already choose (an engine is only restored for a model that was installed with it).

## What routing does meanwhile

The gateway's `ManagedRoutingRecoveryWorker` observes each managed installation and its `/health`, suspending its routing while it is stopped/unready and restoring it from durable `RoutingDesired` after recovery. An intentional start/restart waits for the matching `ActivationStartJobId`, not an old start or the browser. Explicit Stop/disable clears intent and is never automatically undone. Detection is periodic, so there is still a detection window after an unexpected process crash. See `docs/model-publishing.md` for the contract and upgrade requirements.

## After a capacity change

A change of context or parallel requests (Installed → Capacity, `docs/model-capacity.md`) is an Agent restart job that carries the new limits. The Agent keeps the earlier limits aside and restores them if the new ones do not start, so a model is not left down by a value that does not fit. If the Agent restarts mid-job, the new limits stay saved and this bootstrap retries the model (it does not roll back); after five failed attempts it gives up like for any other model.

## After an engine update

An update replaces the image but running models keep the old one. Admin offers **Restart the running models afterwards** (on the Update dialog and on each automatic-update policy, default on for scheduled updates). The gateway then, one model at a time: suspends routing while preserving intent, waits up to 90 seconds for its local in-flight requests, sends one persistent Agent `/restart-jobs` operation and restores routing after readiness. A drain timeout refuses the restart instead of interrupting requests. If one does not come back its routing stays off and the sequence stops; guarded bootstrap/recovery may retry it. It only restarts when the update really changed the image.

Progress is the *Restart models · <engine>* entry in the activity panel. The whole sequence is still gateway-replica-local: a gateway restart loses its progress and does not enqueue the remaining models. An already accepted individual Agent restart continues and its routing is recovered without the browser. A gateway interruption before the Agent accepts it leaves the model safely unrouted until a new Start or explicit health/activate. **Multi-replica distributed drain remains open**; this drain only counts the local replica's active requests.

See also `docs/runtime-engines.md` and `docs/operations.md`.
