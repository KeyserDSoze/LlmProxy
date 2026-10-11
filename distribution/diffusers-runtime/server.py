"""Revision-pinned SSD-1B Diffusers image runtime (GPU only, single request).

A curated OpenAI-compatible `/v1/images/generations` endpoint. No arbitrary
repository execution, pickle weight fallback, image edits or variations.
"""
import asyncio
import base64
import os
import re
import time
from contextlib import asynccontextmanager
from io import BytesIO
from pathlib import Path

import torch
from diffusers import DiffusionPipeline
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, ConfigDict, Field
from fastapi.responses import JSONResponse

MODEL_ID = "segmind/SSD-1B"
MODEL_SHA = os.environ.get("DIFFUSERS_MODEL_REVISION", "")
if not re.fullmatch(r"[a-f0-9]{40}", MODEL_SHA):
    raise RuntimeError("Diffusers requires an Agent-pinned 40-character revision SHA")
MODEL_ROOT = Path("/root/.cache/huggingface/hub/models--segmind--SSD-1B/snapshots") / MODEL_SHA
_ALLOWED_SIZES = {"512x512", "768x768", "1024x1024"}
_MAX_PNG_BYTES = 8 * 1024 * 1024
_engine = None
_gate = asyncio.Semaphore(1)


class ImageRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    model: str
    prompt: str = Field(min_length=1, max_length=2000)
    size: str = "512x512"
    n: int = Field(default=1, ge=1, le=1)
    quality: str = "standard"
    response_format: str = "b64_json"
    negative_prompt: str | None = Field(default=None, max_length=1000)
    steps: int = Field(default=20, ge=1, le=30)
    seed: int | None = Field(default=None, ge=0, le=2147483647)


def validate_request(req: ImageRequest) -> None:
    if req.model != MODEL_ID:
        raise HTTPException(status_code=404, detail="Unknown image model")
    if req.size not in _ALLOWED_SIZES:
        raise HTTPException(status_code=422, detail="Supported sizes: 512x512, 768x768, 1024x1024")
    if req.quality != "standard" or req.response_format != "b64_json":
        raise HTTPException(status_code=422, detail="Only standard quality and b64_json are supported")


def _create_image(req: ImageRequest) -> bytes:
    width, height = map(int, req.size.split("x"))
    generator = torch.Generator(device="cuda")
    if req.seed is not None:
        generator.manual_seed(req.seed)
    with torch.inference_mode():
        output = _engine(
            prompt=req.prompt, negative_prompt=req.negative_prompt,
            width=width, height=height, num_inference_steps=req.steps,
            num_images_per_prompt=1, generator=generator,
        )
    if len(output.images) != 1:
        raise RuntimeError("Diffusers did not return exactly one generated image")
    image = output.images[0]
    if image.size != (width, height):
        raise RuntimeError("Generated dimensions do not match request")
    buffer = BytesIO()
    image.save(buffer, format="PNG")
    payload = buffer.getvalue()
    if len(payload) > _MAX_PNG_BYTES or not payload.startswith(b"\x89PNG\r\n\x1a\n"):
        raise RuntimeError("Invalid or oversized generated PNG")
    return payload


@asynccontextmanager
async def lifespan(_app: FastAPI):
    global _engine
    if not torch.cuda.is_available():
        raise RuntimeError("This curated image runtime requires an operational NVIDIA CUDA GPU")
    if not (MODEL_ROOT / "model_index.json").is_file():
        raise RuntimeError("Pinned Diffusers model snapshot not installed by Node Agent")
    _engine = await asyncio.to_thread(
        DiffusionPipeline.from_pretrained, str(MODEL_ROOT),
        torch_dtype=torch.float16, use_safetensors=True,
        local_files_only=True, trust_remote_code=False,
    )
    _engine.to("cuda")
    # Readiness must prove inference, rather than a container listening on a port.
    await asyncio.to_thread(_create_image, ImageRequest(
        model=MODEL_ID, prompt="a plain red square", size="512x512", steps=1))
    yield
    _engine = None
    torch.cuda.empty_cache()


app = FastAPI(title="LlmProxy Diffusers SSD-1B adapter (experimental)", lifespan=lifespan)


@app.get("/health")
async def health():
    if _engine is None:
        raise HTTPException(503, "GPU model is not ready")
    return {"status": "ok", "model": MODEL_ID, "revision": MODEL_SHA,
            "device": "cuda", "mode": "images"}


@app.get("/v1/models")
async def models():
    return {"object": "list", "data": [
        {"id": MODEL_ID, "object": "model", "owned_by": "llmproxy"}]}


@app.post("/v1/images/generations")
async def generate(req: ImageRequest):
    validate_request(req)
    if _engine is None:
        raise HTTPException(503, "Model not yet ready")
    async with _gate:
        try:
            png = await asyncio.to_thread(_create_image, req)
        except torch.cuda.OutOfMemoryError as exc:
            torch.cuda.empty_cache()
            raise HTTPException(503, "Insufficient GPU memory for image generation") from exc
        except Exception as exc:
            raise HTTPException(500, "Image generation failed") from exc
    return JSONResponse({"created": int(time.time()),
                         "data": [{"b64_json": base64.b64encode(png).decode("ascii")}]})
