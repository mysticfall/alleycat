"""Per-character Gate 1 baseline approval contract (KIMODO-CHAR-002)."""
from __future__ import annotations

import pytest

from kimodo_pipeline.character_baselines import (
    APPROVED_CHARACTER_BASELINES,
    BASELINE_INPUT_KEYS,
    CharacterBaselineError,
    resolve_approved_character,
    validate_baseline_entries,
)


FEMALE = APPROVED_CHARACTER_BASELINES["female"]
FEMALE_INPUTS = {
    "canonical_source": "a6bb259d8765b2ef794b24b8bec45ebafa61567ed92cbac4696e3d4b8436bacf",
    "neutral_template": "e67259db06edcc9d08dcd8292eb43fbcd9bc9ecfd430f1e82ddaa3552f3f06a4",
    "character_setup": "1a5e48c5cc4740715bb3e2808b66fc2fcad687f74319352819a93d9338ef3338",
}


def test_the_female_baseline_keeps_its_approved_gate_1_digests() -> None:
    assert set(APPROVED_CHARACTER_BASELINES) == {"female"}
    assert FEMALE == FEMALE_INPUTS


def test_approved_input_hashes_resolve_their_own_character() -> None:
    character, entry = resolve_approved_character(FEMALE_INPUTS)
    assert character == "female"
    assert entry == FEMALE


def test_an_unrecognised_setup_hash_is_refused_with_an_actionable_error() -> None:
    unknown = dict(FEMALE_INPUTS, character_setup="f" * 64)
    with pytest.raises(CharacterBaselineError) as error:
        resolve_approved_character(unknown)
    message = str(error.value)
    assert "f" * 64 in message
    assert "female" in message
    assert "adding its own baseline entry" in message


def test_repository_fixed_inputs_do_not_admit_a_drifted_character_setup() -> None:
    drifted = dict(FEMALE_INPUTS, canonical_source="0" * 64)
    with pytest.raises(CharacterBaselineError):
        resolve_approved_character(drifted)


def test_onboarding_a_character_adds_an_entry_without_touching_existing_ones() -> None:
    male_setup = "a" * 64
    approved = {
        "female": dict(FEMALE),
        "male": {**{key: FEMALE[key] for key in ("canonical_source", "neutral_template")},
                 "character_setup": male_setup},
    }
    male_inputs = dict(FEMALE_INPUTS, character_setup=male_setup)
    assert resolve_approved_character(male_inputs, approved)[0] == "male"
    assert resolve_approved_character(FEMALE_INPUTS, approved)[0] == "female"
    # The repository-approved female entry itself is untouched by the override.
    assert APPROVED_CHARACTER_BASELINES["female"] == FEMALE


def test_malformed_baseline_mappings_are_refused_structurally() -> None:
    with pytest.raises(CharacterBaselineError):
        validate_baseline_entries({})
    with pytest.raises(CharacterBaselineError):
        validate_baseline_entries({"female": {key: FEMALE[key] for key in BASELINE_INPUT_KEYS[:2]}})
    with pytest.raises(CharacterBaselineError):
        validate_baseline_entries({"female": {**FEMALE, "extra": "0" * 64}})
    with pytest.raises(CharacterBaselineError):
        validate_baseline_entries({"female": {**FEMALE, "character_setup": "not-a-digest"}})
    with pytest.raises(CharacterBaselineError):
        resolve_approved_character({"canonical_source": "0" * 64})
