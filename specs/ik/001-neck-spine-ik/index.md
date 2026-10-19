---
id: IK-001
title: Neck-Spine CCDIK Configuration
---

# Neck-Spine CCDIK Configuration

## Requirement

Provide character-local Godot `CCDIK3D` configurations that adjust neck-spine bones towards a
head target with constrained, natural-looking motion.

## Goal

Define a spec-first, testable contract for neck-spine IK authored locally in every supported
base character template.

## User Requirements

1. Players should see neck and spine motion that follows head intent without implausible
   twisting.
2. Neck/spine behaviour should remain visually stable across representative head-target
   poses.
3. All characters share the same neck-spine rotational limits, so constrained looking
   behaviour — including stooping at low targets — reads consistently across characters
   rather than varying by rig.
4. A downward head target produces a distributed stoop — the bend spreads across the neck
   and upper spine — instead of concentrating rotation at the skull base.

## Technical Requirements

1. Every supported base character template must locally author a `NeckSpineIK` `CCDIK3D` node
   with the required target binding, five-joint chain, and explicit joint constraints.
2. Each local configuration must bind to its template's designated head-solve target and
   resolve the canonical `Spine`-to-`Head` chain for its own generated skeleton. Cached
   `root_bone`/`end_bone` indices are authored per template alongside canonical bone-name
   keys; templates must not share a scene-level CCDIK definition because bone indices differ
   between skeletons.
3. The chain must run from the canonical `Spine` root bone to the canonical `Head` end bone,
   contain exactly five joints, and preserve its serialised per-joint constraint configuration —
   every joint explicit, with the Head end-bone joint's null limitation authored explicitly.
4. Verification must exercise the locally authored configurations through the head-hips
   photobooth verification flow and C# non-visual integration assertions; it must not depend
   on a separate shared IK scene. Integration assertions must pin the engine's root-to-end
   joint-to-bone order per joint by bone name — for example through `get_joint_bone_name` —
   so a silent joint-order reversal cannot regress undetected.
5. Every supported base character template authors an identical per-joint constraint set for
   its `NeckSpineIK` chain: the same rotation axes, cone angles, and limitation frames — right
   axes and rotation offsets included — serialised identically across templates.
   Per-template variation remains only where the generated skeleton demands it (cached bone
   indices and target bindings), never in the constraint values.
