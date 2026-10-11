# Experimental amd64 NVIDIA/CUDA image adapter. On ARM64/GB10 use Dockerfile.arm64.
# The pinned upstream dependencies are built into the image; model weights are
# downloaded separately into a revision-pinned shared cache by Node Agent.
FROM pytorch/pytorch:2.6.0-cuda12.4-cudnn9-runtime
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 HF_HOME=/root/.cache/huggingface HF_HUB_OFFLINE=1
RUN python -m pip install --no-cache-dir \
    fastapi==0.116.1 uvicorn==0.35.0 \
    diffusers==0.31.0 transformers==4.44.2 accelerate==1.1.1 \
    huggingface-hub==0.25.2 safetensors==0.4.5 pillow==11.0.0
WORKDIR /app
COPY server.py /app/server.py
COPY contract.py /app/contract.py
EXPOSE 8000
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8000", "--workers", "1"]
