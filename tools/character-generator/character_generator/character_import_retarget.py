#!/usr/bin/env python3
"""Guard: character Blender import sidecars must carry MakeHuman retarget metadata.

A character whose `.blend.import` sidecars lack the MakeHuman retarget keys imports
with MakeHuman bone names, and the runtime rig then skips every collider bone
attachment with a warning. This check validates existing main and collider sidecars
so that state fails loudly instead of shipping. Targets may be character
directories, specific character `.blend` or `.blend.import` paths, or pack and
content roots scanned recursively for paired `<stem>.colliders.blend` sources,
which distinguish generated characters from animation and prop `.blend` assets
that share their directories.
"""

from __future__ import annotations

import argparse
import os
import sys
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

from .update_character_import_retarget_metadata import (
    _existing_skeleton_node_paths,
    _find_keyed_dictionary,
    _find_subresources_value,
    collider_skeleton_path,
    derive_character_import_sidecars,
)


REQUIRED_RETARGET_KEYS = (
    "retarget/bone_map",
    "retarget/rest_fixer/fix_silhouette/enable",
    "retarget/rest_fixer/fix_silhouette/filter",
)
COLLIDER_BLEND_SUFFIX = ".colliders.blend"
IMPORT_SUFFIX = ".import"
RETARGET_HELPER_COMMAND = "python3 tools/character-generator/commands/update_character_import_retarget_metadata.py"


def repository_root() -> Path:
    """Locate the repository root without assuming an implementation-file depth."""

    for candidate in Path(__file__).resolve().parents:
        if (candidate / ".git").exists():
            return candidate

    raise RuntimeError("Could not locate the repository root from the character-generator tool.")


REPO_ROOT = repository_root()


def display_path(path: Path) -> str:
    """Prefer repository-relative display paths in check output."""

    try:
        return str(path.relative_to(REPO_ROOT))
    except ValueError:
        return str(path)


@dataclass(frozen=True)
class SidecarCheck:
    """Per-file validation outcome for one character import sidecar."""

    sidecar_path: Path
    errors: tuple[str, ...] = ()

    @property
    def passed(self) -> bool:
        return not self.errors


def subresources_text(sidecar_text: str) -> str:
    """Return the `_subresources` dictionary text; raise ValueError when absent."""

    value = _find_subresources_value(sidecar_text)
    if value is None:
        raise ValueError("Could not locate `_subresources`.")

    value_start, value_end, _had_leading_space = value
    return sidecar_text[value_start:value_end]


def _entry_text(host_text: str, entry: tuple[int, int, bool]) -> str:
    """Return one keyed dictionary entry's text without its trailing comma."""

    start, end, has_comma = entry
    block = host_text[start:end]
    return block[:-1] if has_comma else block


def _nodes_text(subresources: str) -> str | None:
    """Return the `_subresources` `nodes` dictionary text, or None when absent."""

    nodes_entry = _find_keyed_dictionary(subresources, "nodes")
    if nodes_entry is None:
        return None
    return _entry_text(subresources, nodes_entry)


def missing_retarget_keys(entry_text: str) -> list[str]:
    """Return the required retarget keys absent from one skeleton node entry."""

    return [key for key in REQUIRED_RETARGET_KEYS if f'"{key}":' not in entry_text]


def main_sidecar_errors(sidecar_text: str) -> list[str]:
    """Check every existing skeleton node entry in a main character sidecar."""

    try:
        subresources = subresources_text(sidecar_text)
    except ValueError as error:
        return [str(error)]

    nodes = _nodes_text(subresources)
    if nodes is None:
        return []

    errors: list[str] = []
    for skeleton_path in sorted(_existing_skeleton_node_paths(subresources)):
        entry = _find_keyed_dictionary(nodes, skeleton_path)
        if entry is None:
            continue
        missing = missing_retarget_keys(_entry_text(nodes, entry))
        if missing:
            errors.append(
                f'Skeleton entry "{skeleton_path}" is missing required retarget keys: '
                + ", ".join(missing)
                + "."
            )
    return errors


def collider_sidecar_errors(sidecar_text: str) -> list[str]:
    """Check the Ragdoll skeleton entry in a collider sidecar."""

    skeleton_path = collider_skeleton_path()
    try:
        subresources = subresources_text(sidecar_text)
    except ValueError as error:
        return [str(error)]

    nodes = _nodes_text(subresources)
    entry = None if nodes is None else _find_keyed_dictionary(nodes, skeleton_path)
    if entry is None:
        return [f'Required skeleton entry "{skeleton_path}" is absent.']
    missing = missing_retarget_keys(_entry_text(nodes, entry))
    if missing:
        return [
            f'Skeleton entry "{skeleton_path}" is missing required retarget keys: '
            + ", ".join(missing)
            + "."
        ]
    return []


def _missing_sidecar_error(
    sidecar_path: Path,
    character_blend_path: Path,
    character_name: str,
) -> str:
    return (
        f'Missing Godot import sidecar "{display_path(sidecar_path)}". Run Godot import for '
        f"the character .blend file, then run {RETARGET_HELPER_COMMAND} "
        f"{display_path(character_blend_path)} {character_name}, then reimport in Godot."
    )


