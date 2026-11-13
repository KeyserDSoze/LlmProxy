# Model family profiles and capability verification

## The problem

Each runtime knows a different part of how to serve a model.

| Knowledge | llama.cpp | vLLM / SGLang | Hugging Face Hub |
| --- | --- | --- | --- |
| architecture, context window, EOS | reads it from the GGUF | reads it from the repository | `config.json`, the `gguf` field of the API |
| chat template | uses the one inside the GGUF (`--jinja`) | uses the repository template | `tokenizer_config.json` |
| tool-call and reasoning parser | **picks them from the template** | **must be named** (`--tool-call-parser`, `--reasoning-parser`) | not published in a machine-readable form |
| recommended sampling | **ignored** (generic defaults: temp 0.8, top_k 40, min_p 0.05) | read with `--generation-config auto` | `generation_config.json` |
| vision projector | downloaded next to the model with `-hf` | depends on the model | `pipeline_tag: image-text-to-text` |
| minimum runtime version / image | not stated | model-specific image tag | README or the project recipe only |

Documentation says what a family *should* do. It does not prove what a running server *does*. LlmProxy therefore has two layers: a curated **profile** that supplies what a runtime cannot infer, and an on-demand **verification** of the running model.

## Profiles

`src/LlmProxy.NodeAgent/ModelFamilyProfiles.cs` holds a small, versioned table. Each entry records the model-name pattern, the launch arguments per runtime, a requirement note, **the source URL and the date it was checked**.

| Profile | vLLM | SGLang | llama.cpp |
| --- | --- | --- | --- |
| Meta Muse Glimmer | `--enable-auto-tool-choice --tool-call-parser muse_glimmer --reasoning-parser muse_glimmer --generation-config auto` | `--reasoning-parser muse --tool-call-parser muse` | `--temp 1.0 --top-p 0.95 --top-k 64` |
| Qwen3 dense (`Qwen3-<n>B`) | `--enable-auto-tool-choice --tool-call-parser hermes --reasoning-parser qwen3` | none (parser names changed between SGLang versions) | none |

Rules:

- The Agent adds the profile flags **before** the operator's own arguments. A flag the operator already set is never added again and is reported as "set by you instead": an explicit choice always wins.
- A model without a profile, or a runtime without a recipe, gets nothing added. Look-alikes are excluded on purpose (`Qwen3-Coder`, `Qwen3-Next` and `Qwen3.5+` use other parsers).
- Flags come only from this allow-list. They are never read from Hugging Face text, because they end up on a Docker command line.
- `NodeAgent__ApplyModelFamilyProfiles=false` turns every profile off on that server.
- The requirement note is shown under the model in Admin. Examples: Muse Glimmer on vLLM needs an image that contains the `muse_glimmer` parsers (`vllm/vllm-openai:muse-glimmer`); on llama.cpp it needs build `b10353` or newer, otherwise the load fails with `unknown model architecture: 'muse-glimmer'`.
- If an image does not know a parser, the startup diagnosis says so and points at the switch above. An outdated `llama.cpp:server-cuda` also gets a `docker pull` hint, because Docker does not refresh a mutable tag on its own.

Profiles apply to existing installations at their **next start**.

### Adding a family

Add an entry only with a documentation source you can cite. Record the URL and today's date, add a test in `ModelFamilyProfileTests`, and keep the pattern narrow. If a parser name is uncertain, leave that runtime out: a wrong flag stops the container from starting.

## Reasoning controls differ per model

Do not assume the OpenAI field. Muse Glimmer takes its level (`low`, `medium`, `high`, `xhigh`) as the chat-template argument `chat_template_kwargs.reasoning_strength`; Meta documents `reasoning_effort` as not implemented by llama.cpp, while the current llama.cpp README lists a server flag `--reasoning-effort` (whether a per-request value is honoured is not verified here). vLLM now returns the reasoning in `message.reasoning` (older versions: `reasoning_content`). Reasoning models also need `stream: true` and a generous `max_tokens`, because reasoning tokens count against it and a non-streaming call can exceed the proxy timeout of a public tunnel.

## Verification

Admin, *Models, Installed*, button **Verify capabilities** (running model, `AdminWrite`):

`POST /api/admin/model-management/deployments/{id}/verify`

It sends four tiny requests straight to the runtime (not through routing, so nothing is counted or limited) with a 120 s limit each, and reports one status per check:

| Check | ok | failed |
| --- | --- | --- |
| chat | the model answered | HTTP error, shown with the server message |
| reasoning | reasoning returned in `reasoning` / `reasoning_content` | thinking markers appear inside the answer (parser missing) |
| streaming | real `text/event-stream` ended by `[DONE]` | not SSE, or no events |
| tools | `tool_calls` with `get_weather` and JSON arguments | tool syntax written as text (parser missing), or vLLM refuses tools (`--enable-auto-tool-choice` missing) |

`warning` means it works with a caveat (for example the whole token budget went into reasoning). `inconclusive` means the request worked but proved nothing: a model that simply answers in words did not show whether its tool parser is configured. The result is shown, not stored; an audit event records only the statuses. A model that is stopped answers `409 runtime_not_running`.

## Limits

- Verification proves the behaviour for the probe prompts only. It is not a benchmark and says nothing about quality or capacity.
- Profiles reflect the documentation on the date recorded in each entry; they can go out of date when a runtime renames a parser.
- A Hugging Face repository is not inspected for these flags: the table is the source.
