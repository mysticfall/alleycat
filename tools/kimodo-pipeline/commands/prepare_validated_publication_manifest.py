#!/usr/bin/env python3
"""Generate a declared two-package manifest from validated publication stages."""
from __future__ import annotations

import argparse
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

from kimodo_pipeline.publication_manifest import write_manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stage-root", required=True, type=Path)
    parser.add_argument("--processed-root", required=True, type=Path)
    parser.add_argument("--runtime-root", required=True, type=Path)
    parser.add_argument("--legacy-root", required=True, type=Path, action="append")
    parser.add_argument("--output", required=True, type=Path)
    arguments = parser.parse_args()
    output = write_manifest(
        arguments.stage_root,
        arguments.processed_root,
        arguments.runtime_root,
        arguments.output,
        arguments.legacy_root,
    )
    print(f"KIMODO_PUBLICATION_MANIFEST={output}")


if __name__ == "__main__":
    main()
