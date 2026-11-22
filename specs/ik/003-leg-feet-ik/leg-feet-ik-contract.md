---
title: Leg-Feet IK Contract
---

# Leg-Feet IK Contract

## Requirement

Define rig-neutral foot-target resolution, target actuation, knee-pole prediction, and two-bone lower-limb solving
beneath IK-003.

## Goal

Resolve each leg's target deterministically from the current tick's pose and solve it without letting providers or
solvers rewrite unrelated state or policy.

## User Requirements

1. Legs bend predictably and smoothly towards resolved foot targets on both reference rigs.
2. An applicable foot provider takes control without visible conflict; a provider that cannot apply lets ordinary
   animation-following behaviour continue.
3. Feet do not snap because a solver mutates an input target or a knee pole becomes degenerate.
4. Each foot is isolated from the other foot's invalid or inapplicable provider state.

## Technical Requirements

1. Each side has an upper-leg → lower-leg → foot `TwoBoneIK3D` solve with a preceding rig-neutral pole-target stage.
2. Target synchronisation is the first modifier stage. It sets both foot targets to the current animated foot poses
   and does not mutate authored animation.
3. `CharacterIK` resolves the explicit provider before the fallback provider. A returned zero-influence intent is
   applicable and disables that side's leg solve. A provider may instead refuse, letting the next provider run.
4. An ordered composite queries providers in authored order and selects the first applicable, finite, value-only
   result. It uses no numeric priorities, and a result for one side is never applied to the other.
5. Providers return values only and never mutate targets, activity gates, modifiers, root, hips, skeleton, or actor.
   Only `CharacterIK` applies the selected target, gate, and influence for each side.
6. The modifier order is `sync → provider → hip reconciliation (when present) → lower-leg consumers`. `CharacterIK`
   repairs this order idempotently on initialisation or retry.
7. Providers resolve from the same-tick animated pose: after the animation source has applied the current tick and after
   that tick's actor movement operation. A target resolved from an earlier tick's pose is never applied. When a tick
   evaluates animation more than once, only the final evaluation is used, and an intermediate evaluation
   triggers no provider resolution or solve.
8. `CharacterIKSubsystemInstaller` sets `Skeleton3D.ModifierCallbackModeProcess` to `Physics` before activating
   modifier groups, and the animation source applies before the modifier chain within each physics tick.
9. Post-modification poses are observable only inside `Skeleton3D.SkeletonUpdated`. The engine applies modifier
   output to the skin and then restores canonical bone poses, so reads at physics-timer time cannot observe IK output.
   Diagnostics and test instruments capture post-IK output there, bound to the tick that produced it.
