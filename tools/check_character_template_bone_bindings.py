#!/usr/bin/env python3
"""Guard: character template BoneAttachment3D bone indices must match refreshed references.

Run this after regenerating a reference character and re-saving its template scenes
in the Godot editor. The IK modifier bone references inside a saved template are
refreshed by name and act as the authoritative bone-name-to-index mapping; any
BoneAttachment3D whose stored bone_idx disagrees fails loudly here. Pass --fix to
refresh stale indices in place (only the offending bone_idx property lines change).

The runtime counterpart is
AlleyCat.IntegrationTests.Rigging.Installation.TemplateBoneAttachmentBindingIntegrationTests,
which proves the same contract against the loaded skeleton without any installer rebind.
"""

from __future__ import annotations

import argparse
from pathlib import Path

from character_template_bone_bindings import (
    parse_scene_bone_bindings,
    refresh_bone_attachment_binding_indices,
    validate_bone_attachment_bindings,
)


REPO_ROOT = Path(__file__).resolve().parents[1]

DEFAULT_TEMPLATE_PATHS = (
    "game/assets/characters/templates/reference_female/reference_female_base.tscn",
    "game/assets/characters/templates/reference_male/reference_male_base.tscn",
)


def display_path(scene_path: Path) -> str:
    try:
        return str(scene_path.relative_to(REPO_ROOT))
    except ValueError:
        return str(scene_path)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--scene",
        action="append",
        default=[],
        help="Template .tscn to check (repeatable); defaults to the shipped reference templates.",
    )
    parser.add_argument(
        "--fix",
        action="store_true",
        help="Refresh stale BoneAttachment3D bone_idx values in place instead of only reporting.",
    )
    values = parser.parse_args()

    scene_paths = [REPO_ROOT / path for path in (values.scene or list(DEFAULT_TEMPLATE_PATHS))]
    failed = False
    for scene_path in scene_paths:
        scene_text = scene_path.read_text(encoding="utf-8")
        bindings = parse_scene_bone_bindings(scene_text)
        errors = validate_bone_attachment_bindings(bindings)
        if not errors:
            print(f"{display_path(scene_path)}: bone attachment bindings consistent.")
            continue

        failed = True
        if values.fix:
            fixed_text, changes = refresh_bone_attachment_binding_indices(scene_text)
            remaining = validate_bone_attachment_bindings(parse_scene_bone_bindings(fixed_text))
            if changes:
                scene_path.write_text(fixed_text, encoding="utf-8")
                print(f"{display_path(scene_path)}: refreshed stale bone attachment indices:")
                for change in changes:
                    print(f"  - {change}")
            if remaining:
                print(f"{display_path(scene_path)}: binding failures that a refresh cannot resolve:")
                for error in remaining:
                    print(f"  - {error}")
            elif changes:
                failed = False
        else:
            print(f"{display_path(scene_path)}: bone attachment binding failures:")
            for error in errors:
                print(f"  - {error}")

    if failed:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