def _retarget_remedy(character_blend_path: Path, character_name: str) -> str:
    return (
        f"Run {RETARGET_HELPER_COMMAND} {display_path(character_blend_path)} "
        f"{character_name}, then reimport in Godot."
    )


def _check_sidecar(
    sidecar_path: Path,
    collect_errors: Callable[[str], list[str]],
    character_blend_path: Path,
    character_name: str,
) -> SidecarCheck:
    if not sidecar_path.is_file():
        return SidecarCheck(
            sidecar_path=sidecar_path,
            errors=(_missing_sidecar_error(sidecar_path, character_blend_path, character_name),),
        )

    errors = collect_errors(sidecar_path.read_text(encoding="utf-8"))
    remedy = _retarget_remedy(character_blend_path, character_name)
    return SidecarCheck(
        sidecar_path=sidecar_path,
        errors=tuple(f"{error} {remedy}" for error in errors),
    )


def check_character_sidecars(main_blend_path: Path) -> list[SidecarCheck]:
    """Validate the main and collider import sidecars of one generated character."""

    sidecars = derive_character_import_sidecars(main_blend_path)
    character_name = main_blend_path.stem
    collider_blend_path = sidecars.colliders.with_name(
        sidecars.colliders.name.removesuffix(IMPORT_SUFFIX)
    )

    checks = [
        _check_sidecar(
            sidecars.main,
            main_sidecar_errors,
            main_blend_path,
            character_name,
        )
    ]
    if collider_blend_path.is_file():
        checks.append(
            _check_sidecar(
                sidecars.colliders,
                collider_sidecar_errors,
                main_blend_path,
                character_name,
            )
        )
    return checks


def _main_blend_for_collider_blend(collider_blend_path: Path) -> Path:
    stem = collider_blend_path.name.removesuffix(COLLIDER_BLEND_SUFFIX)
    return collider_blend_path.with_name(f"{stem}.blend")


def _character_blend_paths_for_file(path: Path) -> list[Path]:
    name = path.name
    if name.endswith(COLLIDER_BLEND_SUFFIX + IMPORT_SUFFIX):
        return [_main_blend_for_collider_blend(path.with_name(name.removesuffix(IMPORT_SUFFIX)))]
    if name.endswith(COLLIDER_BLEND_SUFFIX):
        return [_main_blend_for_collider_blend(path)]
    if name.endswith(".blend" + IMPORT_SUFFIX):
        return [path.with_name(name.removesuffix(IMPORT_SUFFIX))]
    if name.endswith(".blend"):
        return [path]
    raise ValueError(
        f'Unsupported check target "{display_path(path)}"; expected a character directory, '
        "a character .blend, or a .blend.import sidecar path."
    )


def _iter_collider_blend_paths(root: Path) -> set[Path]:
    """Walk a target root for paired collider sources, following directory symlinks once."""

    collider_blends: set[Path] = set()
    visited_real_roots: set[str] = set()
    for current, directories, files in os.walk(root, followlinks=True):
        current_path = Path(current)
        real_root = str(current_path.resolve())
        if real_root in visited_real_roots:
            directories[:] = []
            continue
        visited_real_roots.add(real_root)

        directories[:] = sorted(
            directory for directory in directories if directory != ".godot"
        )
        for file in sorted(files):
            if file.endswith(COLLIDER_BLEND_SUFFIX):
                collider_blends.add(current_path / file)
    return collider_blends


def collect_character_blend_paths(target: Path) -> list[Path]:
    """Resolve one target path to the sorted main character .blend paths it covers."""

    if target.is_file():
        return sorted(set(_character_blend_paths_for_file(target)))

    if target.is_dir():
        return sorted(
            _main_blend_for_collider_blend(collider_blend)
            for collider_blend in _iter_collider_blend_paths(target)
        )

    raise ValueError(
        f'Unsupported check target "{display_path(target)}"; expected a character directory, '
        "a character .blend, or a .blend.import sidecar path."
    )


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Check character .blend.import sidecars for required MakeHuman retarget metadata."
    )
    parser.add_argument(
        "targets",
        nargs="+",
        type=Path,
        help=(
            "Character directories, character .blend or .blend.import paths, or pack and "
            "content roots scanned recursively for paired .colliders.blend sources."
        ),
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv if argv is not None else sys.argv[1:])

    blend_paths: set[Path] = set()
    for target in args.targets:
        try:
            blend_paths.update(collect_character_blend_paths(target))
        except ValueError as error:
            print(f"ERROR: {error}")
            return 2

    checks: list[SidecarCheck] = []
    for blend_path in sorted(blend_paths):
        checks.extend(check_character_sidecars(blend_path))

    for check in checks:
        status = "PASS" if check.passed else "FAIL"
        print(f"{status} {display_path(check.sidecar_path)}")
        for error in check.errors:
            print(f"  - {error}")

    failed_count = sum(1 for check in checks if not check.passed)
    print(
        f"Checked {len(checks)} character import sidecars: "
        f"{len(checks) - failed_count} passed, {failed_count} failed."
    )
    return 1 if failed_count else 0


if __name__ == "__main__":
    raise SystemExit(main())
