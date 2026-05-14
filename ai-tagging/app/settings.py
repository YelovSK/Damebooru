from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class Settings:
    host: str = os.environ.get("AI_TAGGING_HOST", "127.0.0.1")
    port: int = int(os.environ.get("AI_TAGGING_PORT", "8000"))
    model_repo: str = os.environ.get("AI_TAGGING_MODEL_REPO", "Camais03/camie-tagger-v2")
    model_file: str = os.environ.get("AI_TAGGING_MODEL_FILE", "camie-tagger-v2.onnx")
    metadata_file: str = os.environ.get("AI_TAGGING_METADATA_FILE", "camie-tagger-v2-metadata.json")
    model_dir: Path = Path(os.environ.get("AI_TAGGING_MODEL_DIR", "models"))
    provider: str = os.environ.get("AI_TAGGING_PROVIDER", "cpu").lower()
    default_threshold: float = float(os.environ.get("AI_TAGGING_DEFAULT_THRESHOLD", "0.492"))
    min_confidence: float = float(os.environ.get("AI_TAGGING_MIN_CONFIDENCE", "0.01"))
    top_k: int = int(os.environ.get("AI_TAGGING_TOP_K", "256"))
    max_upload_mb: int = int(os.environ.get("AI_TAGGING_MAX_UPLOAD_MB", "32"))
    openvino_device: str = os.environ.get("AI_TAGGING_OPENVINO_DEVICE", "CPU")
    openvino_cache_dir: str | None = os.environ.get("AI_TAGGING_OPENVINO_CACHE_DIR")

    @property
    def max_upload_bytes(self) -> int:
        return self.max_upload_mb * 1024 * 1024

    @property
    def onnx_providers(self) -> list[str | tuple[str, dict[str, str]]]:
        if self.provider == "cpu":
            return ["CPUExecutionProvider"]

        if self.provider == "cuda":
            return ["CUDAExecutionProvider", "CPUExecutionProvider"]

        if self.provider == "directml":
            return ["DmlExecutionProvider", "CPUExecutionProvider"]

        if self.provider == "openvino":
            options: dict[str, str] = {
                "device_type": self.openvino_device,
            }

            if self.openvino_cache_dir:
                options["cache_dir"] = self.openvino_cache_dir

            return [
                ("OpenVINOExecutionProvider", options),
                "CPUExecutionProvider",
            ]

        raise ValueError(
            "AI_TAGGING_PROVIDER must be one of: cpu, cuda, directml, openvino."
        )