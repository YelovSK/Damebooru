from __future__ import annotations

from pydantic import BaseModel, Field


class HealthResponse(BaseModel):
    ok: bool
    model_loaded: bool
    model: str
    provider: str


class ModelsResponse(BaseModel):
    default_model: str
    models: list[dict[str, object]]


class TagResult(BaseModel):
    name: str
    score: float = Field(ge=0, le=1)
    category: str


class TagResponse(BaseModel):
    model: str
    provider: str
    threshold: float
    min_confidence: float
    elapsed_ms: float
    tags: list[TagResult]
    tags_by_category: dict[str, list[TagResult]]
