"""Content variant key contract, independent of Blender."""
from __future__ import annotations

import re


_VARIANT_PATTERN = re.compile(r"[a-z][a-z0-9]*(?:_[a-z0-9]+)*\Z")


def validate_variant(variant: str) -> str:
    """Validate and return a lower-case snake_case content variant key."""
    if not isinstance(variant, str) or not _VARIANT_PATTERN.fullmatch(variant):
        raise ValueError(
            "Variant must be a lower-case snake_case key beginning with a letter."
        )
    return variant
