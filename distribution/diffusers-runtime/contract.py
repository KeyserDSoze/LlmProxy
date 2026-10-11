"""Dependency-free public image runtime contract and binary validation helpers.

Tested in CI without requiring a downloaded model or an NVIDIA GPU.
"""
import struct

MODEL_ID = "segmind/SSD-1B"
SUPPORTED_SIZES = frozenset({"512x512", "768x768", "1024x1024"})
PNG_HEADER = b"\x89PNG\r\n\x1a\n"
MAX_PNG_BYTES = 8 * 1024 * 1024


def validate_parameters(*, model: str, prompt: str, size: str, n: int,
                        quality: str, response_format: str, steps: int,
                        negative_prompt: str | None, seed: int | None) -> None:
    if model != MODEL_ID:
        raise LookupError("Unknown image model")
    if not isinstance(prompt, str) or not 1 <= len(prompt) <= 2000:
        raise ValueError("Prompt must contain 1–2000 characters")
    if size not in SUPPORTED_SIZES:
        raise ValueError("Supported sizes: 512x512, 768x768, 1024x1024")
    if n != 1 or quality != "standard" or response_format != "b64_json":
        raise ValueError("Only one standard-quality b64_json image is supported")
    if not isinstance(steps, int) or not 1 <= steps <= 30:
        raise ValueError("Steps must be between 1 and 30")
    if negative_prompt is not None and len(negative_prompt) > 1000:
        raise ValueError("Negative prompt is too long")
    if seed is not None and not 0 <= seed <= 2147483647:
        raise ValueError("Seed is outside the supported range")


def png_matches_request(payload: bytes, width: int, height: int) -> bool:
    if len(payload) < 24 or len(payload) > MAX_PNG_BYTES:
        return False
    if not payload.startswith(PNG_HEADER) or payload[12:16] != b"IHDR":
        return False
    return struct.unpack(">II", payload[16:24]) == (width, height)
