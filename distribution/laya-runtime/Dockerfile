# syntax=docker/dockerfile:1
# Experimental CPU-first System One adapter for x64/ARM64. Preserve the pip
# cache across failed Docker builds: the ARM64 Torch dependency is large, and
# without a BuildKit cache a transient PyPI interruption restarts every wheel.
FROM python:3.11-slim-bookworm
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 \
    HF_HOME=/root/.cache/huggingface HF_HUB_OFFLINE=1 \
    LAYA_HOST=0.0.0.0 LAYA_PORT=8000 LAYA_DEVICE=cpu \
    LAYA_PRELOAD=1 LAYA_MODELS=english LAYA_DEFAULT_MODEL=english \
    LAYA_MAX_CONCURRENT=1 LAYA_MAX_LOADED=1
RUN --mount=type=cache,id=llmproxy-laya-pip,target=/root/.cache/pip,sharing=locked \
    python -m pip install --retries 12 --timeout 60 "laya[serve]==0.4.2"
EXPOSE 8000
CMD ["laya-serve"]
