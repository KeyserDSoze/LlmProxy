# CPU-first OpenAI-compatible Whisper adapter. Both amd64 and arm64.
# GPU acceleration is deliberately NOT advertised in this profile until
# CUDA/cuDNN compatibility has been tested on real target hardware.
FROM python:3.11-slim-bookworm
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 HF_HOME=/root/.cache/huggingface
RUN python -m pip install --no-cache-dir \
    fastapi==0.116.1 uvicorn==0.35.0 python-multipart==0.0.20 \
    faster-whisper==1.2.0 huggingface-hub==0.34.4
WORKDIR /app
COPY server.py /app/server.py
EXPOSE 8000
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8000", "--workers", "1"]
