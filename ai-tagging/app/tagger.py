from __future__ import annotations

import json
import sys
import time
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import onnxruntime as ort
import requests
from huggingface_hub import hf_hub_url
from PIL import Image, UnidentifiedImageError

from .schemas import TagResult
from .settings import Settings

CATEGORY_ORDER = ["general", "character", "copyright", "artist", "meta", "year", "rating"]
IMAGENET_MEAN = np.array([0.485, 0.456, 0.406], dtype=np.float32)
IMAGENET_STD = np.array([0.229, 0.224, 0.225], dtype=np.float32)
PAD_COLOR = (124, 116, 104)


class TaggingError(Exception):
    pass


@dataclass(frozen=True)
class RawTaggingResult:
    tags: list[TagResult]
    tags_by_category: dict[str, list[TagResult]]


class CamieTagger:
    def __init__(
        self,
        session: ort.InferenceSession,
        metadata: dict,
        model_repo: str,
    ) -> None:
        self.session = session
        self.metadata = metadata
        self.model_repo = model_repo
        self.input_name = session.get_inputs()[0].name
        self.active_provider = session.get_providers()[0]

        dataset_info = metadata.get("dataset_info", {})
        tag_mapping = dataset_info.get("tag_mapping", {})
        self.idx_to_tag: dict[str, str] = tag_mapping.get("idx_to_tag", {})
        self.tag_to_category: dict[str, str] = tag_mapping.get("tag_to_category", {})
        self.image_size = int(metadata.get("model_info", {}).get("img_size", 512))

        if not self.idx_to_tag or not self.tag_to_category:
            raise TaggingError("Camie metadata is missing tag mappings.")

    @classmethod
    def load(cls, settings: Settings) -> "CamieTagger":
        try:
            settings.model_dir.mkdir(parents=True, exist_ok=True)
            model_path = download_file_with_progress(
                repo_id=settings.model_repo,
                filename=settings.model_file,
                destination=settings.model_dir / settings.model_file,
            )
            metadata_path = download_file_with_progress(
                repo_id=settings.model_repo,
                filename=settings.metadata_file,
                destination=settings.model_dir / settings.metadata_file,
            )

            with Path(metadata_path).open("r", encoding="utf-8") as metadata_file:
                metadata = json.load(metadata_file)

            if settings.openvino_cache_dir:
                Path(settings.openvino_cache_dir).mkdir(parents=True, exist_ok=True)

            print("Available ONNX Runtime providers:", ort.get_available_providers(), file=sys.stderr)
            print("Requested ONNX Runtime providers:", settings.onnx_providers, file=sys.stderr)

            session_options = ort.SessionOptions()
            if settings.provider == "openvino":
                session_options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL

            session = ort.InferenceSession(
                model_path,
                sess_options=session_options,
                providers=settings.onnx_providers,
            )

            print("Active ONNX Runtime providers:", session.get_providers(), file=sys.stderr)
            print("Active ONNX Runtime provider options:", session.get_provider_options(), file=sys.stderr)

            return cls(session=session, metadata=metadata, model_repo=settings.model_repo)
        except Exception as ex:
            raise TaggingError(f"Failed to load {settings.model_repo}: {ex}") from ex

    def tag_path(
        self,
        image_path: Path,
        *,
        threshold: float,
        min_confidence: float,
        top_k: int,
        include_below_threshold: bool,
        replace_underscores: bool,
    ) -> RawTaggingResult:
        try:
            image_array = self._preprocess(image_path)
            outputs = self.session.run(None, {self.input_name: image_array[np.newaxis, :]})
        except UnidentifiedImageError as ex:
            raise TaggingError("Uploaded file is not a readable image.") from ex
        except Exception as ex:
            raise TaggingError(f"Inference failed: {ex}") from ex

        logits = outputs[1] if len(outputs) >= 2 else outputs[0]
        probabilities = sigmoid(logits)[0]

        tags_by_category: dict[str, list[TagResult]] = {}
        for idx, score in enumerate(probabilities):
            score_float = float(score)
            if score_float < min_confidence:
                continue
            if not include_below_threshold and score_float < threshold:
                continue

            tag_name = self.idx_to_tag.get(str(idx))
            if tag_name is None:
                continue

            if replace_underscores:
                tag_name = tag_name.replace("_", " ")

            category = self.tag_to_category.get(self.idx_to_tag[str(idx)], "general")
            tags_by_category.setdefault(category, []).append(
                TagResult(name=tag_name, score=round(score_float, 6), category=category)
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
        with Image.open(image_path) as image:
            image = image.convert("RGB")
            width, height = image.size
            if width <= 0 or height <= 0:
                raise TaggingError("Image has invalid dimensions.")

            aspect_ratio = width / height
            if aspect_ratio > 1:
                new_width = self.image_size
                new_height = max(1, int(new_width / aspect_ratio))
            else:
                new_height = self.image_size
                new_width = max(1, int(new_height * aspect_ratio))

            resized = image.resize((new_width, new_height), Image.Resampling.LANCZOS)
            padded = Image.new("RGB", (self.image_size, self.image_size), PAD_COLOR)
            padded.paste(
                resized,
                ((self.image_size - new_width) // 2, (self.image_size - new_height) // 2),
            )

            image_array = np.asarray(padded, dtype=np.float32) / 255.0
            image_array = (image_array - IMAGENET_MEAN) / IMAGENET_STD
            return np.transpose(image_array, (2, 0, 1)).astype(np.float32)


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
