"""Pure ANIM-002 catalogue contracts, independent of Blender."""
from __future__ import annotations

from dataclasses import dataclass
import re


_VARIANT_PATTERN = re.compile(r"[a-z][a-z0-9]*(?:_[a-z0-9]+)*\Z")


@dataclass(frozen=True)
class RoleDeclaration:
    """The immutable ANIM-002 contract for one output role."""

    code: str
    segment_index: int
    is_loop: bool
    derivation: str = "native"
    requires_natural_recurrence: bool = False


@dataclass(frozen=True)
class RootProjectionPolicy:
    """Closed ANIM-002 Hips-to-Root projection for a role family.

    These declarations deliberately describe retained source components, rather
    than a convenient generated trajectory.  Keeping this beside the catalogue
    makes an unrecognised role a publication error instead of a fall-through.
    """

    family: str
    segment_index: int
    translation_axes: tuple[int, ...]
    promote_global_z_yaw: bool
    suppress: tuple[str, ...]
    reconstruct_full_hierarchy: bool = False
    straight_source_travel: bool = False
    loop_closure: str = "generated_recurrence"


def _roles(
    segment_index: int, is_loop: bool, *codes: str, derivation: str = "native",
    requires_natural_recurrence: bool = False,
) -> tuple[RoleDeclaration, ...]:
    return tuple(
        RoleDeclaration(code, segment_index, is_loop, derivation, requires_natural_recurrence)
        for code in codes
    )


ROLE_REGISTRY = (
    *_roles(0, True, "wf_r"), *_roles(0, True, "wf_l", derivation="mirror"),
    *_roles(0, True, "wb_r", derivation="reverse"), *_roles(0, True, "wb_l", derivation="reverse_mirror"),
    *_roles(4, True, "xr_l", "xr_r"), *_roles(4, True, "xl_l", "xl_r", derivation="mirror"),
    *_roles(1, True, "sr"), *_roles(1, True, "sl", derivation="temporal_reversal"),
    *_roles(5, True, "ar_l", "ar_r"), *_roles(5, True, "al_l", "al_r", derivation="mirror"),
    *_roles(2, True, "pr45", requires_natural_recurrence=True),
    *_roles(2, True, "pl45", derivation="mirror", requires_natural_recurrence=True),
    *_roles(0, False, "i2f_r", "f2i_r"), *_roles(0, False, "i2f_l", "f2i_l", derivation="mirror"),
    *_roles(0, False, "i2b_r", "b2i_r", derivation="reverse"), *_roles(0, False, "i2b_l", "b2i_l", derivation="reverse_mirror"),
)


ROOT_PROJECTION_POLICIES = (
    RootProjectionPolicy("walk", 0, (2,), False, ("R", "L", "V"), straight_source_travel=True),
    RootProjectionPolicy("walk_transition", 0, (2,), False, ("R", "L", "V"), straight_source_travel=True),
    RootProjectionPolicy(
        "precision_lateral", 1, (0,), False, ("R", "F", "V"),
        loop_closure="complete_event_repeat_offset",
    ),
    RootProjectionPolicy("pivot", 2, (), True, ("T", "P", "R", "L", "non-Z"), reconstruct_full_hierarchy=True),
    RootProjectionPolicy("strafe", 4, (0,), False, ("R", "F", "V"), straight_source_travel=True),
    RootProjectionPolicy("arc", 5, (0, 2), True, ("V", "P", "R", "off-curve")),
)

# Viewed from armature/global +Z, clockwise/right is negative and
# counter-clockwise/left is positive. Root local-Y has that same evaluated
# physical sign on the Kimodo Source rig.
PIVOT_ROOT_YAW_SIGNS = {"pr45": -1.0, "pl45": 1.0}


def pivot_root_yaw_sign(role_code: str) -> float:
    """Return the fixed Root-local-Y/global-Z semantic sign for a pivot role."""
    try:
        return PIVOT_ROOT_YAW_SIGNS[role_code]
    except KeyError as error:
        raise ValueError(f"unsupported pivot role: {role_code}") from error


def _policy_family(role: RoleDeclaration) -> str:
    code = role.code
    if code.startswith(("wf_", "wb_")):
        return "walk"
    if code.startswith(("i2f_", "f2i_", "i2b_", "b2i_")):
        return "walk_transition"
    if code in {"sl", "sr"}:
        return "precision_lateral"
    if code in {"pl45", "pr45"}:
        return "pivot"
    if code.startswith(("xl_", "xr_")):
        return "strafe"
    if code.startswith(("al_", "ar_")):
        return "arc"
    raise ValueError(f"uncovered ANIM-002 Root projection role: {code}")


def role_projection_policy(role: RoleDeclaration) -> RootProjectionPolicy:
    """Return the sole explicit policy applicable to ``role``."""
    family = _policy_family(role)
    matches = tuple(policy for policy in ROOT_PROJECTION_POLICIES if policy.family == family)
    if len(matches) != 1:
        raise ValueError(f"ambiguous ANIM-002 Root projection policy for {role.code}: {family}")
    policy = matches[0]
    if policy.segment_index != role.segment_index:
        raise ValueError(f"ANIM-002 Root policy segment mismatch for {role.code}")
    return policy


def _validate_root_projection_registry() -> None:
    covered = [role_projection_policy(role) for role in ROLE_REGISTRY]
    if len(covered) != len(ROLE_REGISTRY) or len({role.code for role in ROLE_REGISTRY}) != 24:
        raise ValueError("ANIM-002 Root projection policy does not cover the closed catalogue")


_validate_root_projection_registry()


def validate_variant(variant: str) -> str:
    """Validate and return a lower-case snake_case content variant key."""
    if not isinstance(variant, str) or not _VARIANT_PATTERN.fullmatch(variant):
        raise ValueError(
            "Variant must be a lower-case snake_case key beginning with a letter."
        )
    return variant


def action_name(variant: str, role: RoleDeclaration) -> str:
    """Return a Blender output Action name; only loops receive ``_loop``."""
    return f"loc_{validate_variant(variant)}_{role.code}" + ("_loop" if role.is_loop else "")


def asset_identity(variant: str, role: RoleDeclaration) -> str:
    """Return the external ANIM-002 identity, which never has ``_loop``."""
    return f"loc_{validate_variant(variant)}_{role.code}"


def transition_boundary_phase(role_code: str) -> tuple[str, str] | None:
    """Return the measured transition boundary and its leading foot."""
    form, separator, side = role_code.rpartition("_")
    if not separator or form not in {"i2f", "f2i", "i2b", "b2i"}:
        return None
    if side not in {"r", "l"}:
        raise ValueError(f"transition role has invalid anatomical suffix: {role_code}")
    # These are output boundaries.  Reversal changes which *source* edge is
    # selected, never the anatomical side named by the published Action.
    return ("end" if form in {"i2f", "i2b"} else "start", "Right" if side == "r" else "Left")
