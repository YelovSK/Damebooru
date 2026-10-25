from __future__ import annotations

from pydantic import BaseModel, Field


class HealthResponse(BaseModel):
    ok: bool
    model_loaded: bool
    model: str
    provider: str


class ModelInfo(BaseModel):
    id: str
    repo: str
    license: str
    default_threshold: float
    category_thresholds: dict[str, float]


class ModelsResponse(BaseModel):
    default_model: str
    loaded_model: str | None
    models: list[ModelInfo]


class TagResult(BaseModel):
    name: str
    score: float = Field(ge=0, le=1)
    category: str


class TagResponse(BaseModel):
    model: str
    provider: str
    threshold: float | None
    min_confidence: float
    elapsed_ms: float
    preprocess_ms: float
    inference_ms: float
    tags: list[TagResult]
    tags_by_category: dict[str, list[TagResult]]
