#!/usr/bin/env python3
"""One-off RIG-002 S2 refresh: rewrite stale cached bone indices in template scenes.

After helper-chain regeneration changed the imported skeleton bone order, the serialised
``<prefix>_bone`` caches and ``BoneAttachment3D.bone_idx`` values in the saved template
scenes must be refreshed to the re-imported skeletons' current name-to-index mapping.
Only the integer property lines change; the companion check tool and the integration
guard prove the contract afterwards.
"""

from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
GAME_DIR = REPO_ROOT / "game"

MODIFIER_NAME_LINE = re.compile(r'^(?P<indent>\s*)(?P<prefix>settings/\d+/(?:root|middle|end|apply))_bone_name = "(?P<bone>[^"]+)"$')
MODIFIER_INDEX_LINE = re.compile(r"^(?P<indent>\s*)(?P<prefix>settings/\d+/(?:root|middle|end|apply))_bone = (?P<index>-?\d+)$")
ATTACHMENT_NAME_LINE = re.compile(r'^bone_name = "(?P<bone>[^"]+)"$')
ATTACHMENT_INDEX_LINE = re.compile(r"^bone_idx = (?P<index>-?\d+)$")

DUMP_SCRIPT = '''
extends SceneTree

func _init() -> void:
	var packed: PackedScene = load(SCENE_PATH)
	if packed == null:
		print("FAILED to load ", SCENE_PATH)
		quit(1)
		return
	var root: Node = packed.instantiate()
	var skeletons: Array = root.find_children("*", "Skeleton3D", true, false)
	if skeletons.is_empty():
		print("FAILED no skeleton")
		quit(1)
		return
	var skeleton: Skeleton3D = skeletons[0]
	for index in range(skeleton.get_bone_count()):
		print(skeleton.get_bone_name(index), "\\t", index)
	root.free()
	quit(0)
'''


def imported_bone_map(imported_scn_res_path: str) -> dict[str, int]:
    script_path = GAME_DIR / "temp" / "asset_ops_dump_skeleton_for_refresh.gd"
    script_path.write_text(DUMP_SCRIPT.replace("SCENE_PATH", f'"{imported_scn_res_path}"'))
    result = subprocess.run(
        ["godot-mono", "--headless", "--xr-mode", "off", "--path", str(GAME_DIR), "-s", "res://temp/asset_ops_dump_skeleton_for_refresh.gd"],
        check=True,
        capture_output=True,
        text=True,
        timeout=240,
    )
    mapping: dict[str, int] = {}
    for line in result.stdout.splitlines():
        if "\t" not in line:
            continue
        name, index = line.rsplit("\t", 1)
        mapping[name] = int(index)
    if not mapping:
        raise RuntimeError(f"Could not read bone order from {imported_scn_res_path}: {result.stdout} {result.stderr}")
    return mapping


def refresh_scene(scene_path: Path, bone_map: dict[str, int]) -> list[str]:
    lines = scene_path.read_text(encoding="utf-8").splitlines(keepends=True)
    changes: list[str] = []
    pending_modifier: tuple[str, str] | None = None
    pending_attachment: str | None = None
    in_attachment = False
    for index, raw in enumerate(lines):
        line = raw.strip()
        if line.startswith("[node "):
            in_attachment = "type=\"BoneAttachment3D\"" in line
            pending_modifier = None
            pending_attachment = None
            continue
        modifier_name = MODIFIER_NAME_LINE.match(line)
        if modifier_name is not None:
            pending_modifier = (modifier_name.group("prefix"), modifier_name.group("bone"))
            continue
        modifier_index = MODIFIER_INDEX_LINE.match(line)
        if modifier_index is not None and pending_modifier is not None:
            prefix, bone = pending_modifier
            if prefix == modifier_index.group("prefix"):
                expected = bone_map.get(bone)
                current = int(modifier_index.group("index"))
                if expected is not None and expected != current:
                    lines[index] = raw.replace(f"= {current}", f"= {expected}", 1)
                    changes.append(f"{scene_path.name}: modifier {prefix} bone '{bone}': {current} -> {expected}")
                pending_modifier = None
            continue
        attachment_name = ATTACHMENT_NAME_LINE.match(line)
        if attachment_name is not None and in_attachment:
            pending_attachment = attachment_name.group("bone")
            continue
        attachment_index = ATTACHMENT_INDEX_LINE.match(line)
        if attachment_index is not None and in_attachment and pending_attachment is not None:
            expected = bone_map.get(pending_attachment)
            current = int(attachment_index.group("index"))
            if expected is not None and expected != current:
                lines[index] = raw.replace(f"= {current}", f"= {expected}", 1)
                changes.append(f"{scene_path.name}: attachment bone '{pending_attachment}': {current} -> {expected}")
            pending_attachment = None
    scene_path.write_text("".join(lines), encoding="utf-8")
    return changes


def main() -> int:
    female_map = imported_bone_map("res://.godot/imported/reference_female.blend-13a31033fa54f5c1c948a0c6df3325cc.scn")
    male_map = imported_bone_map("res://.godot/imported/reference_male.blend-d4a9610f54cfd9bc7366fd40539f63b4.scn")
    targets = [
        (REPO_ROOT / "game/assets/characters/templates/reference_female/reference_female_base.tscn", female_map),
        (REPO_ROOT / "game/assets/characters/templates/reference_male/reference_male_base.tscn", male_map),
        (REPO_ROOT / "game/tests/speech/a2f_lipsync_player_test.tscn", female_map),
        (REPO_ROOT / "game/tests/speech/wav2arkit_lipsync_player_test.tscn", female_map),
    ]
    total = 0
    for scene_path, bone_map in targets:
        changes = refresh_scene(scene_path, bone_map)
        for change in changes:
            print(change)
        total += len(changes)
    print(f"Refreshed {total} stale bone index references.")
    return 0 if total >= 0 else 1


if __name__ == "__main__":
    sys.exit(main())
