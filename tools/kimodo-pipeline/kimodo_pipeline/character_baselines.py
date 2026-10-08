"""Per-character Gate 1 baseline approval for the Kimodo bootstrap inputs.

Gate 1 baseline approval is per character (KIMODO-CHAR-002): onboarding a new
character adds that character's own baseline entry, and an existing
character's entry is never edited to admit another character.  This module is
deliberately free of ``bpy`` so the approval contract is unit-testable outside
Blender; ``bootstrap_workspace.py`` enforces it around every composition run.
"""
from __future__ import annotations

from typing import Mapping

BASELINE_INPUT_KEYS = ("canonical_source", "neutral_template", "character_setup")

# The approved Gate 1 baselines: the user-inspected bytes of the three
# bootstrap inputs for each onboarded character.  The canonical source and the
# neutral locomotion template are repository-fixed and therefore shared by
# every character entry; the character setup digest is the per-character
# approval.  Any drift (most plausibly an accidental re-run of
# extract_bootstrap.py) aborts workspace composition until the affected
# character's baseline is re-approved as a deliberate action.
APPROVED_CHARACTER_BASELINES: dict[str, dict[str, str]] = {
    "female": {
        "canonical_source": "a6bb259d8765b2ef794b24b8bec45ebafa61567ed92cbac4696e3d4b8436bacf",
        "neutral_template": "e67259db06edcc9d08dcd8292eb43fbcd9bc9ecfd430f1e82ddaa3552f3f06a4",
        "character_setup": "1a5e48c5cc4740715bb3e2808b66fc2fcad687f74319352819a93d9338ef3338",
    },
}


class CharacterBaselineError(ValueError):
    """Raised when bootstrap input bytes match no approved character baseline."""


def validate_baseline_entries(
    baselines: Mapping[str, Mapping[str, str]],
) -> None:
    """Reject a per-character baseline mapping whose entries are incomplete.

    Every character entry must name exactly the three bootstrap inputs with a
    64-character hexadecimal digest, so a mistyped override fails as a
    structural error instead of silently approving unknown bytes.
    """
    if not isinstance(baselines, Mapping) or not baselines:
        raise CharacterBaselineError(
            "Approved character baselines must be a non-empty mapping of character "
            "name to baseline entry."
        )
    for character, entry in baselines.items():
        if not isinstance(character, str) or not character.strip():
            raise CharacterBaselineError(
                f"Approved character baseline keys must be non-empty strings; got {character!r}."
            )
        if not isinstance(entry, Mapping):
            raise CharacterBaselineError(
                f"Approved character baseline {character!r} must be a mapping of input "
                "name to digest."
            )
        missing = [key for key in BASELINE_INPUT_KEYS if key not in entry]
        unexpected = sorted(set(entry) - set(BASELINE_INPUT_KEYS))
        malformed = sorted(
            key
            for key, value in entry.items()
            if not isinstance(value, str)
            or len(value) != 64
            or any(symbol not in "0123456789abcdef" for symbol in value.lower())
        )
        if missing or unexpected or malformed:
            raise CharacterBaselineError(
                f"Approved character baseline {character!r} is malformed: "
                f"missing={missing}, unexpected={unexpected}, non-digest={malformed}."
            )


def resolve_approved_character(
    hashes: Mapping[str, str],
    approved: Mapping[str, Mapping[str, str]] | None = None,
) -> tuple[str, dict[str, str]]:
    """Return the character whose approved baseline matches the input digests.

    Approval binds bytes, not intent: composition proceeds only when all three
    bootstrap inputs match one character's entry exactly.  Unknown setup bytes
    are refused with the unrecognised digest so onboarding a new character is
    an explicit approval action rather than an accidental pass-through.
    """
    baselines = APPROVED_CHARACTER_BASELINES if approved is None else dict(approved)
    validate_baseline_entries(baselines)
    missing = [key for key in BASELINE_INPUT_KEYS if key not in hashes]
    if missing:
        raise CharacterBaselineError(
            f"Baseline verification requires every bootstrap input digest; missing {missing}."
        )
    for character, entry in baselines.items():
        if all(hashes[key] == entry[key] for key in BASELINE_INPUT_KEYS):
            return character, dict(entry)
    raise CharacterBaselineError(
        "The bootstrap inputs match no approved character baseline "
        "(canonical_source="
        f"{hashes['canonical_source']}, neutral_template={hashes['neutral_template']}, "
        f"character_setup={hashes['character_setup']}); approved characters: "
        f"{sorted(baselines)}. Onboarding a new character requires adding its own "
        "baseline entry; an existing character's baseline is never edited to admit "
        "another character."
    )
