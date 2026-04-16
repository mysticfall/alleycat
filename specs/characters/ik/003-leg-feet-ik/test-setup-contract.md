---
title: Leg-Feet IK Test Setup Contract
---

# Leg-Feet IK Test Setup Contract

## Purpose

Define the IK-003 verification-scene and test-harness contract, including lower-body photobooth inheritance and
`BoneAttachment3D` hips override behaviour.

## Contract Scope

- Verification scene inheritance and required file layout under `@game/tests/characters/ik/`.
- Required lower-body pose scenarios for visual and non-visual checks.
- Hips mobility harness using `BoneAttachment3D` override bone position without animation.
- Integration-test expectations aligned with IK-002-style validation flow.

## Verification Scene Basis

The IK-003 verification scene must inherit from:

- `@game/assets/characters/reference/female/photobooth/lower_body_5_cams.tscn`

Required test artefacts:

- Scene: `@game/tests/characters/ik/leg_feet_ik_test.tscn`
- Runner script (same base name): `@game/tests/characters/ik/leg_feet_ik_test.gd`

## Hips Override Harness

The verification scene must include a `BoneAttachment3D` bound to the hips bone and configured to allow explicit hips
position override without animation playback.

The harness exists to validate IK response when pelvis position is manually offset in test scenarios.

Contract requirements:

1. Hips offset is authored and applied directly through the `BoneAttachment3D` test harness.
2. Hips override works while animation does not drive the same transform.
3. Required pose scenarios include at least one non-neutral hips offset state.

## Required Pose Coverage

At minimum, test scenarios must cover:

- Neutral standing lower-body pose.
- Forward foot placement variation.
- Outward/inward foot rotation variation (to exercise forward-dot-driven forward/up axis interpolation).
- A hips-offset pose via `BoneAttachment3D` override.

Exact marker values are implementation-defined, but each scenario must have stable reproducible setup in scene data.

## Validation Workflow

### Visual Validation

- Runner performs capture scenarios following `@specs/testing/002-visual-verification-scope/index.md`.
- Captures must make knee bend-plane behaviour and foot correction behaviour observable from inherited cameras.

### Non-Visual Validation

A C# integration test must load the same scene and assert:

- Pole-direction continuity under continuous foot rotation changes.
- Solve-to-target runtime behaviour with provided foot targets consumed as goal inputs.
- Foot target transform immutability across IK runtime updates (targets remain read-only inputs).
- Stable behaviour under hips-override scenarios.

## Acceptance Criteria Coverage

This contract defines details for:

- AC-08
- AC-09
- AC-10
- AC-11

Source-of-truth criteria wording is maintained in [IK-003 Overview](index.md#acceptance-criteria).

## References

- [IK-003 Overview](index.md)
- [Leg-Feet IK Contract](leg-feet-ik-contract.md)
- @game/assets/characters/reference/female/photobooth/lower_body_5_cams.tscn
- @specs/testing/002-visual-verification-scope/index.md