6. The `CCDIK3D` joint list serialises root-to-end: joint indices 0–4 map to the bones `Spine`,
   `Chest`, `UpperChest`, `Neck`, `Head` for this chain. `JointLimitationCone3D.angle` is the
   full cone aperture, so the enforced per-joint deviation cap is half the authored angle.
   Final joint-limit and hinge-deviation acceptance measurements are taken against each joint's
   authored local rest pose — using same-stage final parent/child transforms and the authored
   local rest transform — including during animation, never against the incoming animation pose;
   this acceptance reference does not redefine the engine's native incoming-pose solver
   semantics. The unified constraint set all templates author is (joints in serialised
   root-to-end order):

   | Joint | Rotation Axis | Cone Aperture | Deviation Cap | Limitation Frame |
   |---|---|---|---|---|
   | Spine | X | 16° | ±8° | shared convention, identity offsets |
   | Chest | X | 16° | ±8° | shared convention, identity offsets |
   | UpperChest | ALL | 28° | ±14° | shared convention, identity offsets |
   | Neck | ALL | 32° | ±16° | shared convention, identity offsets |
   | Head | X | none — null limitation | — | — |

   The Spine cap is mandatory. The Head end-bone joint keeps a null limitation and is inert
   because the chain does not extend the end bone. Preserve these apertures, identity limitation
   frames, cross-template equality and root-to-end bone-name pins during AI-009 reference integration.
   Cones are tunable design limits and safety bounds — not distribution weights, allocation controls
   or proof of natural anatomy. Final joint-limit and off-axis hinge acceptance checks apply the
   user-approved Provisional Playtest Acceptance Profile, which separates the authored constraint from
   the allowed observed deviation: authored deviation caps remain 8°/8°/14°/16°, while the allowed
   inclusive rest-relative deviations are Spine/Chest swing to cap+3°, UpperChest swing to an absolute
   inclusive 16° observed bound fixed independently of its unchanged 14° authored cap, Neck swing to
   cap+0.1° (16.1°) and Spine/Chest off-X hinge to 1°. The 16° bound reclassifies observed excursions
   as accepted for playtesting; it does not claim those excursions disappeared or that the authored
   cap moved. These bounds are a measurement allowance for
   playtest acceptance of the current implementation — never authored-domain expansion, solver slack,
   an anatomical claim or a numerical-precision proof — and larger excursions remain failures. The
   authored local rest pose stays the measurement reference, including during animation. The executable
   profile home is `integration-tests/src/Mind/Attention/DownwardFoldAngularAcceptance.cs`; AI-009's
   demand-window Head budgets live in
   [its Acceptance Criteria](../../ai/009-attention-head-orientation/index.md#acceptance-criteria).
   If the authored cones fail the required anatomy/reach outcomes, stop for a user decision supported
   by measurements; neither further retuning nor weakening acceptance is authorised by a failed fixture.
7. Every supported base character template authors a `NeckPivot` marker at the neck
   articulation, parented to the Neck bone node in the same way `Viewpoint` hangs under the
   Head node. The marker is the pivot of AI-009's pitch-orbit displacement lever and yaw
   transport — the neutral, upward and lateral regime publishes the orbit construction about it
   and the downward fold-aware regime blends and yaw-transports about it; authoring it
   in the base templates keeps positional head-intent drive available to every character.
   For AI-009, resolve its authored offset against the upstream Neck bone pose, not the marker's
   solved attachment global transform. Capture upstream Head and Neck/pivot, and the animation-owned
   chain poses AI-009's prediction frame re-anchors onto, at the existing CharacterIK Begin boundary
   before head-target evaluation, through the head provider's optional Begin-evaluation capability
   owned by CharacterIK: no IK-to-Mind dependency, and other providers' clocks and the
   NeckSpineIK → HeadCopyRotation → NeckTwistDisperser order are unchanged. The authored cones of
   Technical Requirement 6 also bound AI-009's rest-bounded fold family; retuning them changes that
   family's domain and needs the same user decision. The independent upstream/solved
   snapshots, lifecycle, non-recursive fold-aware origin construction and upstream-origin no-pivot fallback in
   [AI-009 Technical Requirements 14–22](../../ai/009-attention-head-orientation/index.md#technical-requirements)
   are normative integration dependencies. Prove the seam in the installed engine before shared API expansion.

## In Scope

- A `CCDIK3D`-based neck-spine IK chain driving neck and spine bones towards a head target
  position.
- Joint constraint configuration to prevent implausible neck/spine rotations.
- Local `NeckSpineIK` authoring in every supported base character template.
- One shared per-joint constraint set authored identically across all supported base character templates.
- `NeckPivot` marker authoring at the neck articulation in every supported base character template.
- Automated photobooth and integration verification using representative target head poses.

## Out Of Scope

- Full-body IK, limb IK, locomotion blending, or animation state machine design.
- Retargeting rigs across different skeleton topologies.
- Physics-based secondary motion (for example, spring bones or ragdoll behaviour).
- Subjective animation polish beyond objective natural-pose checks in acceptance criteria.
- Changes to the `CCDIK3D` solver implementation itself; this spec governs configuration
  authoring only.

## Acceptance Criteria

1. The spec defines both user-visible motion outcomes and technical implementation contracts.
   (TRACES UR-1, UR-2, UR-3, UR-4, TR-1, TR-2, TR-3, TR-4, TR-5, TR-6, TR-7)
2. Each supported base character template contains a locally authored `NeckSpineIK` Godot
   `CCDIK3D` node that drives neck-spine adjustment towards its designated head-solve target.
   (TRACES TR-1, TR-2)
3. Every local configuration resolves the canonical `Head` end bone for its own generated
   skeleton, with per-template cached indices that match that skeleton's name-keyed bones.
   (TRACES TR-2)
4. Each local configuration serialises a canonical `Spine`-to-`Head` chain with exactly five
   joints and explicit per-joint constraint configuration — including the Head end-bone joint's
   explicit null limitation — to keep rotations within plausible ranges. (TRACES TR-1, TR-3)
5. Photobooth verification runs through the head-hips verification scene:
   - `@game/tests/ik/head_hips_ik_test.tscn`
   - runner script: `@game/tests/ik/head_hips_ik_test.gd`
   (INHERITS `@game/assets/testing/photobooth/templates/full_body_5_cams.tscn`)
6. The verification scene defines target markers for visual and non-visual checks using
   `DebugMarker` for each required pose. (TRACES TR-4)
7. Visual checks cover moderate and extreme target poses: forward, left, right, up,
   down, stoop-forward, lean-back. (TRACES UR-1, UR-2)
8. Before feature-level capture, runner performs a camera/marker framing pass to
   confirm required markers and subject regions are visible.
9. The automated runner exercises the locally authored `NeckSpineIK` configuration for each
   pose scenario and captures screenshots using `Photobooth.capture_screenshots(...)`.
   (TRACES TR-4)
10. Visual checks confirm resulting pose remains natural without obvious over-rotation,
    inversion, or discontinuous neck-spine deformation. (TRACES UR-1, UR-2)
11. C# integration tests validate the locally authored neck-spine IK without a dedicated IK
    scene: base-template authoring assertions resolve each template's cached indices against
    its own skeleton, and verification-scene behaviour is asserted non-visually through the
    head-hips coverage. (TRACES TR-2, TR-4)
12. All supported base character templates serialise identical per-joint constraint sets —
    rotation axes, cone angles, and limitation frames including right axes and rotation
    offsets — matching the unified constraint set of Technical Requirement 6; base-template
    authoring assertions pin cross-template equality, the absolute values, and the
    root-to-end joint-to-bone order per joint via `get_joint_bone_name`.
    (TRACES UR-3, TR-4, TR-5, TR-6)
13. Every supported base character template authors a `NeckPivot` marker at the neck
    articulation, parented to the Neck bone node, mirroring the `Viewpoint`-under-Head
    authoring pattern. Integration verifies upstream Head and Neck/pivot capture without solved attachment feedback,
    animation advancement or bone writes, while preserving modifier order and other head-provider paths. (TRACES TR-7)
14. Photobooth checks on the stoop-forward and down poses (reference female subject) show a
    distributed bend — the Neck and UpperChest bones visibly share the rotation rather than
    the Head bone carrying it alone. (TRACES UR-4, TR-4)
    Apply AI-009's frozen stoop/share/same-sign/aim bounds over its complete windows; authoring equality alone is not
    anatomical validation. Retain meaningful head-hips solver engagement (Head travel ≥0.05 m with directional alignment
    and Neck engagement) alongside saturation travel ≤0.001 m; a no-motion-only assertion is insufficient.

## References

- @game/assets/testing/photobooth/templates/full_body_5_cams.tscn
- @game/tests/ik/head_hips_ik_test.tscn
- @game/tests/ik/head_hips_ik_test.gd
- @specs/testing/002-visual-verification-scope/index.md
- @specs/character/001-character-skeleton/index.md
- @integration-tests/src/Mind/Attention/DownwardFoldAngularAcceptance.cs
- [AI-009: Attention-Driven Head Orientation](../../ai/009-attention-head-orientation/index.md)
