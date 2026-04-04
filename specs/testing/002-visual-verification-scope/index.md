---
id: TEST-002
title: Visual Verification Scope
---

# Visual Verification Scope

## Requirement

Provide AI agents with a practical, repeatable workflow for validating visual behaviour when logic assertions alone are
insufficient.

## Goal

Standardise a photobooth-first verification process that produces reliable screenshot evidence and pairs it with
non-visual C# integration checks.

## In Scope

- Feature-level visual checks tied to explicit spec expectations.
- Photobooth-based verification scenes under `@game/tests/<feature>/...`.
- Reuse of existing photobooth base scenes where applicable.
- Marker-based visual references that are visible in screenshots.
- Screenshot capture scripts that execute scenario states and produce reproducible outputs.
- Follow-up C# integration tests that validate the same behaviour non-visually.

## Out Of Scope

- Subjective art-direction judgement (mood, atmosphere, style preference).
- Replacing existing logic/unit/integration tests that already validate behaviour sufficiently.
- One-off screenshot workflows that skip reusable test-scene setup.

## Workflow Contract

1. **Create A Test Scene (Photobooth)**
   - Inherit a suitable base under `@game/assets/...` when available.
   - Default base: `@game/assets/testing/photobooth/photobooth.tscn`.
   - Save feature verification scene under `@game/tests/<feature>/...`.
2. **Verify Cameras And Markers First**
   - Add markers via Photobooth API.
   - Capture per-camera framing screenshots before feature-level verification.
   - Confirm required subject regions and markers are visible for the intended checks.
3. **Run Feature Scenarios**
   - Use a runner script with the same base name as the test scene (`.tscn` + `.gd`).
   - Apply scenario states and capture screenshots with `Photobooth.capture_screenshots(...)`.
4. **Add C# Integration Coverage**
   - Load the same test scene in C# integration tests.
   - Assert non-visual equivalents (for example marker distances, transform bounds, node states).

## Acceptance Criteria

1. Visual-verification tasks use the workflow contract above unless a spec explicitly overrides it.
2. Verification scenes are reusable, stored under `@game/tests/<feature>/...`, and based on an appropriate photobooth
   inheritance chain.
3. Camera/marker framing validation is completed before feature-level screenshot review.
4. Screenshot evidence and C# non-visual assertions validate the same functionality.
5. Handoffs include test-scene path, runner script path, screenshot output location, and C# integration test location.

## References

- @specs/index.md
- @game/assets/testing/photobooth/photobooth.tscn
- @game/assets/characters/reference/female/photobooth/full_body_5_cams.tscn
- @docs/gdscript-api.md
