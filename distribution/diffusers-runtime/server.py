"""Revision-pinned SSD-1B Diffusers image runtime (GPU only, single request).

A curated OpenAI-compatible `/v1/images/generations` endpoint. No arbitrary
repository execution, pickle weight fallback, image edits or variations.
"""
import asyncio
import base64
import os
import re
import secrets
import time
from contextlib import asynccontextmanager
from io import BytesIO
from pathlib import Path

import torch
from diffusers import DiffusionPipeline
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel, ConfigDict, Field
from fastapi.responses import JSONResponse
from contract import MODEL_ID, validate_parameters, png_matches_request


MODEL_SHA = os.environ.get("DIFFUSERS_MODEL_REVISION", "")
if not re.fullmatch(r"[a-f0-9]{40}", MODEL_SHA):
    raise RuntimeError("Diffusers requires an Agent-pinned 40-character revision SHA")
MODEL_ROOT = Path("/root/.cache/huggingface/hub/models--segmind--SSD-1B/snapshots") / MODEL_SHA
_engine = None
_gate = asyncio.Semaphore(1)


class ImageRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")
    model: str = Field(min_length=1, max_length=160)
    prompt: str = Field(min_length=1, max_length=2000)
    size: str = Field(default="512x512", max_length=16)
    n: int = Field(default=1, ge=1, le=1)
    quality: str = Field(default="standard", max_length=32)
    response_format: str = Field(default="b64_json", max_length=32)
    negative_prompt: str | None = Field(default=None, max_length=1000)
    steps: int = Field(default=20, ge=1, le=30)
    seed: int | None = Field(default=None, ge=0, le=2147483647)


def validate_request(req: ImageRequest) -> None:
    try:
        validate_parameters(model=req.model, prompt=req.prompt, size=req.size,
                            n=req.n, quality=req.quality,
                            response_format=req.response_format, steps=req.steps,
                            negative_prompt=req.negative_prompt, seed=req.seed)
    except LookupError as exc:
        raise HTTPException(status_code=404, detail=str(exc)) from exc
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc


def _create_image(req: ImageRequest) -> bytes:
    width, height = map(int, req.size.split("x"))
    # Unseeded requests must not repeat the same default torch generator seed.
    generator = torch.Generator(device="cuda").manual_seed(
        req.seed if req.seed is not None else secrets.randbelow(2**31))
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
    if not png_matches_request(payload, width, height):
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
        local_files_only=True,
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
    # Reject excess work promptly rather than queueing unbounded GPU requests.
    try:
        await asyncio.wait_for(_gate.acquire(), timeout=0.05)
    except TimeoutError as exc:
        raise HTTPException(503, "Image runtime is busy",
                            headers={"Retry-After": "1"}) from exc
    try:
        try:
            png = await asyncio.to_thread(_create_image, req)
        except torch.cuda.OutOfMemoryError as exc:
            torch.cuda.empty_cache()
            raise HTTPException(503, "Insufficient GPU memory for image generation") from exc
        except Exception as exc:
            raise HTTPException(500, "Image generation failed") from exc
    finally:
        _gate.release()
    return JSONResponse({"created": int(time.time()),
                         "data": [{"b64_json": base64.b64encode(png).decode("ascii")}]})
