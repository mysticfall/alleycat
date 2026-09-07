#!/usr/bin/env python3
"""Publish declared Kimodo package stages through validated journalled replacement."""
from __future__ import annotations

from pathlib import Path
import sys


def _pipeline_root() -> Path:
    for candidate in Path(__file__).resolve().parents:
        if (candidate / "kimodo_pipeline").is_dir() and (candidate / "config").is_dir():
            return candidate
    raise RuntimeError("Kimodo pipeline package root could not be located.")


PIPELINE_ROOT = _pipeline_root()
if str(PIPELINE_ROOT) not in sys.path:
    sys.path.insert(0, str(PIPELINE_ROOT))

from kimodo_pipeline.validated_publication import main


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"Kimodo validated publication failed: {error}", file=sys.stderr)
        raise SystemExit(2)
