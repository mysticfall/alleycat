#!/usr/bin/env python3
"""Apply character retarget metadata to existing Godot Blender import sidecars."""

from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass
from pathlib import Path


BONE_MAP_RESOURCE = (
    'Resource("uid://db42k2j8v05ku", '
    '"res://assets/characters/reference/skeleton_profiles/bone_map_makehuman.tres")'
)
CHARACTER_ROOT_TYPE = '"CharacterBody3D"'
CHARACTER_ROOT_SCRIPT = 'Resource("uid://bh2t1kkfwpvbf", "res://src/Character/Character.cs")'
EYE_ANIMATION_IMPORT_SCRIPT = '"res://assets/characters/import/eye_animation_library_import.gd"'
SILHOUETTE_FILTER = (
    '[&"Head", &"Neck", &"UpperChest", &"Chest", &"Spine", &"Hips", '
    '&"RightThumbMetacarpal", &"RightThumbProximal", &"RightThumbDistal", '
    '&"RightIndexProximal", &"RightIndexIntermediate", &"RightIndexDistal", '
    '&"RightMiddleProximal", &"RightMiddleIntermediate", &"RightMiddleDistal", '
    '&"RightRingProximal", &"RightRingIntermediate", &"RightRingDistal", '
    '&"RightLittleProximal", &"RightLittleIntermediate", &"RightLittleDistal", '
    '&"LeftThumbMetacarpal", &"LeftThumbProximal", &"LeftThumbDistal", '
    '&"LeftIndexProximal", &"LeftIndexIntermediate", &"LeftIndexDistal", '
    '&"LeftMiddleProximal", &"LeftMiddleIntermediate", &"LeftMiddleDistal", '
    '&"LeftRingProximal", &"LeftRingIntermediate", &"LeftRingDistal", '
    '&"LeftLittleProximal", &"LeftLittleIntermediate", &"LeftLittleDistal", '
    '&"RightFoot", &"LeftFoot"]'
)


@dataclass(frozen=True)
class CharacterImportSidecars:
    """Import metadata sidecar paths paired with a generated character output."""

    main: Path
    colliders: Path


def derive_character_import_sidecars(output_file_path: Path) -> CharacterImportSidecars:
    """Return the expected main and collider `.blend.import` sidecar paths."""

    collider_blend_path = output_file_path.with_name(f"{output_file_path.stem}.colliders.blend")
    return CharacterImportSidecars(
        main=Path(f"{output_file_path}.import"),
        colliders=Path(f"{collider_blend_path}.import"),
    )


def main_skeleton_path(character_name: str) -> str:
    return f"PATH:{character_name}/Skeleton3D"


def collider_skeleton_path() -> str:
    return "PATH:Ragdoll/Skeleton3D"


def retarget_node_block(skeleton_path: str) -> str:
    return (
        f'"{skeleton_path}": {{\n'
        f'"retarget/bone_map": {BONE_MAP_RESOURCE},\n'
        '"retarget/rest_fixer/fix_silhouette/enable": true,\n'
        f'"retarget/rest_fixer/fix_silhouette/filter": {SILHOUETTE_FILTER}\n'
        '}'
    )


def _find_balanced_block(text: str, open_brace_index: int) -> tuple[int, int]:
    """Return the inclusive-exclusive range of a brace block, ignoring quoted strings."""

    depth = 0
    in_string = False
    escaped = False
    for index in range(open_brace_index, len(text)):
        character = text[index]
        if in_string:
            if escaped:
                escaped = False
            elif character == "\\":
                escaped = True
            elif character == '"':
                in_string = False
            continue

        if character == '"':
            in_string = True
            continue
        if character == "{":
            depth += 1
            continue
        if character == "}":
            depth -= 1
            if depth == 0:
                return open_brace_index, index + 1

    raise ValueError("Unbalanced Godot import dictionary block.")


