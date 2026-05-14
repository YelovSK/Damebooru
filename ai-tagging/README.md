# Damebooru AI Tagging API

Standalone FastAPI service for local image tagging with Camie Tagger v2.

This service downloads Camie Tagger v2 at runtime. The model is not included in
this repository or Docker image. Camie Tagger v2 is licensed separately under
GPL-3.0: https://huggingface.co/Camais03/camie-tagger-v2

## Run Locally

```powershell
cd ai-tagging
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
python -m app.main
```

The first model load downloads:

- `camie-tagger-v2.onnx`
- `camie-tagger-v2-metadata.json`

By default, files are cached under `./models`.

## Run With Docker

```powershell
cd ai-tagging
docker build -t damebooru-ai-tagging .
docker run --rm -p 8000:8000 -v ${PWD}/models:/models damebooru-ai-tagging
```

## API

```http
GET /health
GET /ready
GET /models
POST /tag
```

`/health` reports that the web process is alive. `/ready` reports whether the
model finished loading and inference requests can be served.

`POST /tag` accepts multipart form data:

- `file`: image upload
- `threshold`: optional probability threshold, default `0.492`
- `min_confidence`: optional minimum returned confidence before filtering, default `0.01`
- `top_k`: optional maximum tags per category, default `256`
- `include_below_threshold`: optional boolean, default `false`
- `replace_underscores`: optional boolean, default `false`

Example:

```powershell
curl.exe -F "file=@image.png" -F "threshold=0.492" http://127.0.0.1:8000/tag
```

## Runtime Settings

| Variable | Default | Notes |
| --- | --- | --- |
| `AI_TAGGING_HOST` | `127.0.0.1` locally, `0.0.0.0` in Docker | Bind host |
| `AI_TAGGING_PORT` | `8000` | Bind port |
| `AI_TAGGING_MODEL_REPO` | `Camais03/camie-tagger-v2` | Hugging Face repo |
| `AI_TAGGING_MODEL_FILE` | `camie-tagger-v2.onnx` | ONNX model filename |
| `AI_TAGGING_METADATA_FILE` | `camie-tagger-v2-metadata.json` | Metadata filename |
| `AI_TAGGING_MODEL_DIR` | `./models` | Runtime model cache |
| `AI_TAGGING_PROVIDER` | `cpu` | Currently `cpu`; future values can map to GPU providers |
| `AI_TAGGING_DEFAULT_THRESHOLD` | `0.492` | Camie v2 macro-optimized threshold from model card |
| `AI_TAGGING_MAX_UPLOAD_MB` | `32` | Upload limit |

## GPU Later

The inference layer accepts ONNX Runtime providers through one small provider mapping.
For now the Docker image installs CPU-only `onnxruntime`. NVIDIA CUDA, DirectML, or
OpenVINO can be added later by changing dependencies/image base and extending the
provider mapping in `app/settings.py`.
