from __future__ import annotations

import json
import sys
import threading
import time
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import requests
from huggingface_hub import hf_hub_url
from PIL import Image, UnidentifiedImageError

from .models import ModelSpec, TagList
from .schemas import TagResult
from .settings import Settings

CATEGORY_ORDER = ["general", "character", "copyright", "artist", "style", "meta", "year", "rating"]
WHITE = (255, 255, 255)


class TaggingError(Exception):
    pass


@dataclass(frozen=True)
class RawTaggingResult:
    tags: list[TagResult]
    tags_by_category: dict[str, list[TagResult]]


Infer = Callable[[np.ndarray], Sequence[np.ndarray]]


class Tagger:
    def __init__(self, spec: ModelSpec, infer: Infer, active_provider: str, tags: TagList) -> None:
        self.spec = spec
        self.infer = infer
        self.active_provider = active_provider
        self.tags = tags

    @classmethod
    def load(cls, spec: ModelSpec, settings: Settings) -> "Tagger":
        try:
            model_dir = settings.model_dir / spec.id
            model_dir.mkdir(parents=True, exist_ok=True)
            model_path = download_file_with_progress(
                repo_id=spec.repo,
                filename=spec.model_file,
                destination=model_dir / spec.model_file,
            )
            tags_path = download_file_with_progress(
                repo_id=spec.repo,
                filename=spec.tags_file,
                destination=model_dir / spec.tags_file,
            )

            with Path(tags_path).open("r", encoding="utf-8") as tags_file:
                tags = spec.read_tags(json.load(tags_file))

            if settings.provider == "openvino":
                infer, active_provider = load_openvino(model_path, settings)
            else:
                infer, active_provider = load_onnxruntime(model_path, settings)

            return cls(spec=spec, infer=infer, active_provider=active_provider, tags=tags)
        except Exception as ex:
            raise TaggingError(f"Failed to load {spec.id}: {ex}") from ex

    def tag_path(
        self,
        image_path: Path,
        *,
        threshold: float | None,
        min_confidence: float,
        top_k: int,
        include_below_threshold: bool,
        replace_underscores: bool,
    ) -> RawTaggingResult:
        try:
            image_array = self._preprocess(image_path)
            outputs = self.infer(image_array[np.newaxis, :])
        except UnidentifiedImageError as ex:
            raise TaggingError("Uploaded file is not a readable image.") from ex
        except Exception as ex:
            raise TaggingError(f"Inference failed: {ex}") from ex

        output = outputs[self.spec.output_index]
        probabilities = (output if self.spec.outputs_probabilities else sigmoid(output))[0]

        tags_by_category: dict[str, list[TagResult]] = {}
        for idx in np.flatnonzero(probabilities >= min_confidence):
            score = float(probabilities[idx])
            name, category = self.tags[idx]
            category_threshold = self.spec.threshold_for(category) if threshold is None else threshold
            if not include_below_threshold and score < category_threshold:
                continue

            if replace_underscores:
                name = name.replace("_", " ")

            tags_by_category.setdefault(category, []).append(
                TagResult(name=name, score=round(score, 6), category=category)
            )

        for category, tags in tags_by_category.items():
            tags.sort(key=lambda tag: tag.score, reverse=True)
            tags_by_category[category] = tags[:top_k]

        ordered_categories = [
            *[category for category in CATEGORY_ORDER if category in tags_by_category],
            *sorted(category for category in tags_by_category if category not in CATEGORY_ORDER),
        ]
        ordered_by_category = {
            category: tags_by_category[category]
            for category in ordered_categories
        }
        flat_tags = [
            tag
            for category in ordered_categories
            for tag in ordered_by_category[category]
        ]
        flat_tags.sort(key=lambda tag: tag.score, reverse=True)

        return RawTaggingResult(tags=flat_tags, tags_by_category=ordered_by_category)

    def _preprocess(self, image_path: Path) -> np.ndarray:
        spec = self.spec
        with Image.open(image_path) as image:
            width, height = image.size
            if width <= 0 or height <= 0:
                raise TaggingError("Image has invalid dimensions.")

            if image.mode in ("RGBA", "LA", "PA") or "transparency" in image.info:
                rgba = image.convert("RGBA")
                image = Image.new("RGB", rgba.size, WHITE)
                image.paste(rgba, mask=rgba.getchannel("A"))
            else:
                image = image.convert("RGB")

            scale = spec.image_size / max(width, height)
            new_width = max(1, round(width * scale))
            new_height = max(1, round(height * scale))

            resized = image.resize((new_width, new_height), spec.resample)
            padded = Image.new("RGB", (spec.image_size, spec.image_size), spec.pad_color)
            padded.paste(
                resized,
                ((spec.image_size - new_width) // 2, (spec.image_size - new_height) // 2),
            )

            image_array = np.asarray(padded, dtype=np.float32) / 255.0
            image_array = (image_array - np.array(spec.mean, dtype=np.float32)) / np.array(spec.std, dtype=np.float32)
            return np.transpose(image_array, (2, 0, 1)).astype(np.float32)


def load_onnxruntime(model_path: Path, settings: Settings) -> tuple[Infer, str]:
    import onnxruntime as ort

    print("Available ONNX Runtime providers:", ort.get_available_providers(), file=sys.stderr)
    print("Requested ONNX Runtime providers:", settings.onnx_providers, file=sys.stderr)

    session = ort.InferenceSession(model_path, providers=settings.onnx_providers)
    input_name = session.get_inputs()[0].name

    print("Active ONNX Runtime providers:", session.get_providers(), file=sys.stderr)

    return (
        lambda image_batch: session.run(None, {input_name: image_batch}),
        session.get_providers()[0],
    )


def load_openvino(model_path: Path, settings: Settings) -> tuple[Infer, str]:
    import openvino as ov

    core = ov.Core()
    if settings.openvino_cache_dir:
        Path(settings.openvino_cache_dir).mkdir(parents=True, exist_ok=True)
        core.set_property({"CACHE_DIR": settings.openvino_cache_dir})

    # Compiling from the path, not from a read_model() result, lets OpenVINO import a
    # cached blob without parsing the ONNX file, which would briefly hold several
    # copies of the weights.
    compiled_model = core.compile_model(
        model_path,
        settings.openvino_device,
        {"PERFORMANCE_HINT": "LATENCY"},
    )

    print("OpenVINO device:", settings.openvino_device, file=sys.stderr)

    # Calling the compiled model reuses one implicit infer request, which fails with
    # "Infer Request is busy" when requests overlap.
    infer_lock = threading.Lock()

    def infer(image_batch: np.ndarray) -> Sequence[np.ndarray]:
        with infer_lock:
            return compiled_model(image_batch).to_tuple()

    return infer, f"OpenVINO:{settings.openvino_device}"


def sigmoid(values: np.ndarray) -> np.ndarray:
    return 1.0 / (1.0 + np.exp(-values))


def download_file_with_progress(repo_id: str, filename: str, destination: Path) -> Path:
    if destination.exists() and destination.stat().st_size > 0:
        print(f"Using cached {filename}: {destination}", file=sys.stderr, flush=True)
        return destination

    url = hf_hub_url(repo_id=repo_id, filename=filename)
    temp_destination = destination.with_suffix(destination.suffix + ".partial")
    temp_destination.unlink(missing_ok=True)

    print(f"Downloading {repo_id}/{filename}...", file=sys.stderr, flush=True)
    with requests.get(url, stream=True, timeout=(10, 60)) as response:
        response.raise_for_status()
        total_bytes = int(response.headers.get("content-length") or 0)
        downloaded_bytes = 0
        last_report_at = time.monotonic()

        with temp_destination.open("wb") as output:
            for chunk in response.iter_content(chunk_size=1024 * 1024):
                if not chunk:
                    continue
                output.write(chunk)
                downloaded_bytes += len(chunk)

                now = time.monotonic()
                if now - last_report_at >= 2:
                    print_download_progress(filename, downloaded_bytes, total_bytes)
                    last_report_at = now

    temp_destination.replace(destination)
    print_download_progress(filename, destination.stat().st_size, destination.stat().st_size)
    print(f"Downloaded {filename} to {destination}", file=sys.stderr, flush=True)
    return destination


def print_download_progress(filename: str, downloaded_bytes: int, total_bytes: int) -> None:
    if total_bytes > 0:
        percent = downloaded_bytes / total_bytes * 100
        print(
            f"{filename}: {format_mib(downloaded_bytes)} / {format_mib(total_bytes)} ({percent:.1f}%)",
            file=sys.stderr,
            flush=True,
        )
    else:
        print(f"{filename}: {format_mib(downloaded_bytes)} downloaded", file=sys.stderr, flush=True)


def format_mib(byte_count: int) -> str:
    return f"{byte_count / 1024 / 1024:.1f} MiB"
