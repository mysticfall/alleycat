"""Pure path contract for the Kimodo locomotion bootstrap extraction artefacts.

This module deliberately imports no Blender code so the extraction contract can
be validated and unit-tested outside ``bpy``.  Five artefacts make up the
contract:

* ``kimodo_source`` — the canonical clean rig, repository-fixed.
* ``locomotion_template`` — the neutral locomotion template, repository-fixed.
* ``kimodo_retarget_setup`` — per-character, beneath the character asset root.
* ``locomotion_[variant]_authoring`` — per-character authoring workspace.
* ``locomotion_[variant]`` — per-character processed package.

The per-character animated idle sources (KIMODO-IDLE-002) are a sixth,
character-scoped authored input family derived by ``derive_idle_source_path``.
"""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Union

from .contracts import validate_variant

CANONICAL_SOURCE_RELATIVE = Path("game/assets/characters/animations/source/kimodo_source.blend")
NEUTRAL_TEMPLATE_RELATIVE = Path("game/assets/characters/animations/source/locomotion/locomotion_template.blend")
CHARACTER_SETUP_RELATIVE = Path("animations/source/kimodo_retarget_setup.blend")
IDLE_SOURCE_DIRECTORY_RELATIVE = Path("animations/source/idle")
AUTHORING_WORKSPACE_DIRECTORY_RELATIVE = Path("animations/source/locomotion")
PROCESSED_PACKAGE_DIRECTORY_RELATIVE = Path("animations/processed/locomotion")


class BootstrapPathError(ValueError):
    """Raised when the bootstrap artefact path contract is violated."""


def repository_root() -> Path:
    """Return the repository root that owns the two repository-fixed artefacts."""
    source = Path(__file__).resolve()
    for candidate in (source.parent, *source.parents):
        if (candidate / "AlleyCat.sln").is_file() and (candidate / "game/project.godot").is_file():
            return candidate
    raise BootstrapPathError(f"Could not locate the repository root from {source}.")


@dataclass(frozen=True)
class BootstrapArtefactPaths:
    """The five resolved artefact paths for one character variant."""

    canonical_source: Path
    neutral_template: Path
    character_setup: Path
    authoring_workspace: Path
    processed_package: Path
    variant: str
    character_asset_root: Path


def _character_asset_root(character_asset_root: Union[str, Path]) -> Path:
    if isinstance(character_asset_root, str):
        if not character_asset_root.strip():
            raise BootstrapPathError("Character asset root must not be empty.")
    elif not isinstance(character_asset_root, Path):
        raise BootstrapPathError(
            f"Character asset root must be a str or Path; got {type(character_asset_root).__name__}."
        )
    resolved = Path(character_asset_root).expanduser().resolve()
    if resolved == Path(resolved.anchor):
        raise BootstrapPathError(
            f"Character asset root must not be the filesystem root; got {resolved}."
        )
    return resolved


def derive_artefact_paths(
    character_asset_root: Union[str, Path],
    variant: str,
    repository_root_path: Path | None = None,
) -> BootstrapArtefactPaths:
    """Derive the five bootstrap artefact paths for ``variant`` of a character.

    The canonical source and neutral template are fixed relative to the
    repository root; the remaining three artefacts hang beneath the supplied
    character asset root.  Paths are derived only — existence is a later
    extraction-phase concern.
    """
    try:
        validated_variant = validate_variant(variant)
    except ValueError as error:
        raise BootstrapPathError(f"Invalid variant for bootstrap artefacts: {error}") from error
    root = _character_asset_root(character_asset_root)
    artefact_root = (
        repository_root()
        if repository_root_path is None
        else _character_asset_root(repository_root_path)
    )
    return BootstrapArtefactPaths(
        canonical_source=artefact_root / CANONICAL_SOURCE_RELATIVE,
        neutral_template=artefact_root / NEUTRAL_TEMPLATE_RELATIVE,
        character_setup=root / CHARACTER_SETUP_RELATIVE,
        authoring_workspace=(
            root / AUTHORING_WORKSPACE_DIRECTORY_RELATIVE / f"locomotion_{validated_variant}_authoring.blend"
        ),
        processed_package=(
            root / PROCESSED_PACKAGE_DIRECTORY_RELATIVE / f"locomotion_{validated_variant}.blend"
        ),
        variant=validated_variant,
        character_asset_root=root,
    )


def derive_idle_source_path(
    character_asset_root: Union[str, Path],
    variant: str,
) -> Path:
    """Derive the per-character animated idle-source path for ``variant``.

    Idle sources live at ``[character asset root]/animations/source/idle/
    <variant>.blend`` (KIMODO-IDLE-002); they are protected character-authored
    inputs the pipeline never modifies.  Paths are derived only — existence is
    a later idle-staging-phase concern.
    """
    try:
        validated_variant = validate_variant(variant)
    except ValueError as error:
        raise BootstrapPathError(f"Invalid variant for the idle source: {error}") from error
    return (
        _character_asset_root(character_asset_root)
        / IDLE_SOURCE_DIRECTORY_RELATIVE
        / f"{validated_variant}.blend"
    )
