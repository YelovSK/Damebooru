from __future__ import annotations

import asyncio
import os
import tempfile
import time
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Annotated

from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from fastapi.responses import FileResponse, JSONResponse
import uvicorn

from .lazy_tagger import LazyTagger
from .models import MODELS, ModelSpec
from .schemas import HealthResponse, ModelInfo, ModelsResponse, TagResponse
from .settings import Settings
from .tagger import TaggingError, elapsed_ms_since

settings = Settings()
if settings.default_model not in MODELS:
    raise ValueError(f"AI_TAGGING_MODEL must be one of: {', '.join(MODELS)}.")
tagger = LazyTagger(settings)


@asynccontextmanager
async def lifespan(_: FastAPI):
    unloader = asyncio.create_task(tagger.unload_when_idle())
    yield
    unloader.cancel()


app = FastAPI(
    title="Damebooru AI Tagging API",
    version="0.1.0",
    lifespan=lifespan,
)


@app.get("/", include_in_schema=False)
async def index() -> FileResponse:
    return FileResponse(Path(__file__).parent / "static" / "index.html")


@app.get("/health", response_model=HealthResponse)
async def health() -> HealthResponse:
    loaded_model = tagger.loaded_model
    return HealthResponse(
        ok=True,
        model_loaded=loaded_model is not None,
        model=loaded_model or settings.default_model,
        provider=settings.provider,
    )


@app.get("/ready", response_model=HealthResponse)
async def ready() -> HealthResponse:
    try:
        async with tagger.use(MODELS[settings.default_model]) as loaded_tagger:
            provider = loaded_tagger.active_provider
    except TaggingError as ex:
        raise HTTPException(status_code=503, detail=str(ex)) from ex

    return HealthResponse(
        ok=True,
        model_loaded=True,
        model=settings.default_model,
        provider=provider,
    )


@app.get("/models", response_model=ModelsResponse)
async def models() -> ModelsResponse:
    return ModelsResponse(
        default_model=settings.default_model,
        loaded_model=tagger.loaded_model,
        models=[
            ModelInfo(
                id=spec.id,
                repo=spec.repo,
                license=spec.license,
                default_threshold=spec.default_threshold,
                category_thresholds=spec.category_thresholds,
            )
            for spec in MODELS.values()
        ],
    )


@app.post("/tag", response_model=TagResponse)
async def tag_image(
    file: Annotated[UploadFile, File()],
    model: Annotated[str | None, Form()] = None,
    threshold: Annotated[float | None, Form()] = None,
    min_confidence: Annotated[float | None, Form()] = None,
    top_k: Annotated[int | None, Form()] = None,
    include_below_threshold: Annotated[bool, Form()] = False,
    replace_underscores: Annotated[bool, Form()] = False,
) -> TagResponse:
    if file.content_type and not file.content_type.startswith("image/"):
        raise HTTPException(status_code=415, detail=f"Unsupported media type: {file.content_type}")

    spec = resolve_model(model)
    effective_min_confidence = settings.min_confidence if min_confidence is None else min_confidence
    effective_top_k = settings.top_k if top_k is None else top_k

    if threshold is not None:
        validate_probability("threshold", threshold)
    validate_probability("min_confidence", effective_min_confidence)
    if effective_top_k <= 0:
        raise HTTPException(status_code=422, detail="top_k must be greater than zero.")

    suffix = Path(file.filename or "upload").suffix
    total = 0
    try:
        with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as temp_file:
            temp_path = Path(temp_file.name)
            while chunk := await file.read(1024 * 1024):
                total += len(chunk)
                if total > settings.max_upload_bytes:
                    raise HTTPException(
                        status_code=413,
                        detail=f"Upload exceeds {settings.max_upload_mb} MB.",
                    )
                temp_file.write(chunk)

        async with tagger.use(spec) as loaded_tagger:
            started_at = time.perf_counter()
            result = await asyncio.to_thread(
                loaded_tagger.tag_path,
                temp_path,
                threshold=threshold,
                min_confidence=effective_min_confidence,
                top_k=effective_top_k,
                include_below_threshold=include_below_threshold,
                replace_underscores=replace_underscores,
            )
            elapsed_ms = elapsed_ms_since(started_at)
            provider = loaded_tagger.active_provider

        return TagResponse(
            model=spec.id,
            provider=provider,
            threshold=threshold,
            min_confidence=effective_min_confidence,
            elapsed_ms=elapsed_ms,
            preprocess_ms=result.preprocess_ms,
            inference_ms=result.inference_ms,
            tags=result.tags,
            tags_by_category=result.tags_by_category,
        )
    except TaggingError as ex:
        raise HTTPException(status_code=500, detail=str(ex)) from ex
    finally:
        await file.close()
        if "temp_path" in locals():
            temp_path.unlink(missing_ok=True)


@app.exception_handler(HTTPException)
async def http_exception_handler(_, exc: HTTPException) -> JSONResponse:
    return JSONResponse(
        status_code=exc.status_code,
        content={"error": True, "detail": exc.detail},
    )


def resolve_model(model: str | None) -> ModelSpec:
    model_id = model or settings.default_model
    spec = MODELS.get(model_id)
    if spec is None:
        raise HTTPException(
            status_code=422,
            detail=f"Unknown model '{model_id}'. Available: {', '.join(MODELS)}.",
        )
    return spec


def validate_probability(name: str, value: float) -> None:
    if value < 0 or value > 1:
        raise HTTPException(status_code=422, detail=f"{name} must be between 0 and 1.")


def main() -> None:
    uvicorn.run(
        "app.main:app",
        host=settings.host,
        port=settings.port,
        reload=os.environ.get("AI_TAGGING_RELOAD", "").lower() in {"1", "true", "yes"},
    )


if __name__ == "__main__":
    main()
