# Model bootstrap: models and engines come back by themselves

A model the operator started is expected to be running. After a reboot of the server, a power cut or a crash it must come back without anyone opening Admin or sending a first request.

## Why they did not come back

GPU containers (vLLM, SGLang, AirLLM, Diffusers and llama.cpp on GPU) are started with `--restart no`. That was deliberate: Docker restarting a heavy GPU container in a loop after a thermal or power incident can make things worse. CPU runtimes (Whisper, Kokoro, Laya, llama.cpp on CPU) use `unless-stopped` and already returned after a reboot. The result for GPU models was a server that was up, a gateway that still routed to it, and requests that failed until somebody started the model by hand.

## What the Agent does now

`ManagedModelBootstrapWorker` (Node Agent) starts the models that should be running. It keeps `--restart no` and takes over the decision.

**What is "should be running".** Each installation records `desiredRunning`: true after a verified start, false after a stop (including force stop and a configuration change). A reboot or a crash leaves it true. Records from an older Agent have no value and count as wanted only if they were running when the Agent was upgraded.

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

While a model is loading, the gateway still routes to it if the server is healthy, so requests to that model fail until it answers. The Agent only starts a model; it does not change routing. Marking a *single deployment* as not ready (instead of the whole server) is on the roadmap.

## After an engine update

An update replaces the image but running models keep the old one. Admin offers **Restart the running models afterwards** (on the Update dialog and on each automatic-update policy, default on for scheduled updates). The gateway then, one model at a time: takes it out of routing, waits up to 90 seconds for its requests in flight, stops it, starts it again (the Agent waits for `/health`) and puts it back. If one does not come back its routing stays off and the others are left running. It only restarts when the update really changed the image. Progress is the *Restart models · <engine>* entry in the activity panel; it is kept on the gateway replica that started it and is lost if the gateway restarts mid-way (routing for that model is then still off, which is the safe state).

See also `docs/runtime-engines.md` and `docs/operations.md`.
