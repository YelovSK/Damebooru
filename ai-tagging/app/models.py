from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass, field

from PIL import Image

# Each entry is (name, category), in model output order.
TagList = list[tuple[str, str]]


@dataclass(frozen=True)
class ModelSpec:
    id: str
    repo: str
    model_file: str
    tags_file: str
    license: str
    read_tags: Callable[[dict], TagList]
    image_size: int
    mean: tuple[float, float, float]
    std: tuple[float, float, float]
    pad_color: tuple[int, int, int]
    resample: Image.Resampling
    output_index: int
    outputs_probabilities: bool
    default_threshold: float
    category_thresholds: dict[str, float] = field(default_factory=dict)

    def threshold_for(self, category: str) -> float:
        return self.category_thresholds.get(category, self.default_threshold)


def read_camie_tags(metadata: dict) -> TagList:
    tag_mapping = metadata["dataset_info"]["tag_mapping"]
    idx_to_tag: dict[str, str] = tag_mapping["idx_to_tag"]
    tag_to_category: dict[str, str] = tag_mapping["tag_to_category"]
    names = [idx_to_tag[str(idx)] for idx in range(len(idx_to_tag))]
    return [(name, tag_to_category.get(name, "general")) for name in names]


def read_pixai_tags(config: dict) -> TagList:
    tags: list[str] = config["tags"]
    result: TagList = []
    for category, count in config["tags_split"]:
        offset = len(result)
        result.extend((name, category) for name in tags[offset:offset + count])
    return result


CAMIE_V2 = ModelSpec(
    id="camie-v2",
    repo="Camais03/camie-tagger-v2",
    model_file="camie-tagger-v2.onnx",
    tags_file="camie-tagger-v2-metadata.json",
    license="gpl-3.0",
    read_tags=read_camie_tags,
    image_size=512,
    mean=(0.485, 0.456, 0.406),
    std=(0.229, 0.224, 0.225),
    pad_color=(124, 116, 104),
    resample=Image.Resampling.LANCZOS,
    # The second output holds the refined predictions.
    output_index=1,
    outputs_probabilities=False,
    default_threshold=0.492,
)

PIXAI_V1 = ModelSpec(
    id="pixai-v1",
    repo="nocturna/pixai-tagger-v1.0-onnx",
    model_file="model.onnx",
    tags_file="config.json",
    license="apache-2.0",
    read_tags=read_pixai_tags,
    image_size=1008,
    mean=(0.5, 0.5, 0.5),
    std=(0.5, 0.5, 0.5),
    pad_color=(0, 0, 0),
    resample=Image.Resampling.BICUBIC,
    output_index=0,
    outputs_probabilities=True,
    default_threshold=0.2,
    category_thresholds={
        "general": 0.17,
        "character": 0.27,
        "style": 0.15,
        "copyright": 0.24,
        "meta": 0.17,
        "rating": 0.41,
    },
)

MODELS: dict[str, ModelSpec] = {spec.id: spec for spec in (CAMIE_V2, PIXAI_V1)}