def _find_keyed_dictionary(text: str, key: str, start: int = 0, end: int | None = None) -> tuple[int, int, bool] | None:
    """Find a dictionary entry named `key` and report whether its block is followed by a comma."""

    search_end = len(text) if end is None else end
    needle = f'"{key}":'
    key_index = text.find(needle, start, search_end)
    if key_index < 0:
        return None

    open_brace_index = text.find("{", key_index + len(needle), search_end)
    if open_brace_index < 0:
        return None

    _block_start, block_end = _find_balanced_block(text, open_brace_index)
    cursor = block_end
    while cursor < search_end and text[cursor] in " \t":
        cursor += 1
    has_comma = cursor < search_end and text[cursor] == ","
    entry_end = cursor + 1 if has_comma else block_end
    return key_index, entry_end, has_comma


def _find_subresources_value(text: str) -> tuple[int, int, bool] | None:
    prefix = "_subresources="
    key_index = text.find(prefix)
    if key_index < 0:
        return None
    value_start = key_index + len(prefix)
    cursor = value_start
    while cursor < len(text) and text[cursor] in " \t":
        cursor += 1
    if cursor >= len(text) or text[cursor] != "{":
        return None
    _block_start, value_end = _find_balanced_block(text, cursor)
    return value_start, value_end, cursor != value_start


def _replace_or_insert_node_entry(subresources: str, skeleton_path: str) -> str:
    nodes_entry = _find_keyed_dictionary(subresources, "nodes")
    node_block = retarget_node_block(skeleton_path)
    if nodes_entry is None:
        opening_brace = subresources.find("{")
        if opening_brace < 0:
            raise ValueError("Subresources block does not contain an opening dictionary brace.")
        insertion = f'{{\n"nodes": {{\n{node_block}\n}}'
        remainder = subresources[opening_brace + 1 :]
        if remainder.strip() != "}":
            insertion += ","
        else:
            insertion += "\n"
        return insertion + remainder

    nodes_start, nodes_end, nodes_has_comma = nodes_entry
    nodes_text = subresources[nodes_start:nodes_end]
    if nodes_has_comma:
        nodes_text_without_comma = nodes_text[:-1]
    else:
        nodes_text_without_comma = nodes_text

    node_entry = _find_keyed_dictionary(nodes_text_without_comma, skeleton_path)
    if node_entry is not None:
        entry_start, entry_end, entry_has_comma = node_entry
        replacement = node_block + ("," if entry_has_comma else "")
        new_nodes_text = (
            nodes_text_without_comma[:entry_start]
            + replacement
            + nodes_text_without_comma[entry_end:]
        )
    else:
        open_brace = nodes_text_without_comma.find("{")
        close_brace = nodes_text_without_comma.rfind("}")
        existing_body = nodes_text_without_comma[open_brace + 1 : close_brace]
        if existing_body.strip():
            new_body = f"\n{node_block}," + existing_body
        else:
            new_body = f"\n{node_block}\n"
        new_nodes_text = (
            nodes_text_without_comma[: open_brace + 1]
            + new_body
            + nodes_text_without_comma[close_brace:]
        )

    if nodes_has_comma:
        new_nodes_text += ","
    return subresources[:nodes_start] + new_nodes_text + subresources[nodes_end:]


def _existing_skeleton_node_paths(subresources: str) -> list[str]:
    nodes_entry = _find_keyed_dictionary(subresources, "nodes")
    if nodes_entry is None:
        return []

    nodes_start, nodes_end, nodes_has_comma = nodes_entry
    nodes_text = subresources[nodes_start:nodes_end]
    if nodes_has_comma:
        nodes_text = nodes_text[:-1]

    return [
        match.group(1)
        for match in re.finditer(r'"(PATH:[^"]+/Skeleton3D)"\s*:', nodes_text)
        if match.group(1).endswith("/Skeleton3D")
    ]


def _apply_retarget_metadata_to_text(text: str, skeleton_paths: list[str]) -> str:
    subresources_value = _find_subresources_value(text)
    if subresources_value is None:
        raise ValueError("Could not locate `_subresources`.")

    value_start, value_end, _had_leading_space = subresources_value
    subresources = text[value_start:value_end]
    updated_subresources = subresources
    for skeleton_path in skeleton_paths:
        updated_subresources = _replace_or_insert_node_entry(updated_subresources, skeleton_path)
    return text[:value_start] + updated_subresources + text[value_end:]


