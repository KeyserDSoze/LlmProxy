# Managed image generation: experimental Diffusers SSD-1B

Initial real-runtime implementation for issue #25. Curated checkpoint:
[segmind/SSD-1B](https://huggingface.co/segmind/SSD-1B), with **Apache-2.0**
license identified in the model card. The Agent records the exact Hugging Face
revision and downloads only the Diffusers subcomponents, not redundant giant
root-format checkpoints or pickle `.bin` files.

The Agent builds a pinned NVIDIA CUDA image (`Dockerfile` for amd64 and
`Dockerfile.arm64` for GB10); no CPU fallback is claimed. Install, start,
stop and uninstall reuse the Node Agent lifecycle. A successful start needs
a real 512x512/1-step PNG generation during adapter warmup. This is not a
substitute for checking quality or safety on real prompts.

Public API: `POST /v1/images/generations` with an API key and logical model
alias. Curated supported parameters:
- `model`: alias is mapped by the Gateway to `segmind/SSD-1B`
- `prompt`: 1–2000 chars
- `size`: `512x512`, `768x768`, or `1024x1024`
- `n`: exactly 1
- `response_format`: `b64_json` only (returns actual PNG)
- `quality`: `standard` only
- `steps`: 1–30, 20 default
- `negative_prompt`: optional, 1000 chars max; `seed`: optional int
- one in-flight generation per runtime; PNG hard limit 8 MiB

Unsupported image edits/variations, URL output, multi-image batches and
custom image repository scripts are explicitly *not* available.

To run binary-aware acceptance after deploying a model, export the Gateway URL, API key and published logical alias, then run `bash tests/hardware/multimodal-smoke.sh images`. The same script supports `tts`, `stt` (requires a speech audio file), and `systemone` with their corresponding aliases. It verifies actual binary/image/audio formats, but not semantic quality. Never commit the API key.

**Acceptance before release or production:** native amd64/GB10 ARM64 Docker
build and pinned dependency checks; verify checkpoint snapshot and true local
offline restart; generated PNG dimensions and decode; rejection cases; GPU OOM,
cancellation, concurrency, inbound and outbound Agent relay, Cloudflare timeout
on large requests; no persisted image/audio binary in audit; licensing and
generated-content safeguards review. Current hardware planning estimates are
not a benchmark, and runtime must not route traffic until the selected
deployment is healthy and enabled. Expose an experimental badge in Admin.

For production deployments, establish model output safeguards appropriate to
the intended use and consider upstream model licensing and prohibited uses.
