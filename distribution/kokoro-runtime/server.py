"""Experimental CPU-first OpenAI speech adapter; fixed pinned Kokoro checkpoint."""
import asyncio
import os
import re
from contextlib import asynccontextmanager
from io import BytesIO
from pathlib import Path

import numpy as np
import soundfile as sf
import torch
from fastapi import FastAPI, HTTPException
from fastapi.responses import Response
from kokoro import KPipeline, KModel
from pydantic import BaseModel, Field

MODEL_ID = "hexgrad/Kokoro-82M"
MODEL_SHA = os.getenv("KOKORO_MODEL_REVISION", "")
VOICE = "af_heart"
if not re.fullmatch(r"[a-f0-9]{40}", MODEL_SHA):
    raise RuntimeError("Kokoro requires the Agent-pinned model revision")
SNAPSHOT = Path("/root/.cache/huggingface/hub/models--hexgrad--Kokoro-82M/snapshots") / MODEL_SHA
_pipeline = None
_gate = asyncio.Semaphore(1)


class SpeechRequest(BaseModel):
    model: str
    input: str = Field(min_length=1, max_length=4000)
    voice: str = VOICE
    response_format: str = "wav"
    speed: float = Field(default=1.0, ge=0.5, le=2.0)


@asynccontextmanager
async def lifespan(_app: FastAPI):
    global _pipeline
    config, weights, voice = (
        SNAPSHOT / "config.json",
        SNAPSHOT / "kokoro-v1_0.pth",
        SNAPSHOT / "voices/af_heart.pt",
    )
    if not all(p.is_file() for p in (config, weights, voice)):
        raise RuntimeError("Pinned Kokoro weights, config or voice not cached by Agent")
    # Loading files from an explicit snapshot prevents accidental latest revision.
    model = await asyncio.to_thread(
        lambda: KModel(repo_id=MODEL_ID, config=str(config), model=str(weights)).to("cpu").eval()
    )
    _pipeline = KPipeline(lang_code="a", repo_id=MODEL_ID, model=model, device="cpu")
    _pipeline.voices[VOICE] = torch.load(str(voice), map_location="cpu", weights_only=True)
    yield
    _pipeline = None


app = FastAPI(title="LlmProxy Kokoro TTS (experimental)", lifespan=lifespan)


@app.get("/health")
async def health():
    if _pipeline is None:
        raise HTTPException(503, "Voice checkpoint not ready")
    return {"status": "ok", "model": MODEL_ID, "voice": VOICE}


@app.get("/v1/models")
async def models():
    return {"object": "list", "data": [{"id": MODEL_ID, "object": "model", "owned_by": "llmproxy"}]}


def _synthesize(text: str, speed: float) -> bytes:
    output = []
    for _, _, audio in _pipeline(text, voice=VOICE, speed=speed):
        if audio is not None:
            output.append(audio.detach().cpu().numpy() if isinstance(audio, torch.Tensor) else np.asarray(audio))
    if not output:
        raise RuntimeError("No speech samples generated")
    buffer = BytesIO()
    sf.write(buffer, np.concatenate(output), 24000, format="WAV", subtype="PCM_16")
    return buffer.getvalue()


@app.post("/v1/audio/speech")
async def speech(body: SpeechRequest):
    if body.model != MODEL_ID:
        raise HTTPException(404, "Unknown deployed model")
    if body.voice != VOICE:
        raise HTTPException(422, "Only the curated af_heart voice is installed")
    if body.response_format != "wav":
        raise HTTPException(422, "Only WAV is currently supported by this runtime")
    async with _gate:
        try:
            payload = await asyncio.to_thread(_synthesize, body.input, body.speed)
        except Exception as exc:
            raise HTTPException(422, "Unable to synthesize requested speech") from exc
    if len(payload) > 20 * 1024 * 1024:
        raise HTTPException(413, "Generated audio exceeds configured response limit")
    return Response(payload, media_type="audio/wav")
