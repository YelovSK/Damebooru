FROM python:3.11-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    AI_TAGGING_HOST=0.0.0.0 \
    AI_TAGGING_PORT=8000 \
    AI_TAGGING_MODEL_DIR=/models

WORKDIR /app

ARG INSTALL_OPENVINO_GPU_DEPS=false

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && if [ "$INSTALL_OPENVINO_GPU_DEPS" = "true" ]; then \
        apt-get install -y --no-install-recommends \
            intel-opencl-icd \
            ocl-icd-libopencl1 \
            clinfo; \
    fi \
    && rm -rf /var/lib/apt/lists/*

COPY requirements.txt .

ARG ONNXRUNTIME_PACKAGE=onnxruntime==1.20.1

RUN pip install --no-cache-dir -r requirements.txt \
    && pip install --no-cache-dir "${ONNXRUNTIME_PACKAGE}"

COPY app ./app
COPY README.md .

EXPOSE 8000

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -fsS "http://127.0.0.1:${AI_TAGGING_PORT}/health" || exit 1

CMD ["python", "-m", "app.main"]
