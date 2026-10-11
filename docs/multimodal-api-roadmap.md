# Multimodal roadmap: OpenAI-compatible images + audio, and System One checkpoint sourcing

Status: **Gateway connector first; Agent-managed installation is NOT implemented in this PR.**

## Source of models vs inference protocol

A System One model does **not** have to come from Hugging Face. A logical model's `providerModelName` is the provider-facing identity/metadata; the routing deployment points to an **HTTP(S) runtime service root** on a registered node. The runtime may load checkpoint files from Hugging Face, an internal artifact registry, local disk or a remote operator-managed service. Laya's `laya[serve]` exposes typed `POST /v1/systemone`; neither the Hugging Face model card nor the source URL is a running server. The managed Laya installer is tracked separately (#19).

For multimodal inference, similarly separate **checkpoint source**, **serving engine**, **installed runtime**, **logical model** and **routing deployment**. Generic Hugging Face discovery never means a checkpoint can directly be installed/run.

## First implemented gateway step

| Logical surface | OpenAI-compatible public path | Request | Response | Runtime requirement |
|---|---|---|---|---|
| ImageGeneration | `POST /v1/images/generations` | JSON `model,prompt` | OpenAI-style JSON (normally `data[].b64_json`) | Running image server implementing the matching API, e.g. curated Diffusers adapter |
| SpeechSynthesis | `POST /v1/audio/speech` | JSON `model,input,voice` | Audio binary or upstream event stream | Running OpenAI-compatible TTS server (Kokoro-FastAPI is a candidate) |
| AudioTranscription | `POST /v1/audio/transcriptions` | multipart form `model,file` | JSON `text` or upstream requested format | Running OpenAI-compatible ASR server |
| AudioTranscription | `POST /v1/audio/translations` | multipart form `model,file` | JSON or text | Only an ASR runtime that truly supports audio translation |

The gateway validates request types, size limits (1 MiB JSON, 25 MiB audio), explicit model alias, requires the usual LLMProxy API key, applies rate and physical/deployment capacity limits, selects *only* deployments for the requested surface, rewrites the public `model` alias to the provider model identity and forwards through an HTTP(S) service root or the outbound Linux Agent relay. It preserves upstream media content type. Images/audio are **non-idempotent**: do not automatically retry on a different node after a failed request, to avoid generating/billing twice.

`GET /v1/models` advertises OpenAi and image/audio model aliases; `GET /v1/systemone/models` continues to list classifiers separately. This is not a guarantee that every declared logical model has a healthy connected runtime.

### Examples (a correctly configured healthy compatible runtime must already be connected)

```bash
curl "$BASE_URL/v1/images/generations" -H "Authorization: Bearer $LLMPROXY_API_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"model":"local-image","prompt":"Illustration of a small red cabin on a snowy hill","size":"1024x1024"}'

curl "$BASE_URL/v1/audio/speech" -H "Authorization: Bearer $LLMPROXY_API_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"model":"local-tts","voice":"af_heart","input":"Ciao, sono un assistente vocale.","response_format":"mp3"}' \
  --output voice.mp3

curl "$BASE_URL/v1/audio/transcriptions" -H "Authorization: Bearer $LLMPROXY_API_KEY" \
  -F 'model=local-whisper' -F 'file=@speech.wav'
```

New logical model Admin UI surfaces: ImageGeneration, SpeechSynthesis, AudioTranscription. Assign an explicit runtime service root for each deployment (never blind deploy-to-all on arbitrary physical nodes). Publishing and connecting are not equivalent to starting a model.

## Supported upstream candidates to validate

- **Text-to-image:** Hugging Face Diffusers with an intentionally pinned Qwen-Image or an appropriately licensed FLUX/SDXL variant. Diffusers is a Python pipeline, **not** an OpenAI HTTP server until LLMProxy supplies a serving adapter. On the Spark GB10 ARM64, confirm Torch/CUDA compatibility, VRAM/system unified memory, dtype, disk and cold start.
- **Speech-to-text:** Whisper / faster-whisper or whisper.cpp; the whisper.cpp published main container supports amd64/arm64, but CUDA images are documented amd64-only. Build/test ARM64 CUDA image and provide a deliberate OpenAI multipart adapter when required.
- **Text-to-speech:** Kokoro with a reviewed OpenAI-compatible serving adapter (Kokoro-FastAPI is a good candidate). PIN the runtime/image source and review voice quality and language (Italian support cannot be assumed for every checkpoint). Piper is another candidate if wrapped.
- **System One:** upstream Laya typed `/v1/systemone` server; checkpoint source is configurable and independent of API surface.

## Required next work before 'Install from LLMProxy' is safe

1. **Curation:** modality-specific Hub search (text-to-image, automatic-speech-recognition, text-to-speech), license and model-card review, architecture, memory/storage requirements, local/remote weights, pinned SHA and supported inference backends.
2. **Agent installers:** separately versioned images with amd64/arm64 support and pinned dependencies, downloads/progress/start/stop/health, integration of cache, persistence across reboot, explicit uninstall. Do not reuse vLLM's Chat installation profile for image/audio models.
3. **Adapters:** verified OpenAI JSON/binary/multipart wire shapes including output encoding, supported sizes/quality/voice/language/timestamps, and upstream error translation. Do not claim full OpenAI feature/voice/model coverage where the engine only supports a subset.
4. **Governance and observability:** binary-safe request audit (metadata + SHA/file size, never raw audio/image blobs by default), image count and audio duration usage metrics distinct from token usage, configurable file/time quotas, per-modality cost and safe retention; handle binary streaming without full buffering.
5. **UX:** first-class catalog filters for each modality, explicit 'Install' vs 'Connect existing', runtime/hardware model fit, audio upload tests and image preview from Admin, output format selection, locale/theme/mobile adherence.
6. **Tests:** API-key denial, JSON/multipart validation, alias rewriting, surface isolation, capacity exhaustion, upstream timeouts, cancellation, relay, 500-with-no-failover, corrupted media, max upload, real binary roundtrip and full Spark ARM64 integration.

## Technical/compatibility limitations in the gateway-only phase

- No automatic model download or engine installation; the admin creates a logical model and an explicit deployment to a separately operated compatible service.
- Image editing/variations, realtime/audio streaming sessions, fine-grained speech alignment and image upload are not yet exposed; only the paths in the table.
- The gateway does not transcode formats or make an arbitrary model OpenAI-compatible; the upstream server must already implement the endpoint and selected options. Response is forwarded unmodified.
- Only request-count and generic latency metrics are available initially; audio duration, image count and GPU output-specific metrics require schema updates.
- Raw binary content must **not** be stored as request/response text in the content-audit pipeline. The first gateway-only phase leaves multimodal paths out of existing text-content audit, pending a metadata-safe design.
