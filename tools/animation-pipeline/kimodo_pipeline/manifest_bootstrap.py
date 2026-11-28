"""Blender CLI entry point for the read-only bootstrap manifest inspection."""
from __future__ import annotations
import argparse
import json
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from kimodo_pipeline.bootstrap_manifest import ManifestError, build_manifest


def _arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Characterise the locomotion authoring blend for bootstrap extraction (read-only)."
    )
    parser.add_argument("--blend", required=True, help="Path to the monolithic authoring .blend file.")
    parser.add_argument(
        "--character-root",
        help="Character asset root for artefact path projection; requires --variant.",
    )
    parser.add_argument("--variant", help="Lower-case snake_case variant key; requires --character-root.")
    parser.add_argument(
        "--temp-root",
        help="Optional manifest output root beneath the repository game/temp directory.",
    )
    parser.add_argument(
        "--repository-root",
        help="Optional root for repository-fixed artefact projection in disposable tests.",
    )
    arguments = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    return parser.parse_args(arguments)


def main() -> int:
    arguments = _arguments()
    try:
        summary = build_manifest(
            arguments.blend,
            arguments.character_root,
            arguments.variant,
            arguments.temp_root,
            arguments.repository_root,
        )
    except (ManifestError, ValueError) as error:
        print(f"Kimodo bootstrap manifest failed: {error}", file=sys.stderr)
        return 2
    print("KIMODO_BOOTSTRAP_MANIFEST_JSON=" + json.dumps(summary, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
