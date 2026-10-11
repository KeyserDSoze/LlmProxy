"""One-process, CPU-first OpenAI STT/translation adapter for CTranslate2 Whisper.

The Agent supplies an allowlisted repository and its pinned downloaded SHA.
This API cannot generate speech, images or Chat completions.
"""
import asyncio
import os
import re
from contextlib import asynccontextmanager
from io import BytesIO

from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from fastapi.responses import JSONResponse, PlainTextResponse, Response
from faster_whisper import WhisperModel

MODEL_ID = os.environ.get("WHISPER_MODEL_ID", "")
MODEL_SHA = os.environ.get("WHISPER_MODEL_REVISION") or None
MAX_AUDIO_BYTES = 25 * 1024 * 1024
_ALLOWED_MODELS = frozenset({
    "Systran/faster-whisper-tiny",
    "Systran/faster-whisper-base",
    "Systran/faster-whisper-small",
})
if MODEL_ID not in _ALLOWED_MODELS:
    raise RuntimeError("Unsupported Whisper model; select a curated checkpoint.")
if MODEL_SHA is not None and not re.fullmatch(r"[a-f0-9]{40}", MODEL_SHA):
    raise RuntimeError("The local model revision must be a pinned commit SHA.")

_engine = None
_gate = asyncio.Semaphore(1)


@asynccontextmanager
async def lifespan(_app: FastAPI):
    global _engine
    # Never silently download a new model revision when a previously installed
    # container is restarted: the Agent manages model downloads and updates.
    _engine = await asyncio.to_thread(
        WhisperModel, MODEL_ID, device="cpu", compute_type="int8",
        cpu_threads=max(1, min(16, int(os.getenv("WHISPER_CPU_THREADS", "4")))),
        local_files_only=True, revision=MODEL_SHA,
    )
    yield
    _engine = None


app = FastAPI(title="LlmProxy Whisper STT adapter", lifespan=lifespan)


@app.get("/health")
def health():
    if _engine is None:
        raise HTTPException(503, "Model not loaded")
    return {"status": "ok", "service": "llmproxy-faster-whisper"}


@app.get("/v1/models")
def models():
    return {"object": "list", "data": [{"id": MODEL_ID, "object": "model", "owned_by": "llmproxy"}]}


def _timestamps(seconds: float, separator: str) -> str:
    milliseconds = max(0, round(seconds * 1000))
    h, remainder = divmod(milliseconds, 3600000)
    m, remainder = divmod(remainder, 60000)
    s, ms = divmod(remainder, 1000)
    return f"{h:02}:{m:02}:{s:02}{separator}{ms:03}"


def _perform(audio: bytes, task: str, language: str | None):
    segments, info = _engine.transcribe(
        BytesIO(audio), task=task, language=language,
        beam_size=3, vad_filter=True
    )
    # Whisper inference is lazy: exhaust the iterator before releasing the lock.
    data = [{"id": i, "seek": 0, "start": float(seg.start),
             "end": float(seg.end), "text": seg.text, "tokens": [],
             "temperature": 0, "avg_logprob": seg.avg_logprob,
             "compression_ratio": seg.compression_ratio,
             "no_speech_prob": seg.no_speech_prob}
            for i, seg in enumerate(segments)]
    return data, info.language


async def _run(file: UploadFile, model: str, task: str,
               language: str | None, response_format: str):
    if model != MODEL_ID:
        raise HTTPException(404, "Unknown deployed model")
    if _engine is None:
        raise HTTPException(503, "Model not loaded")
    if response_format not in {"json", "verbose_json", "text", "srt", "vtt"}:
        raise HTTPException(422, "Unsupported response_format")
    if language and not re.fullmatch("[a-z]{2}", language):
        raise HTTPException(422, "language must be a two-letter language code")
    if task == "translate" and language and language not in {"en"}:
        # Here language is the source language, not the target. Whisper always
        # translates speech to English; do not pretend other targets work.
        pass
    payload = await file.read(MAX_AUDIO_BYTES + 1)
    await file.close()
    if not payload:
        raise HTTPException(400, "Audio file is empty")
    if len(payload) > MAX_AUDIO_BYTES:
        raise HTTPException(413, "Audio upload exceeds 25 MiB")
    async with _gate:
        try:
            segments, detected = await asyncio.to_thread(_perform, payload, task, language)
        except Exception as exc:
            # Corrupt/unsupported audio must not result in a fabricated transcript.
            raise HTTPException(422, "Audio could not be decoded or transcribed") from exc
    text = "".join(item["text"] for item in segments).strip()
    if response_format == "json":
        return JSONResponse({"text": text})
    if response_format == "verbose_json":
        return JSONResponse({"task": task, "language": detected,
                             "duration": max((x["end"] for x in segments), default=0),
                             "text": text, "segments": segments})
    if response_format == "text":
        return PlainTextResponse(text)
    lines = []
    if response_format == "vtt":
        lines.append("WEBVTT\n")
    for index, segment in enumerate(segments, 1):
        if response_format == "srt":
            lines.append(str(index))
        sep = "," if response_format == "srt" else "."
        lines.append(f"{_timestamps(segment['start'], sep)} --> {_timestamps(segment['end'], sep)}")
        lines.extend([segment["text"].strip(), ""])
    return Response("\n".join(lines), media_type="text/plain; charset=utf-8")


@app.post("/v1/audio/transcriptions")
async def transcribe(
    file: UploadFile = File(...), model: str = Form(...),
    language: str | None = Form(None), response_format: str = Form("json")
):
    return await _run(file, model, "transcribe", language, response_format)


@app.post("/v1/audio/translations")
async def translate(
    file: UploadFile = File(...), model: str = Form(...),
    language: str | None = Form(None), response_format: str = Form("json")
):
    return await _run(file, model, "translate", language, response_format)
