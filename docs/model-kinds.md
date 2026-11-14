# Model kinds: one workspace and one API per kind

Admin → *Models & inference* has a workspace for every kind of model. They all use the same pages (**Discover & install**, **Installed**, **Jobs**, **Publishing**) and the same components; only the catalog, the endpoint and the properties that make sense change.

| Workspace | Surface (internal) | API | Runtime today | Published properties |
| --- | --- | --- | --- | --- |
| Text (chat) | `OpenAi` | `/v1/chat/completions`, `/v1/responses` | vLLM, SGLang, llama.cpp, AirLLM | tools, vision, reasoning, streaming, context, max output |
| Images | `ImageGeneration` | `/v1/images/generations` | Diffusers (SSD-1B) | n/a |
| Text to speech | `SpeechSynthesis` | `/v1/audio/speech` | Kokoro (CPU) | n/a |
| Speech to text | `AudioTranscription` | `/v1/audio/transcriptions`, `/v1/audio/translations` | faster-whisper (CPU) | n/a |
| Voice (streaming) | `Realtime` | `GET /v1/realtime` (WebSocket) | none bundled; any runtime the gateway reaches directly | n/a |
| Embeddings | `Embedding` | `/v1/embeddings` | llama.cpp `--embedding` (GGUF) | context window |
| System One | `SystemOne` | `/v1/systemone` | Laya (CPU) | n/a; its guided setup is the *Setup* tab |

The **Playground** has one tab per kind (Model chat, Images, Text to speech, Speech to text, Embeddings, System One classifier), each offering only that kind's running models and sending the same request body as the public endpoint through `/api/admin/testing/{images|speech|transcriptions|embeddings}` (`AdminWrite`; same routing, capacity and runtime call as the public route, authenticated by the administrator session). Streaming voice has no Playground tab yet: a browser WebSocket through the administrator session is not built. `GET /v1/modalities` lists every modality with its running models.

By default a workspace lists what is running: *Installed* shows running, starting, installing and failed-to-start models, *Publishing* shows routable models; a tick shows the stopped ones so they can be started or removed. The public `GET /v1/models` follows the same rule.

A model appears in exactly one workspace, by its kind: installed cards, install jobs, the catalog and Publishing are all filtered, so a System One classifier (Laya) never shows up among the text models and an embedding model never among the voice models. System One's own workspace has the same four tabs plus **Setup** (default classifier and connection of an external service); the old address `/admin/system-one` opens it.

The workspace is selected by `?kind=` in the URL (`/admin/models?kind=Embedding`); a missing or unknown value means Text. *Publishing* lists only the models of the workspace; a checkbox shows every type.

## Embeddings

Curated GGUF models (Qwen3 Embedding 0.6B and 4B, BGE-M3, Nomic Embed Text v1.5) are installed like any other model. The gateway starts llama.cpp with `--embedding` and a physical batch (`--batch-size`/`--ubatch-size`) as large as the per-slot context (at most 8192), because llama.cpp refuses an input longer than the physical batch. The pooling type comes from the GGUF file. *Install custom GGUF embedding model* accepts any GGUF repository (the kind travels with the install so the model is registered as an embedding model).

Limits to know: dense vectors only; very long inputs are bounded by the served context; throughput and the right number of parallel slots must be measured on your hardware before bulk indexing.

## Voice (streaming)

`/v1/realtime` is prepared and tested with a real WebSocket relay, but there is no curated runtime yet and the Agent relay does not carry WebSocket. Until a runtime that speaks the realtime protocol is validated on the target hardware, treat it as available only for directly reachable runtimes.

## Jobs

The *Jobs* tab is one compact table for downloads, model starts and administrative operations of the workspace: type, model, status, progress, start time and the actions that apply (cancel, retry, register, start, activate). It is paginated (10, 25 or 50 rows) and shows what is running or waiting for you by default; failures open their diagnosis by themselves.
