"""Stable anatomical character-scale contracts for Kimodo rigs."""
from __future__ import annotations

import math
import re
from typing import Iterable


ANATOMICAL_SCALE_PROCEDURE = "maximum_deforming_motion_bone_rest_length"
_NON_MOTION_TOKENS = frozenset({
    "CONTROL", "CTRL", "CTL", "HELPER", "MCH", "ORG", "RET", "LOOK",
    "TARGET", "POLE", "IK", "FK",
})
_NON_MOTION_PREFIXES = ("CONTROL", "CTRL", "CTL", "HELPER", "MCH", "ORG")


class AnatomicalScaleError(ValueError):
    """The rig does not expose a usable anatomical motion-bone scale."""


def is_anatomical_motion_bone(name: str, use_deform: bool) -> bool:
    """Identify anatomy by deform status and rig-control naming conventions.

    Root is trajectory infrastructure rather than anatomy. Control and helper
    bones are excluded by semantic name tokens, including Kimodo's ``*_RET``
    and ``*_LOOK`` conventions, rather than by a fixture-specific bone list.
    """
    if not use_deform or name.casefold() == "root":
        return False
    tokenised = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", name)
    tokens = {token for token in re.split(r"[^A-Za-z0-9]+", tokenised.upper()) if token}
    upper_name = name.upper()
    return not (
        bool(tokens & _NON_MOTION_TOKENS)
        or upper_name.startswith(_NON_MOTION_PREFIXES)
    )


def anatomical_character_scale(bones: Iterable[tuple[str, float, bool]]) -> float:
    """Return the longest valid anatomical motion-bone rest length.

    The maximum anatomical segment is deterministic, proportional under a
    uniform rig resize, and insensitive to Root/control/helper dimensions.
    """
    lengths = [
        float(length)
        for name, length, use_deform in bones
        if is_anatomical_motion_bone(name, use_deform)
        and math.isfinite(length)
        and length > 1e-12
    ]
    if not lengths:
        raise AnatomicalScaleError(
            "rig has no valid deforming anatomical motion bones for character scale"
        )
    return max(lengths)
