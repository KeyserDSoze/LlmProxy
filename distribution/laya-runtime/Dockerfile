# Experimental CPU-first System One serving adapter. Native x64/ARM64 build
# requires real hardware acceptance; no GPU acceleration is advertised.
FROM python:3.11-slim-bookworm
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 \
    HF_HOME=/root/.cache/huggingface HF_HUB_OFFLINE=1 \
    LAYA_HOST=0.0.0.0 LAYA_PORT=8000 LAYA_DEVICE=cpu \
    LAYA_PRELOAD=1 LAYA_MODELS=english LAYA_DEFAULT_MODEL=english \
    LAYA_MAX_CONCURRENT=1 LAYA_MAX_LOADED=1
RUN python -m pip install --no-cache-dir "laya[serve]==0.4.2"
EXPOSE 8000
CMD ["laya-serve"]
