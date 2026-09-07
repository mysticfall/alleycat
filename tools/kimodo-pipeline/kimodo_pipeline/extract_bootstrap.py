"""Blender CLI entry point for the one-time bootstrap extraction."""
from __future__ import annotations
import argparse
import json
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from kimodo_pipeline.bootstrap_extraction import (
    ExtractionError,
    default_blend_path,
    default_character_asset_root,
    default_manifest_path,
    extract_bootstrap,
)


def _arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Extract the canonical clean rig, neutral template, and character retarget setup "
            "from the monolithic locomotion blend in one validated transaction."
        )
    )
    parser.add_argument(
        "--blend",
        default=None,
        help=f"Monolithic authoring .blend; defaults to {default_blend_path()}.",
    )
    parser.add_argument(
        "--character-root",
        default=None,
        help=f"Character asset root for the setup artefact; defaults to {default_character_asset_root()}.",
    )
    parser.add_argument(
        "--variant", required=True, help="Lower-case snake_case variant key for path derivation."
    )
    parser.add_argument(
        "--manifest",
        default=None,
        help=f"Phase 1 manifest JSON; defaults to {default_manifest_path()}.",
    )
    parser.add_argument(
        "--temp-root",
        help="Optional staging/report root beneath the repository game/temp directory.",
    )
    parser.add_argument(
        "--publish-root",
        help=(
            "Rebase the repository-fixed artefact destinations beneath this root for "
            "disposable publication (tests); defaults to real repository destinations."
        ),
    )
    parser.add_argument(
        "--repository-root",
        help="Optional root for repository-fixed artefacts in disposable tests.",
    )
    arguments = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    return parser.parse_args(arguments)


def main() -> int:
    arguments = _arguments()
    try:
        summary = extract_bootstrap(
            arguments.blend,
            arguments.character_root,
            arguments.variant,
            arguments.temp_root,
            arguments.manifest,
            arguments.publish_root,
            arguments.repository_root,
        )
    except (ExtractionError, ValueError, OSError) as error:
        print(f"Kimodo bootstrap extraction failed: {error}", file=sys.stderr)
        return 2
    print("KIMODO_BOOTSTRAP_EXTRACTION_JSON=" + json.dumps(summary, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
