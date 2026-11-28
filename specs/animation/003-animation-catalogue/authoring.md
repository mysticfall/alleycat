---
title: Animation Clip Authoring
---

# Animation Clip Authoring

## Purpose

This page is the normative contract for producing [ANIM-003](index.md) idle clips. It is written for artists, tool
implementers and reviewers. Locomotion sets keep the reviewed-source ingestion of the
[Kimodo tooling contract](../../tooling/kimodo-pipeline/locomotive-animation-set.md#source-production-contract); the
catalogue only wraps a published set in an entry.

## Shared Output Contract

1. Every authoring path ends in a native Godot `Animation` clip on the reference character skeleton, whose bone tracks
   bind to `GeneralSkeleton`. No path publishes SOMA idle samples or MotionBricks idle data.
2. Tools produce clips only. Authors maintain [variant descriptors](variant-format.md), which the validator
   checks. Clip authoring tools never write or rewrite descriptors.
3. Authoring guidance:
   - an idle starts on a settled pose, because frame 0 and the frames after it are its transition target;
   - an idle loops cleanly;
   - an idle has no authored entry or exit markers.
4. Clips recorded from runtime poses divide bone positions by the skeleton's `Skeleton3D.motion_scale` before writing
   them.
5. Preview happens in Blender. The Godot check is the existing actual-rig verification under [ANIM](../index.md) TR4,
   whose native playback remains the visual authority.
6. Authored sources are protected inputs and are never modified by tools. Clip output is staged and validated before
   it replaces a published clip, so a failure leaves prior output intact.
7. Store the built-in playable clip and its descriptor in `game/assets/animations/idle/<variant>/` beside its
   ignored `source/` child. The runtime must not load its authoring input.

## Kimodo Path

1. Author on the SOMA `Kimodo_Source` rig in a verified copy of the character's calibrated setup, under
   [KIMODO-SET-001–002][kimodo-set].
   The animated input lives at `idle/<variant>/source/<variant>.blend` under the built-in animation asset root,
   separate from the shared character setup. [KIMODO-SRC-006][kimodo-sources] defines bootstrap path derivation;
   the clip exporter receives the animated input explicitly.
2. Preview on the retargeted, meshed reference rig in that setup copy. Bake the same retargeted motion onto the
   reference skeleton and export it as the native clip. Preview and export share one retarget mechanism, so the
   exported clip matches what the author reviewed.
3. Export binds the animated source action, including slotted actions, and keeps frame-varying motion through the
   exported range. It does not take a clip list hardcoded in the tool.
4. The setup may apply an arbitrary uniform scale to the reference model, to make preview comparison with the SOMA
   skeleton easier. That scale is a preview convenience, not a contract value. Export must restore it, so the native
   clip matches the scale of the imported Godot character.

## Rigify Path

1. Author with a Rigify (or equivalent) control rig built over the reference skeleton.
2. Bake the deform bones to the reference skeleton and export them as the native clip. The control rig is not
   exported.

[kimodo-set]: ../../tooling/kimodo-pipeline/locomotive-animation-set.md#character-setup-and-calibration-contract
[kimodo-sources]: ../../tooling/kimodo-pipeline/locomotive-animation-set.md#repository-source-asset-contract

## Acceptance Criteria

1. **ANIM-003-AUTH-AC-001:** Each path produces a native clip that binds to the reference skeleton. The sources stay
   byte-unchanged, and a failed export leaves prior output intact. Verify the shipped idle loads from its
   category-first path with the adjacent authoring input excluded from Godot import.
2. **ANIM-003-AUTH-AC-002:** For the Kimodo path, the exported clip matches the Blender retargeted preview, because
   both come from one mechanism. Any preview scale on the setup's reference model is restored, so the clip matches
   the imported character's scale. Runtime-recorded clips apply the `motion_scale` rule.
3. **ANIM-003-AUTH-AC-003:** No clip authoring tool writes descriptors or SOMA idle data. The `sad` clip plays
   on both reference skeletons in native verification.
