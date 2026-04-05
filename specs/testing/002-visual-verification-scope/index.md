---
id: TEST-002
title: Visual Verification Scope
---

# Visual Verification Scope

## Requirement

Provide AI agents with a practical, repeatable workflow for validating visual behaviour when logic assertions alone are
insufficient.

## Goal

Standardise a photobooth-based verification process where C# integration tests are the primary acceptance mechanism and
screenshots serve as a diagnostic aid.

## In Scope

- Feature-level visual checks tied to explicit spec expectations.
- Photobooth-based verification scenes under `@game/tests/<feature>/...`.
- Reuse of existing photobooth base scenes where applicable.
- Marker-based visual references that are visible in screenshots.
- Screenshot capture scripts that execute scenario states and produce reproducible outputs.
- Screenshot capture on test failure as a diagnostic aid for investigating assertion failures.
- C# integration tests that validate the same behaviour non-visually as the primary verification mechanism.

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
3. **Add C# Integration Coverage**
   - C# integration tests are the **primary** verification mechanism.
   - Load the same test scene in C# integration tests.
   - Assert non-visual equivalents (pole-target direction, bone positions, transform bounds).
   - When assertions fail, capture screenshots from relevant cameras using `Object.Call()` to invoke GDScript
     `Photobooth`/`CameraRig` methods.
4. **Run Feature Scenarios For Diagnostic Screenshots**
   - Use a runner script with the same base name as the test scene (`.tscn` + `.gd`).
   - Apply scenario states and capture screenshots with `Photobooth.capture_screenshots(...)`.
   - The GDScript runner produces multi-camera screenshots for all scenarios, but these serve as a **diagnostic aid**
     reviewed when C# tests fail — they are not the primary acceptance gate.

## Acceptance Criteria

1. Visual-verification tasks use the workflow contract above unless a spec explicitly overrides it.
2. Verification scenes are reusable, stored under `@game/tests/<feature>/...`, and based on an appropriate photobooth
   inheritance chain.
3. Camera/marker framing validation is completed before feature-level screenshot review.
4. C# non-visual assertions are the primary verification mechanism; screenshot evidence serves as a diagnostic aid when
   assertions fail.
5. Handoffs include test-scene path, runner script path, screenshot output location, and C# integration test location.

## References

- @specs/index.md
- @game/assets/testing/photobooth/photobooth.tscn
- @game/assets/characters/reference/female/photobooth/full_body_5_cams.tscn
- @docs/gdscript-api.md
