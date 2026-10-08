"""Blender CLI entry point for the authoring-workspace bootstrap."""
from __future__ import annotations
import argparse
import json
import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from kimodo_pipeline.bootstrap_workspace import (
    WorkspaceError,
    bootstrap_workspace,
    default_character_asset_root,
)


def _arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Compose, validate, and publish one locomotion authoring workspace from the "
            "Gate 1 bootstrap inputs and a validated idle source."
        )
    )
    parser.add_argument(
        "--character-root",
        default=None,
        help=(
            "Character asset root for the workspace; "
            f"defaults to {default_character_asset_root()}."
        ),
    )
    parser.add_argument(
        "--variant", required=True, help="Lower-case snake_case variant key for path derivation."
    )
    parser.add_argument(
        "--setup",
        default=None,
        help="Character retarget setup .blend; defaults to the derived character-setup artefact.",
    )
    parser.add_argument(
        "--template",
        default=None,
        help="Neutral locomotion template .blend; defaults to the derived template artefact.",
    )
    parser.add_argument(
        "--idle-blend",
        required=True,
        help="Blend carrying the idle source action (opened read-only; never modified).",
    )
    parser.add_argument(
        "--idle-action",
        required=True,
        help="Name of the Kimodo-space single-pose idle action inside --idle-blend.",
    )
    parser.add_argument(
        "--temp-root",
        help="Optional staging/report root beneath the repository game/temp directory.",
    )
    parser.add_argument(
        "--publish-root",
        help=(
            "Rebase the workspace destination beneath this root for disposable publication "
            "(tests); defaults to the real character destination."
        ),
    )
    parser.add_argument(
        "--repository-root",
        help="Optional root for repository-fixed bootstrap inputs in disposable tests.",
    )
    parser.add_argument(
        "--approved-character-baselines",
        help=(
            "Optional JSON per-character baseline mapping for a disposable fixture, "
            'as in \'{"fixture_character": {"canonical_source": "<sha256>", ...}}\'.'
        ),
    )
    arguments = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    return parser.parse_args(arguments)


def main() -> int:
    arguments = _arguments()
    try:
        summary = bootstrap_workspace(
            variant=arguments.variant,
            character_asset_root=arguments.character_root,
            setup_path=arguments.setup,
            template_path=arguments.template,
            idle_blend=arguments.idle_blend,
            idle_action=arguments.idle_action,
            temp_root=arguments.temp_root,
            publish_root=arguments.publish_root,
            repository_root_path=arguments.repository_root,
            approved_character_baselines=(
                json.loads(arguments.approved_character_baselines)
                if arguments.approved_character_baselines is not None
                else None
            ),
        )
    except (WorkspaceError, ValueError, OSError) as error:
        print(f"Kimodo workspace bootstrap failed: {error}", file=sys.stderr)
        return 2
    print("KIMODO_BOOTSTRAP_WORKSPACE_JSON=" + json.dumps(summary, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
