---
title: Leg-Feet IK Test Setup Contract
---

# Leg-Feet IK Test Setup Contract

## Requirement

Define the IK-003 verification-scene and test-harness contract for leg-feet inverse kinematics validation.

## Goal

Enable repeatable verification of leg-feet IK behaviour through a photobooth-based test scene with hips-override
capability and defined pose scenarios.

## User Requirements

Visual validation must make knee bend-plane behaviour and foot correction behaviour observable from inherited
cameras.

## Technical Requirements

### Verification Scene Basis

The IK-003 verification scene must inherit from `@game/assets/testing/photobooth/templates/lower_body_5_cams.tscn`.

Required test artefacts:

- Scene: `@game/tests/ik/leg_feet_ik_test.tscn`
- Runner script: `@game/tests/ik/leg_feet_ik_test.gd`

### Hips Override Harness

The verification scene must include a `BoneAttachment3D` bound to the hips bone, configured to allow explicit
position override without animation playback.

Contract requirements:

1. Hips offset is authored and applied directly through the `BoneAttachment3D` test harness.
2. Hips override works while animation does not drive the same transform.
3. Required pose scenarios include at least one non-neutral hips offset state.

### Required Pose Coverage

Test scenarios must cover:

1. Neutral standing lower-body pose (primary geometric method).
2. Forward foot placement variation.
3. Outward/inward foot rotation variation (forward-dot-driven forward/up axis interpolation during fallback).
4. Hips-offset pose via `BoneAttachment3D` override (geometric method under different geometry).
5. Degenerate pose scenario: triggers fallback mode (animation-derived direction too short or near-zero).

Marker values are implementation-defined; each scenario must have stable reproducible setup.

### Non-Visual Validation

A C# integration test must load the scene and assert:

- Pole-direction continuity under continuous foot rotation changes.
- Solve-to-target runtime behaviour with foot targets consumed as goal inputs.
- Foot target transform immutability across IK runtime updates (targets remain read-only).
- Stable behaviour under hips-override scenarios.
- Deterministic corner-case response to degenerate animation geometries.

## In Scope

- Verification scene inheritance from lower-body photobooth template.
- Lower-body pose scenarios for visual and non-visual checks.
- Hips mobility harness using `BoneAttachment3D` override.
- Integration-test expectations aligned with IK-002-style validation flow.

## Out Of Scope

- Specific threshold values for IK solver parameters.
- Runtime performance benchmarking.
- Character animation authoring beyond test pose scenarios.

## Acceptance Criteria

This contract defines details for:

- AC-08
- AC-09
- AC-10
- AC-11 (visual: stable knee/foot behaviour verification)
- AC-12 (non-visual: continuity, read-only targets, hips override, corner-case response)

Source-of-truth criteria wording is maintained in [IK-003 Overview](index.md#acceptance-criteria).

## References

- [IK-003 Overview](index.md)
- [Leg-Feet IK Contract](leg-feet-ik-contract.md)
- @game/assets/testing/photobooth/templates/lower_body_5_cams.tscn
- @specs/testing/002-visual-verification-scope/index.md