10. **Knee-Pole Prediction:**
     - Measure transverse animated bend off the animated hip-to-foot line, then resolve it perpendicular to the
       hip-to-foot-target axis. Blend weak or poorly aligned cues continuously towards the reference instead of
       using them raw. In the ordinary downward-leg region, reject cues on the rearward side of the plane
       perpendicular to the selected reference, not a plane based on avatar forward alone.
     - For an ordinary downward leg with a weak bend, use foot-target forward projected perpendicular to the
       target axis. Weight its influence smoothly by the magnitude of alignment between the foot and body up axes.
       When tilt makes the foot unusable, use projected avatar forward for a degenerate bend and retain a coherent
       animated bend cue where available.
     - Blend continuously over a tunable angular band away from straight down, at every azimuth, into an anatomical
       knee plane independent of foot yaw. A raised, deeply folded knee opens towards its own side, while an
       extended leg reaching forward bends upwards. Do not normalise a vanishing projection of avatar forward.
     - At the straight-up singularity the reference is undefined. Hold the last valid pole and resume without
       reversal when a reference is defined again.
     - Rate-limit the pole's angular adjustment relative to the transported reference using elapsed time, not a
       per-frame step. Keep the pole continuous when body-pose ownership passes from generated motion to tracked
       idle under [CTRL-001 TR4](../../ctrl/001-locomotion/index.md#technical-requirements).

    A tunable rest-leg-derived minimum pole offset applies.
11. The solver never writes a target's position, rotation, or scale.
12. Target geometry and reach checks use composed position and rotation in one consistent world frame, with no
    second application of any scale conversion. This generic solve does not impose locomotion foot planting.
13. The path contains no terrain probe, pelvis or whole-body adjustment, or downstream target overwrite.

## In Scope

- Target synchronisation, provider composition and actuation, same-tick ordering, and post-IK observability.
- Pole prediction and two-bone solving on both reference rigs.

## Out Of Scope

- Locomotion generation and contact adaptation; see the [IK-003 boundary](index.md).
- Numeric pole thresholds and scene-node names, which remain rig configuration.
- Terrain adaptation and pelvis authority.

## Acceptance Criteria

### User Requirement Acceptance

1. Both reference rigs show stable knee direction and target following, including a degenerate-pole case.
   Observe the player from release through settling into tracked idle; knees do not flip or snap at the handoff.
2. Switching between synchronised, applicable-provider, and fallback targets is deterministic and conflict-free.
3. A refusal or invalid result for one foot does not disturb the other foot.

### Technical Requirement Acceptance

1. Fixtures prove the two-bone solve and pole stage per side on both rigs.
2. Fixtures prove synchronisation, explicit-provider precedence, zero-influence behaviour, refusal fallback, and
   first-applicable composite selection with side isolation.
3. Fixtures prove providers are value-only and `CharacterIK` is the sole actuator, with idempotent order repair.
4. A real-NPC trace proves `Physics` callback mode, animation before modifiers, same-tick provider input, and post-IK
   capture inside `Skeleton3D.SkeletonUpdated`.
5. Verify pole prediction, the minimum pole offset and that the solver never writes its target.
    - With target position fixed and the bend cue weak, yaw a usable foot target in ordinary downward reach and
      verify that it redirects the pole. Vary foot tilt to verify smooth loss of yaw influence and the unusable-foot
      fallback. At forward extension, verify the reference bends up and does not follow foot yaw.
    - Check the reference before rate limiting across forward-parallel reach from sagittal and oblique approaches
      over the supported reach envelope. Before rate limiting, verify that a raised, deeply folded knee opens
      towards its own side. Verify continuity across the transition band and that straight up holds the last valid
      pole and resumes without reversal.
    - Verify the angular adjustment limit scales with elapsed time. Trace the pole from release through generated
      settling and into settled tracked idle. Check for no snap, including at the ownership handoff, and no
      rearward pole relative to the selected reference in the ordinary downward-leg region during that sequence.
6. Per-foot checks prove target geometry and reach computation use the composed target transform in one consistent
   world frame, without duplicate scaling.
7. An audit confirms no terrain probe, pelvis adjustment, or downstream target overwrite exists.

### Acceptance Traceability

- User Requirements 1, 3 → User Requirement Acceptance 1, Technical Requirement Acceptance 1, 5.
- User Requirements 2, 4 → User Requirement Acceptance 2–3, Technical Requirement Acceptance 2–3.
- Technical Requirements 1–6 → Technical Requirement Acceptance 1–3.
- Technical Requirements 7–9 → Technical Requirement Acceptance 4.
- Technical Requirements 10–11 → Technical Requirement Acceptance 5.
- Technical Requirement 12 → Technical Requirement Acceptance 6.
- Technical Requirement 13 → Technical Requirement Acceptance 7.

## Implementation Status

The pole rule has been independently reviewed for headset playtest. The user reports that the original knee flip
appears gone in the headset; this does not resolve the settling pose snap or rule out rarer knee-flip variants.
A foot-yaw crossing at ±180° while foot-reference weighting is partial has not been tested.

## References

- [IK-003 Overview](index.md)
- [Shared Animation Contract](../../animation/index.md)
