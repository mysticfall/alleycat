#!/usr/bin/env python3
"""Blender command wrapper staging a per-character Kimodo idle source."""
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

from kimodo_pipeline.idle_source_bootstrap import main


if __name__ == "__main__":
    raise SystemExit(main())
