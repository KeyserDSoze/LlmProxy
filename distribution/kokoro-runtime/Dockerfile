# Experimental CPU-only Kokoro serving adapter. No GPU/voice cloning support.
# espeak-ng is an LGPL/GPL-family dependency: review redistribution obligations.
FROM python:3.11-slim-bookworm
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 HF_HOME=/root/.cache/huggingface HF_HUB_OFFLINE=1
RUN apt-get update && apt-get install -y --no-install-recommends \
      espeak-ng libsndfile1 && rm -rf /var/lib/apt/lists/*
RUN python -m pip install --no-cache-dir \
    fastapi==0.116.1 uvicorn==0.35.0 kokoro==0.9.4 soundfile==0.13.1
WORKDIR /app
COPY server.py /app/server.py
EXPOSE 8000
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8000", "--workers", "1"]
