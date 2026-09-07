"""Portable contracts shared by the Kimodo-to-Female Blender processor.

This module deliberately has no ``bpy`` import so projection and naming rules
can be verified by the normal Python test runner.  The published-path contract
mirrors ``kimodo_pipeline.bootstrap_paths`` (which owns the authoritative
five-artefact derivation, including ``animations/processed/locomotion``); the
two are held equal by a dedicated cross-check test.
"""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path, PurePosixPath
import re


# Published packages belong beneath the character asset root; the reference
# Female is the canonical bake target.  Kimodo inputs are deliberately kept
# below the ignored animation source directory and must never be used as an
# import destination.
DEFAULT_CHARACTER_ASSET_ROOT = PurePosixPath("game/assets/characters/reference/female")
PROCESSED_DIRECTORY_RELATIVE = PurePosixPath("animations/processed")
PACKAGES = ("locomotion", "idle")

_VARIANT_PATTERN = re.compile(r"[a-z][a-z0-9]*(?:_[a-z0-9]+)*\Z")


def validate_variant(variant: str) -> str:
    """Validate and return a lower-case snake_case content variant key."""
    if not isinstance(variant, str) or not _VARIANT_PATTERN.fullmatch(variant):
        raise ValueError("Variant must be a lower-case snake_case key beginning with a letter.")
    return variant


def published_output_root(
    character_asset_root: str | Path | PurePosixPath = DEFAULT_CHARACTER_ASSET_ROOT,
    package: str = "locomotion",
) -> PurePosixPath:
    """Return the published package directory beneath a character asset root.

    Locomotion packages publish to ``[character asset root]/animations/processed
    /locomotion`` and idle packages to the parallel ``idle`` directory; the old
    ``processed/kimodo`` directory level is gone from new publications.
    """
    if package not in PACKAGES:
        raise ValueError(f"Unknown Kimodo package {package!r}; expected one of {sorted(PACKAGES)}.")
    root = PurePosixPath(character_asset_root)
    if str(root).startswith(("/", "\\")) or not str(root).strip(". /\\"):
        raise ValueError("The character asset root must be a non-empty relative path.")
    return root / PROCESSED_DIRECTORY_RELATIVE / PurePosixPath(package)


@dataclass(frozen=True)
class ExportMotion:
    """One published identity and the materialised action that supplies it."""

    role: str
    source_action: str
    internal_action: str
    external_identity: str
    is_loop: bool
    runtime_capability: str
    selected_side: str | None = None
    selection_rationale: str | None = None


ROLE_CODES = (
    "wf_r", "wf_l", "wb_r", "wb_l", "xr_l", "xr_r", "xl_l", "xl_r",
    "sr", "sl", "ar_l", "ar_r", "al_l", "al_r", "pr45", "pl45",
    "i2f_r", "f2i_r", "i2f_l", "f2i_l", "i2b_r", "b2i_r", "i2b_l", "b2i_l",
)
LOOP_ROLES = frozenset({
    "wf_r", "wf_l", "wb_r", "wb_l", "xr_l", "xr_r", "xl_l", "xl_r",
    "sr", "sl", "ar_l", "ar_r", "al_l", "al_r", "pr45", "pl45",
})
PROJECTED_SOURCE_ROLES = {"wf_r": "wf", "wb_r": "wb"}
OMITTED_SOURCE_ROLES = frozenset({"wf_l", "wb_l"})

RUNTIME_CAPABILITIES = {
	"idle": "idle_contact_free",
    "wf": "cyclic_forward_primary",
    "wb": "cyclic_backward_primary",
    "xl_l": "cyclic_lateral_left_left_lane",
    "xl_r": "cyclic_lateral_left_right_lane",
    "xr_l": "cyclic_lateral_right_left_lane",
    "xr_r": "cyclic_lateral_right_right_lane",
    "al_l": "cyclic_arc_left_left_lane",
    "al_r": "cyclic_arc_left_right_lane",
    "ar_l": "cyclic_arc_right_left_lane",
    "ar_r": "cyclic_arc_right_right_lane",
    "sl": "confined_side_step_left",
    "sr": "confined_side_step_right",
    "pl45": "confined_pivot_left",
    "pr45": "confined_pivot_right",
    "i2f_l": "entry_forward_left",
    "i2f_r": "entry_forward_right",
    "f2i_l": "exit_forward_left",
    "f2i_r": "exit_forward_right",
    "i2b_l": "entry_backward_left",
    "i2b_r": "entry_backward_right",
    "b2i_l": "exit_backward_left",
    "b2i_r": "exit_backward_right",
}


def runtime_capability(role: str) -> str:
    """Resolve the explicit runtime semantic capability authored for one export role."""
    try:
        return RUNTIME_CAPABILITIES[role]
    except KeyError as error:
        raise ValueError(f"Role {role!r} has no declared runtime capability.") from error


def locomotion_exports(variant: str) -> tuple[ExportMotion, ...]:
    """Return the closed 22-motion ANIM-002 projection in stable role order."""
    validate_variant(variant)
    result: list[ExportMotion] = []
    for source_role in ROLE_CODES:
        if source_role in OMITTED_SOURCE_ROLES:
            continue
        if source_role in PROJECTED_SOURCE_ROLES:
            role = PROJECTED_SOURCE_ROLES[source_role]
            rationale = "ANIM-002 temporary projection selects the materialised _r phase deterministically."
            selected_side: str | None = "r"
        else:
            role = source_role
            rationale = None
            selected_side = None
        is_loop = source_role in LOOP_ROLES
        source_action = f"loc_{variant}_{source_role}" + ("_loop" if is_loop else "")
        internal = f"loc_{variant}_{role}" + ("-loop" if is_loop else "")
        result.append(
            ExportMotion(
                role, source_action, internal, f"loc_{variant}_{role}", is_loop,
                runtime_capability(role), selected_side, rationale,
            )
        )
    if len(result) != 22 or len({item.external_identity for item in result}) != 22:
        raise AssertionError("ANIM-002 projection no longer produces exactly 22 unique identities.")
    return tuple(result)


def idle_export(variant: str, source_action: str = "kimodo_m0a3dyf0") -> ExportMotion:
    """Return the isolated provisional idle contract for one variant.

    The idle source action is an input, not a constant: the default names the
    legacy monolith-flow action, while workspace-flow callers pass the idle
    action of their authoring source explicitly.
    """
    validate_variant(variant)
    if not isinstance(source_action, str) or not source_action.strip():
        raise ValueError("The idle source action name must be a non-empty string.")
    return ExportMotion(
        "idle", source_action, f"idle_{variant}_loop", f"idle_{variant}", True,
        runtime_capability("idle"),
    )


def portable_source_name(path_name: str) -> str:
    """Return a provenance-safe source name, never a local filesystem path."""
    return path_name.rsplit("/", 1)[-1].rsplit("\\", 1)[-1]
