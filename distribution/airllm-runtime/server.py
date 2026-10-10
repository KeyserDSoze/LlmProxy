"""Experimental AirLLM layer-streamed, single-worker, OpenAI non-streaming Chat adapter.

Serializes generation deliberately. A queue is not parallel token execution.
SSE, Responses and tool calling are *not* advertised until independently proven.
"""
import asyncio
import os
import time
import uuid
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import PlainTextResponse
from pydantic import BaseModel, Field

MODEL_ID = os.environ["AIRLLM_MODEL_ID"]
MAX_CONTEXT = max(256, min(262144, int(os.getenv("AIRLLM_MAX_CONTEXT", "8192"))))
QUEUE_LIMIT = max(1, min(128, int(os.getenv("AIRLLM_MAX_QUEUED", "1"))))
engine = None
load_error = None
worker = asyncio.Lock()
queued = 0
completed = 0
output_tokens_total = 0
guard = asyncio.Lock()


class Message(BaseModel):
    role: str
    content: str


class Chat(BaseModel):
    model: str
    messages: list[Message]
    stream: bool = False
    max_tokens: int = Field(default=64, ge=1, le=256)
    temperature: float = Field(default=0, ge=0, le=2)


def _load():
    global engine
    from airllm import AutoModel
    import torch
    engine = AutoModel.from_pretrained(MODEL_ID)
    ids = engine.tokenizer("Hello", return_tensors="pt")["input_ids"]
    if torch.cuda.is_available():
        ids = ids.cuda()
    # Force first layer conversion before the readiness probe passes.
    with torch.inference_mode():
        engine.generate(ids, max_new_tokens=1, use_cache=True, return_dict_in_generate=True)


def _infer(messages, length, temperature):
    import torch
    tok = engine.tokenizer
    turns = [{"role": item.role, "content": item.content} for item in messages]
    if getattr(tok, "chat_template", None):
        prompt = tok.apply_chat_template(turns, tokenize=False, add_generation_prompt=True)
    else:
        prompt = "\n".join(f"{m.role}: {m.content}" for m in messages) + "\nassistant:"
    ids = tok(prompt, return_tensors="pt", truncation=True, max_length=MAX_CONTEXT)["input_ids"]
    prompt_length = int(ids.shape[-1])
    if torch.cuda.is_available():
        ids = ids.cuda()
    args = {"max_new_tokens": length, "use_cache": True,
            "return_dict_in_generate": True, "do_sample": temperature > 0}
    if temperature > 0:
        args["temperature"] = temperature
    with torch.inference_mode():
        result = engine.generate(ids, **args)
    result = result.sequences if hasattr(result, "sequences") else result
    out = result[0][prompt_length:]
    return tok.decode(out, skip_special_tokens=True), prompt_length, int(out.shape[-1])


@asynccontextmanager
async def lifespan(app):
    async def warmup():
        global load_error
        try:
            await asyncio.to_thread(_load)
        except Exception as exc:
            load_error = type(exc).__name__ + ": " + str(exc)[:160]
    task = asyncio.create_task(warmup())
    yield
    task.cancel()


app = FastAPI(title="LLMProxy AirLLM experimental", lifespan=lifespan)


@app.get("/health")
async def health():
    if load_error:
        raise HTTPException(503, "Model initialization failed: " + load_error)
    if engine is None:
        raise HTTPException(503, "Converting and preparing model layers")
    return {"status": "ok", "engine": "airllm", "experimental": True}


@app.get("/v1/models")
async def models():
    return {"object": "list", "data": [{"id": MODEL_ID, "object": "model", "owned_by": "airllm"}]}


@app.get("/metrics", response_class=PlainTextResponse)
async def metrics():
    return (f"llmproxy_airllm_queued_requests {queued}\n"
            f"llmproxy_airllm_active_requests {int(worker.locked())}\n"
            f"llmproxy_airllm_completed_requests_total {completed}\n"
            f"llmproxy_airllm_output_tokens_total {output_tokens_total}\n")


@app.post("/v1/chat/completions")
async def chat(body: Chat, request: Request):
    global queued, completed, output_tokens_total
    if body.model != MODEL_ID:
        raise HTTPException(404, "Unknown model")
    if body.stream:
        raise HTTPException(422, "AirLLM SSE is not supported; do not simulate fake streaming")
    if not body.messages or len(body.messages) > 32 or any(
        m.role not in ("system", "developer", "user", "assistant") or len(m.content) > 32768
        for m in body.messages
    ):
        raise HTTPException(422, "Invalid prompt")
    if engine is None or load_error:
        raise HTTPException(503, "AirLLM not ready")
    async with guard:
        if queued >= QUEUE_LIMIT:
            raise HTTPException(429, "AirLLM bounded queue is full")
        queued += 1
    acquired = False
    try:
        try:
            await asyncio.wait_for(worker.acquire(), timeout=120)
            acquired = True
        except asyncio.TimeoutError:
            raise HTTPException(503, "AirLLM inference queue timed out")
        if await request.is_disconnected():
            raise HTTPException(499, "Client disconnected")
        answer, prompt_tokens, output_tokens = await asyncio.to_thread(
            _infer, body.messages, body.max_tokens, body.temperature)
        completed += 1
        output_tokens_total += output_tokens
        return {
            "id": "chatcmpl-" + uuid.uuid4().hex, "object": "chat.completion",
            "created": int(time.time()), "model": MODEL_ID,
            "choices": [{"index": 0, "message": {"role": "assistant", "content": answer},
                         "finish_reason": "stop"}],
            "usage": {"prompt_tokens": prompt_tokens, "completion_tokens": output_tokens,
                      "total_tokens": prompt_tokens + output_tokens},
        }
    finally:
        if acquired:
            worker.release()
        async with guard:
            queued -= 1


@app.post("/v1/responses")
async def responses():
    raise HTTPException(501, "AirLLM Responses compatibility is not validated")
