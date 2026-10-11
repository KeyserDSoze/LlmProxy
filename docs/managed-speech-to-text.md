# Managed speech-to-text: experimental CPU Whisper

Date: 2026-10-11. Implemented as a dedicated Node Agent runtime, distinct from OpenAI Chat/System One and from the pre-existing multimodal gateway forwarding added in PR #21.

## Why

OpenAI-compatible public routes `POST /v1/audio/transcriptions` and `/v1/audio/translations` already exist on the gateway. Previously they required an **already running** remote audio server. This profile permits downloading, starting, stopping and removing a Whisper transcription server through LLMProxy **without manually installing Python on the host**.

## Available curated checkpoints

- `Systran/faster-whisper-tiny` — `whisper-tiny-cpu`
- `Systran/faster-whisper-base` — `whisper-base-cpu`
- `Systran/faster-whisper-small` — `whisper-small-cpu`

Source/checkpoint license is reported as MIT by Hugging Face. These are converted CTranslate2 Whisper repositories; do not use a generic Chat checkpoint with this runtime. The Node Agent accepts only matching catalog ID and repository ID; it does not run arbitrary user-provided code or shell commands. Models may be multilingual, and translation produces **English output only**.

### Operator path

1. Update the LLMProxy gateway and registered Linux Node Agent to the release containing the runtime adapter. **Updating the gateway alone does not install the new Agent bundle.**
2. Admin → **Install & manage models** → select your machine (Spark x64/ARM64 or another managed node).
3. Find the Whisper Tiny/Base/Small **AudioTranscription** profile, inspect memory/disk, click Install. The dialog deliberately has **no Chat token, KV-cache, GPU selection, or tensor-parallel settings**.
4. Await Agent's persistent download job. Agent builds a versioned `llmproxy-faster-whisper:0.1` CPU image, downloads checkpoint files by Hugging Face revision and retains them in the shared cache.
5. Start. The adapter preloads the local downloaded model and becomes healthy only after successful load. Confirm health / activate for routing and exercise the audio endpoints with real input; **a 200 from /health does not prove accuracy or performance**.
6. Stop/remove through Admin. Cache stays intact for reuse. New Agent startup `--restart unless-stopped` policy applies while manually stopped containers remain stopped.

### API examples

Transcription:
```sh
curl -fsS https://YOUR-GATEWAY/v1/audio/transcriptions \
  -H "Authorization: Bearer YOUR-API-KEY" \
  -F "model=whisper-tiny-cpu" -F "file=@sample.wav" \
  -F "response_format=json"
```
Translation to English:
```sh
curl -fsS https://YOUR-GATEWAY/v1/audio/translations \
  -H "Authorization: Bearer YOUR-API-KEY" \
  -F "model=whisper-tiny-cpu" -F "file=@italian.wav"
```

Response formats: `json`, `verbose_json`, `text`, `srt`, `vtt`. Max upload size: 25 MiB enforced at gateway AND adapter. Single CPU model generator/one request at a time; current capacity admission and routing policies still apply.

### Guarantees and limitations

- Image based on Python 3.11 multiarch with pinned Python adapter package versions; first Agent installation builds it locally and must reach permitted repositories/package indexes. No CUDA acceleration is claimed (even on GB10 GPU), and ARM64 binary compatibility requires a real Spark Docker build test.
- Input audio is processed inside the model container; the gateway does **not** persist raw audio in Request Audit. Request metrics record metadata only.
- No streaming, realtime WebSocket, diarization, TTS or image generation. These are separate modalities and runtimes.
- **Hardware acceptance pending:** CI validates source, API/model surface and mock lifecycle; cold-start, real WAV transcription/translation, x64+ARM64 image build and CPU latency must be measured before calling this production-ready.
- The existing durable Agent download job finalization into gateway models/deployments still relies on Admin polling after completion; this is separate backlog and should be moved server-side.
- Faster-Whisper model reload uses the recorded downloaded source revision and local-only model cache to prevent silent checkpoint upgrades; new upstream commits require an intentional installation/update path.

## Next multimodal runtimes

- **TTS:** choose a pinned, licence-reviewed voice model and runtime (e.g. Piper or Kokoro), provide exact OpenAI `POST /v1/audio/speech` formats/voices, avoid automatic speaker cloning, then validate x64/ARM64 and real audio.
- **Image generation:** separate Diffusers/SD-type profile, supported text-to-image API `POST /v1/images/generations` with size and output constraints, return base64/URL according to supported output formats, explicit GPU memory and job admission, and reject unsupported edit/variations endpoints.
- **System One / Laya:** separate typed classifier runtime remains tracked by GitHub issue #19. Hugging Face is an optional model repository, not the SystemOne protocol or a required source for all models.
