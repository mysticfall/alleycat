"""Focused non-Blender tests for the Kimodo retargeting contract."""
from pathlib import Path

import pytest

from kimodo_pipeline.retargeting import idle_export, locomotion_exports, portable_source_name, published_output_root, runtime_capability
from kimodo_pipeline.bootstrap_paths import derive_artefact_paths, repository_root


ROLE_SCHEMA = (
    "wf", "wb", "xl_l", "xl_r", "xr_l", "xr_r", "sl", "sr", "al_l", "al_r", "ar_l", "ar_r",
    "pl45", "pr45", "i2f_l", "i2f_r", "i2b_l", "i2b_r", "f2i_l", "f2i_r", "b2i_l", "b2i_r",
)


def test_locomotion_projection_is_closed_and_suffix_free() -> None:
    motions = locomotion_exports("ember")
    assert {item.role for item in motions} == set(ROLE_SCHEMA)
    assert all(not item.external_identity.endswith(("_loop", "-loop")) for item in motions)
    assert next(item for item in motions if item.role == "wf").source_action == "loc_ember_wf_r_loop"
    assert next(item for item in motions if item.role == "wb").source_action == "loc_ember_wb_r_loop"
    assert next(item for item in motions if item.role == "wf").internal_action == "loc_ember_wf-loop"


def test_locomotion_projection_declares_unique_runtime_capabilities() -> None:
    capabilities = [runtime_capability(item.role) for item in locomotion_exports("ember")]
    assert len(capabilities) == len(set(capabilities)) == 22
    with pytest.raises(ValueError, match="no declared runtime capability"):
        runtime_capability("opaque_unknown_role")


def test_locomotion_projection_is_variant_parameterised() -> None:
    motions = locomotion_exports("calm")
    assert {item.role for item in motions} == set(ROLE_SCHEMA)
    walk = next(item for item in motions if item.role == "wf")
    assert walk.source_action == "loc_calm_wf_r_loop"
    assert walk.internal_action == "loc_calm_wf-loop"
    assert walk.external_identity == "loc_calm_wf"
    with pytest.raises(ValueError):
        locomotion_exports("Ember")


def test_idle_contract_is_parameterised_and_portable() -> None:
    idle = idle_export("ember", "fixture_idle")
    assert runtime_capability(idle.role) == "idle_contact_free"
    assert (idle.source_action, idle.internal_action, idle.external_identity) == (
        "fixture_idle", "idle_ember_loop", "idle_ember",
    )
    explicit = idle_export("calm", "kimodo_workspace_idle")
    assert (explicit.source_action, explicit.internal_action, explicit.external_identity) == (
        "kimodo_workspace_idle", "idle_calm_loop", "idle_calm",
    )
    with pytest.raises(ValueError):
        idle_export("calm", "   ")
    assert portable_source_name("/unshareable/work/donor_idle.blend") == "donor_idle.blend"


def test_published_output_is_character_root_derived_and_outside_the_ignored_source_tree() -> None:
    ignored_source = Path("game/assets/characters/animations/source")
    female_root = Path("game/assets/characters/reference/female")
    output = Path(published_output_root(female_root))
    assert output == Path("game/assets/characters/reference/female/animations/processed/locomotion")
    assert Path(published_output_root(female_root, package="idle")) == Path(
        "game/assets/characters/reference/female/animations/processed/idle"
    )
    assert ignored_source not in (output, *output.parents)
    # Publications never target the retired processed/kimodo tree.
    assert "kimodo" not in output.parts
    with pytest.raises(ValueError):
        published_output_root(female_root, package="bogus")
    # The character asset root is explicit; the contract has no default character.
    with pytest.raises(TypeError):
        published_output_root()  # type: ignore[call-arg]


def test_published_output_matches_the_bootstrap_artefact_contract() -> None:
    for character in ("reference/female", "reference/male"):
        character_root = Path("game/assets/characters") / character
        artefacts = derive_artefact_paths(character_root, "ember")
        assert repository_root() / published_output_root(character_root) == artefacts.processed_package.parent
