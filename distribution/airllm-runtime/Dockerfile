# Experimental: compiled on the paired node, not certified for production.
# Upstream library version pinned; revalidate CUDA/torch/driver on target hardware.
FROM pytorch/pytorch:2.6.0-cuda12.4-cudnn9-runtime
ENV PYTHONUNBUFFERED=1 HF_HOME=/root/.cache/huggingface
RUN python -m pip install --no-cache-dir airllm==4.0.0 transformers==4.57.1 fastapi==0.116.1 uvicorn==0.35.0 sentencepiece
WORKDIR /app
COPY server.py /app/server.py
EXPOSE 8000
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8000", "--workers", "1"]
