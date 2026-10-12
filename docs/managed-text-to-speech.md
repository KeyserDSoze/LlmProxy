# Experimental managed Kokoro CPU TTS

First iterative runtime proposal for issue #24. Uses Apache-2.0 checkpoint
`hexgrad/Kokoro-82M` and the curated American English `af_heart` voice.
This is separate from managed Chat, Whisper STT and System One. Exposes a
constrained `POST /v1/audio/speech` accepting the OpenAI `model`, `input`,
`voice`, `response_format` and `speed` properties. The initial adapter
supports only `response_format=wav`, voice `af_heart` and speed 0.5–2;
unsupported formats/voices return 422 rather than pretending to work.

Agent installs pinned HF weights, serializes CPU inference, stops/starts via
the existing managed container lifecycle, and registers only on
`ModelSurface.AudioSpeech`. No real-time streaming, voice cloning or
unbounded length: inputs are limited to 4,000 characters.

The runtime includes `espeak-ng`; its licensing and the complete third-party
dependency tree require an explicit redistribution review before production.
The model weights have a separate Apache-2.0 license.

## Acceptance prerequisites

- Build on Linux amd64 and GB10 arm64 with package and PyTorch wheel smoke.
- Download snapshot and `af_heart` voice, test cold/offline starts with
  `HF_HUB_OFFLINE=1`, verify loaded SHA and assert no implicit network fetch.
- Synthesize 2–3 English utterances through `/v1/audio/speech`, decode
  actual RIFF/WAVE audio and listen for audible content.
- Check input/output limits, unsupported formats/voices, full API key and
  relay routing, CPU latency, memory, restart, update and uninstall.
- Explicitly confirm gateway request audit stores metadata, never WAV bytes.
- Add more voices/languages and MP3 only after validated format conversion
  and voice licensing.

Not production ready until both ARM64 and x64 audio acceptance and legal
review are complete.
