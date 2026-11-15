# Model capacity: context, parallel requests and engine limits

Admin → *Models & inference* → **Installed** → **Capacity** (on every installed model card). It changes what a model can hold at once, on the spot, for every engine that has such a setting. Weights stay on disk; only the runtime restarts.

## What the numbers mean

- **Context window** is **prompt + answer** together. There is no separate "input limit" and "output limit" in the engines: a request whose prompt alone is larger than the context is refused by the engine itself (llama.cpp: `exceed_context_size_error`, `n_prompt_tokens` > `n_ctx`). To accept longer prompts, raise the context. How long an answer may be is chosen by each request (`max_tokens`) inside the same window; the *Max output* shown under *Published to clients* is a declaration, not a limit the gateway enforces.
- **llama.cpp** serves one context *pool* that its parallel slots share (`--ctx-size`, `--parallel`): with 65,536 tokens and 4 slots, a request can count on about 16,384 when every slot is busy. The dialog and the card show that figure. vLLM, SGLang and AirLLM give each request the whole context.
- **Parallel requests** (`--parallel`, `--max-num-seqs`, `--max-running-requests`, AirLLM queue) are the slots the engine runs at once.
- A larger context needs more memory (KV cache) and can slow requests down. The product does **not** estimate KV-cache size: raise the context step by step and watch free memory in *Servers & hardware*. The ceiling in the dropdown is the model's own limit when the catalog knows it, otherwise the Agent's 256K policy ceiling, which is not proof that the checkpoint supports it.
- The gateway does not count or limit tokens on requests (no tokenizer/estimation semantics exist); the engine enforces the context.

## Per engine

| Engine | Context | Parallel | Also |
|---|---|---|---|
| llama.cpp | `--ctx-size` (pool) | `--parallel` | Embedding models: the physical batch (`--batch-size/--ubatch-size`) is recomputed from the per-slot context, as at install |
| vLLM | `--max-model-len` | `--max-num-seqs` | GPU memory fraction `--gpu-memory-utilization` (0.50–0.95; 0.70 is preselected when never set) |
| SGLang | `--context-length` | `--max-running-requests` | |
| AirLLM | `AIRLLM_MAX_CONTEXT` | queue length | Maximum answer length (the only engine with an output cap) |
| Whisper, Kokoro, Laya, Diffusers | fixed profile | fixed | The button is disabled: there is nothing to change |

Every value is a dropdown (usual steps up to the model's ceiling, the current value even if unusual, and **Custom…** for any number). Values outside every engine's range are refused in the form and by the API before anything is touched.

## What "Apply" does

| Model is | Buttons | Result |
|---|---|---|
| running | **Apply and restart** | restarts with the new limits; there is no "stop it first" step |
| stopped | **Apply and start** / **Save limits** | starts with them, or keeps them for the next start |
| starting | disabled | wait until it is up or has failed |

For a **running** model the gateway runs one background operation (shown in the bottom-right activity dock, with a link back):

1. If routing was wanted, it is switched off (`RoutingDesired` is kept, `ActivationStartJobId` records the job it waits for) and requests in flight on this gateway replica get up to **90 seconds** to finish. If they do not, nothing is touched and routing is given back.
2. It asks the Agent for one persistent job, `POST /v1/models/{id}/restart-jobs {jobId, profile}`. The Agent validates the profile **before** stopping anything, stops the container, saves the new limits, starts it and waits for `/health`.
3. **If the new limits do not start** (typically out of memory) the Agent puts the previous limits back, starts the model again and marks the job `failed` with `rolledBack: true`. The gateway reports *"the new limits did not fit and were not applied (cause). The previous limits were restored and the model is serving again"* and gives routing back. If even the previous limits do not start, the limits are still restored in the Agent's registry, the model stays wanted and the guarded bootstrap retries it; routing stays off.
4. Only when the job it asked for has completed does routing return. A model the operator had switched off stays off after the restart.

A stopped model uses `start-jobs` with the same `profile`. An Agent refusal (HTTP error answer) restores the routing state it had; a lost connection leaves the barrier for the routing recovery worker (`docs/model-publishing.md`).

## Context advertised to clients

*Also tell clients the new window* changes the context read on `GET /v1/models/details` to what the runtime now serves per request (and lowers the declared maximum output if it no longer fits). It is on by default only when the advertised value followed the runtime before and no other server keeps the old size under the same name; it is off when a person set a different number. It changes a declaration only; it never blocks a request. It is applied after the model is verified healthy, and not when the new limits were rolled back.

## Other servers

When the same logical model is installed on other servers, they are listed under *Same model on other servers*. Ticking them applies the same context and parallelism there as separate operations: each server restarts on its own, engine-specific settings (GPU fraction, AirLLM output) are left as they are there, and a **stopped** server is updated but **not started** (`keepStopped`). There is no all-or-nothing: one server can fail or roll back while another succeeds, and the dialog reports which.

## API

```http
PUT /api/admin/model-management/deployments/{id}/configuration     (AdminWrite)
{ "maxModelLen": 65536, "maxNumSeqs": 2, "maxOutputTokens": null, "gpuMemoryUtilization": null,
  "applyNow": true, "keepStopped": false, "syncPublishedContext": true }
```

- `applyNow: false` (default) saves for the next start and answers `200`; a running model answers `409 stop_runtime_first` (the message points to *Apply and restart*).
- `applyNow: true` answers `202` with the operation (`kind: model.limits`, readable at `GET /api/admin/operations`) and audits `model.runtime.configure` with the outcome `applied`, `rolled_back` or `failed`.
- `maxOutputTokens` and `gpuMemoryUtilization` left `null` keep the runtime's current value. Sending the AirLLM output cap to another engine, a GPU fraction to a non-vLLM engine, a context above the catalog ceiling, an AirLLM output larger than the context, or anything for a fixed media runtime answers `400`.
- `409 operation_in_progress` when another operation already runs for that model.

## Agent requirements and limits

- **Upgrade the Agent with the gateway.** `start-jobs` and `restart-jobs` accept `{jobId, profile}` and echo the `profile`; an older Agent that does not is reported as *"too old to apply limits while restarting"* and routing is restored. Saving limits on a stopped model still works with older Agents, but they do not recompute an embedding model's batch.
- If the Agent itself restarts while a job is running, the job becomes `interrupted`, the new limits stay saved, and the model is retried by the guarded bootstrap (1/2/5/10/15 minutes, five attempts). That path does **not** roll back.
- The 90-second drain counts requests of the gateway replica that started the operation, not every replica.
- Validated locally: unit tests; a real Node Agent with a Docker CLI test double and a mock runtime (invalid limits refused before any docker command; limits that cannot start rolled back and the model running again; limits that start kept; idempotent replay; id reuse with other limits refused; start with a profile; start without one unchanged); a real PostgreSQL gateway with a mock Agent. **Not yet validated on the real Spark** (GPU memory behaviour, real `llama-server`/vLLM start times and out-of-memory failures); that remains external.
