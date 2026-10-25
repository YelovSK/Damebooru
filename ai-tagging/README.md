# Damebooru AI Tagging API

Standalone FastAPI service for local image tagging.

Models are downloaded at runtime and are not included in this repository or
Docker image. Each is licensed separately:

| Id | Source | License |
| --- | --- | --- |
| `camie-v2` | [Camais03/camie-tagger-v2](https://huggingface.co/Camais03/camie-tagger-v2) | GPL-3.0 |
| `pixai-v1` | [nocturna/pixai-tagger-v1.0-onnx](https://huggingface.co/nocturna/pixai-tagger-v1.0-onnx), an FP16 ONNX export of [pixai-labs/pixai-tagger-v1.0](https://huggingface.co/pixai-labs/pixai-tagger-v1.0) | Apache-2.0 |

PixAI is more accurate (especially for characters) but much slower: it runs at
1008×1008 with about 3× the parameters of Camie.

## Run Locally

```powershell
cd ai-tagging
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
python -m app.main
```

The first load of a model downloads its ONNX file and tag list into
`./models/<id>/`.

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

`GET /` serves a small test page: pick a model, drop or paste an image, and
adjust the threshold live.

`/health` reports that the web process is alive. `/ready` loads the default
model if it is not loaded and reports whether inference requests can be served.
`/models` lists the available models and their default thresholds.

At most one model is loaded at a time. It is loaded on first use, swapped when a
request names a different model (after in-flight requests finish), and unloaded
after `AI_TAGGING_IDLE_UNLOAD_SECONDS` without requests. Swapping is slow, so
batch requests per model instead of interleaving them.

`POST /tag` accepts multipart form data:

- `file`: image upload
- `model`: optional model id, default `AI_TAGGING_MODEL`
- `threshold`: optional probability threshold applied to every category; when omitted, each category uses the model's default (see `/models`)
- `min_confidence`: optional minimum returned confidence before filtering, default `0.01`
- `top_k`: optional maximum tags per category, default `256`
- `include_below_threshold`: optional boolean, default `false`
- `replace_underscores`: optional boolean, default `false`

Example:

```powershell
curl.exe -F "file=@image.png" -F "model=pixai-v1" http://127.0.0.1:8000/tag
```

## Runtime Settings

| Variable | Default | Notes |
| --- | --- | --- |
| `AI_TAGGING_HOST` | `127.0.0.1` locally, `0.0.0.0` in Docker | Bind host |
| `AI_TAGGING_PORT` | `8000` | Bind port |
| `AI_TAGGING_MODEL` | `camie-v2` | Model used when a request doesn't name one |
| `AI_TAGGING_MODEL_DIR` | `./models` | Runtime model cache |
| `AI_TAGGING_PROVIDER` | `cpu` | `cpu`, `cuda` or `directml` run through ONNX Runtime; `openvino` runs through OpenVINO directly |
| `AI_TAGGING_OPENVINO_DEVICE` | `CPU` | OpenVINO target device, for example `CPU`, `GPU`, or `AUTO` |
| `AI_TAGGING_OPENVINO_CACHE_DIR` | empty | Optional OpenVINO compiled model cache directory, passed through OpenVINO `CACHE_DIR` |
| `AI_TAGGING_MIN_CONFIDENCE` | `0.01` | Default confidence floor |
| `AI_TAGGING_TOP_K` | `256` | Default maximum tags per category |
| `AI_TAGGING_MAX_UPLOAD_MB` | `32` | Upload limit |
| `AI_TAGGING_IDLE_UNLOAD_SECONDS` | `300` | Unload the model after this many idle seconds; `0` keeps it loaded |

## OpenVINO

The normal Docker image installs CPU `onnxruntime`. The GHCR workflow also publishes
an `openvino` image tag with OpenVINO (without ONNX Runtime) and Intel OpenCL packages.
The Docker base is pinned to Debian Bookworm because the Intel OpenCL package used
by the OpenVINO image is available there.

For Intel GPU experiments, use the `openvino` image tag, set
`AI_TAGGING_PROVIDER=openvino`, and set `AI_TAGGING_OPENVINO_DEVICE=GPU`.
The container also needs access to the host GPU device, usually `/dev/dri`.