def _godot_string_literal(value: str) -> str:
    escaped = value.replace('\\', '\\\\').replace('"', '\\"')
    return f'"{escaped}"'


def _replace_or_insert_param(text: str, key: str, value: str) -> str:
    line_pattern = re.compile(rf"^{re.escape(key)}=.*$", re.MULTILINE)
    replacement = f"{key}={value}"
    if line_pattern.search(text):
        return line_pattern.sub(replacement, text, count=1)

    params_match = re.search(r"^\[params\]\n", text, re.MULTILINE)
    if params_match is None:
        raise ValueError("Could not locate `[params]`.")

    insertion_index = params_match.end()
    while text.startswith("\n", insertion_index):
        insertion_index += 1
    return text[:insertion_index] + replacement + "\n" + text[insertion_index:]


def _apply_character_root_metadata_to_text(text: str, character_name: str) -> str:
    updated_text = _replace_or_insert_param(text, "nodes/root_type", CHARACTER_ROOT_TYPE)
    updated_text = _replace_or_insert_param(
        updated_text,
        "nodes/root_name",
        _godot_string_literal(character_name),
    )
    updated_text = _replace_or_insert_param(updated_text, "nodes/root_script", CHARACTER_ROOT_SCRIPT)
    return _replace_or_insert_param(updated_text, "import_script/path", EYE_ANIMATION_IMPORT_SCRIPT)


def apply_retarget_metadata(import_sidecar_path: Path, skeleton_path: str) -> bool:
    """Update one existing import sidecar and return whether its text changed."""

    text = import_sidecar_path.read_text(encoding="utf-8")
    try:
        updated_text = _apply_retarget_metadata_to_text(text, [skeleton_path])
    except ValueError:
        raise ValueError(f'Could not locate `_subresources` in "{import_sidecar_path}".')
    if updated_text == text:
        return False

    import_sidecar_path.write_text(updated_text, encoding="utf-8")
    return True


def apply_main_retarget_metadata(import_sidecar_path: Path, character_name: str) -> bool:
    """Update main sidecar root and skeleton entries, falling back to the generated-name path."""

    text = import_sidecar_path.read_text(encoding="utf-8")
    text_with_root = _apply_character_root_metadata_to_text(text, character_name)
    subresources_value = _find_subresources_value(text)
    if subresources_value is None:
        raise ValueError(f'Could not locate `_subresources` in "{import_sidecar_path}".')

    value_start, value_end, _had_leading_space = subresources_value
    subresources = text[value_start:value_end]
    skeleton_paths = _existing_skeleton_node_paths(subresources)
    if not skeleton_paths:
        skeleton_paths = [main_skeleton_path(character_name)]

    updated_text = _apply_retarget_metadata_to_text(text_with_root, skeleton_paths)
    if updated_text == text:
        return False

    import_sidecar_path.write_text(updated_text, encoding="utf-8")
    return True


def update_existing_character_sidecars(output_file_path: Path, character_name: str) -> list[str]:
    """Update existing generated character sidecars and return user-facing status messages."""

    sidecars = derive_character_import_sidecars(output_file_path)
    targets = (
        (sidecars.main, lambda path: apply_main_retarget_metadata(path, character_name)),
        (sidecars.colliders, lambda path: apply_retarget_metadata(path, collider_skeleton_path())),
    )
    messages: list[str] = []
    for sidecar_path, apply_metadata in targets:
        if not sidecar_path.is_file():
            messages.append(
                f'Missing Godot import sidecar "{sidecar_path}". Run Godot import for the '
                "generated .blend file, then rerun this helper or the character generator."
            )
            continue

        changed = apply_metadata(sidecar_path)
        action = "Updated" if changed else "Verified"
        messages.append(f'{action} Godot import retarget metadata in "{sidecar_path}".')

    return messages


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Apply MakeHuman retarget metadata to existing character .blend.import sidecars."
    )
    parser.add_argument("output_file", type=Path, help="Generated character .blend path.")
    parser.add_argument("name", help="Configured generated character name.")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    for message in update_existing_character_sidecars(args.output_file, args.name):
        print(message)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
