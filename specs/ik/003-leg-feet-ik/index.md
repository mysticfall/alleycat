---
id: IK-003
title: Leg and Feet IK System
---

# Leg and Feet IK System

## Requirement

Define rig-neutral lower-limb IK and animation-driven foot planting for player and NPC rigs.

## Goal

Feet that the animation metadata marks planted stay put horizontally and keep the facing captured at contact, and feet
it marks swinging follow the animation. Foot planting reads ANIM-002 planted intervals, pins the ball joint
horizontally and holds the planted foot's world heading through IK, and never moves the character. It also exposes its
contact state read-only, so movement owners can observe it without gaining movement authority.

## User Requirements

1. Player and NPC legs bend stably and naturally towards their foot targets.
2. A planted foot does not slide horizontally until its planted interval ends. Rolling about the ball of the foot,
   including heel lift, is acceptable. The foot keeps its animated height, so small float or sink left by retargeting
   remains visible.
3. A planted foot keeps the world facing captured when it was planted: it does not twist about the ball of the foot
   while planted. Rolling about the ball, including heel lift, remains acceptable.
4. A planted foot stays planted, in position and facing, through animation crossfades, including a stop clip blending
   into Idle.
5. A foot released at lift-off rejoins its animated motion smoothly, in position and facing, without a pop or
   single-tick jump.
6. Foot planting never moves, steers, or delays the character.

## Technical Requirements

### Ownership and Solve

1. IK-003 is rig-neutral: the same contracts apply to NPC and player rigs, and neither rig's topology or scene is
   normative for the other.
2. `CharacterIK` is the generic resolver and sole actuator of each foot target, activity gate, and modifier influence.
   The [Leg-Feet IK Contract](leg-feet-ik-contract.md) normatively owns target synchronisation, provider composition,
   modifier order, pole prediction, and two-bone solving.
3. The modifier order is `sync → provider → hip reconciliation (when present) → lower-leg consumers`.

### Foot Planting

4. Foot planting is a value-only foot-target provider. It outputs IK targets only and never moves the actor or feeds a
   movement contribution to any movement owner. Its contact observations (Technical Requirements 17–20) are read-only
   state.
5. **When:** a foot is planted exactly while the [ANIM-002 per-foot planted intervals][anim-planted] of the clips
   driving the pose say so, evaluated at each clip's actual playback position, including slices. The metadata is
   authoritative. The foot bone's own speed and height are never used to decide planting.
6. When several clips or slices contribute, planted state follows the
   [ANIM-002 Planted Combination Rule][planted-rule]. A pin and its captured facing held into a crossfade are kept,
   not recaptured, while the foot stays planted under that rule, so pins and facings survive crossfades including
   stop → Idle.
7. **Where:** the pinned point is exactly one point, the ball joint: the rest-pose origin of the toe bone (`LeftToes`
   or `RightToes`, the Godot humanoid-profile names used on `GeneralSkeleton`), expressed in foot-local space. "Ball
   point" in NAV-001 and this spec means this joint. The foot bone origin is never the pinned point. Both toe bones are
   required rig data: if either is missing, foot-planting binding fails with a clear error. There is no degraded
   per-foot mode.
8. Planting uses the same-tick pose: the pose after the AnimationTree has applied the current tick and after that
   tick's actor movement operation, before lower-leg solving. A pose from an earlier tick is never used. When a tick
   evaluates the AnimationTree more than once, as in [NAV-001 mid-clip entry][nav-tree-use], only the tick's final
   evaluation is used. Capture and release commit exactly once per tick, from that pose: never from a pose that
   precedes the tick's movement operation, and never from an intermediate evaluation.
9. At interval start (weight-bearing ball contact, per ANIM-002), planting captures two values so it introduces no
   jump: the ball point's current world planar position, as its pin, and the foot's current world horizontal facing
   (Technical Requirement 12). While planted, the ball point is held at that planar position and the foot's heading at
   that captured facing. The ball's vertical position follows the animation, and no floor projection or height
   correction is applied.
    - **Persistence:** both values are preserved throughout the continuous metadata-planted interval, across animation
      seams and movement-request changes. There is no periodic recapture.
    - **Interrupted release:** if the interval starts while a previous release blend is unfinished, the pin and facing
      are captured from the effective continuous target the foot is currently following, not from the raw animated
      pose, so capture does not jump.
    - **No preceding swing:** an interval with no preceding swing (idle at spawn, teleport, or scene load) pins at the
      ball point's current animated position and captures its current animated facing.
    - **No recapture:** a pin and facing that have blended out under Technical Requirement 14 are not recaptured within
      the same planted interval. The foot follows the animation until the next planted interval.
10. While planted, the held facing is the world-up heading captured at interval start. The target basis preserves the
    current animation's tilt and heel roll and the animated ball height: the full foot quaternion is not frozen,
    orientation is not projected onto the ground, and no vertical correction is introduced. The target differs from
    the animated pose by a yaw offset about the world vertical axis, applied about the ball point.
11. The yaw offset rotates the target about the ball point, not the ankle or the foot-bone origin. For the held target
    basis `B_h`, the foot-local rest offset `r` of the ball point, and the desired world ball position
    `q = (pinX, animatedBallY, pinZ)`, the target origin is `q − B_h · r`, evaluated in one consistent world frame.
    Bone and container scales are honoured within that frame; no scale conversion, including `motion_scale`, is
    applied a second time.
12. The facing is derived from a declared anatomical forward axis of the foot and a declared frame convention,
    evaluated through world-space transforms. It is never read as a hard-coded skeleton-local axis, such as an assumed
    local `−Z`, and never obtained by copying a Euler `Y` rotation across frames. The convention is correct on both
    reference rigs, whose container is yaw-rotated 180°, so container rotation neither flips nor offsets the captured
    or held facing. Facing comparisons are wrap-safe over the full circle. A degenerate horizontal projection of the
    declared axis, where the axis points nearly vertically, is detected and handled by a declared deterministic rule
    that changes only how the facing is held, never whether the foot is planted: contact permission stays with the
    ANIM-002 intervals.
13. At interval end, the planar position offset and the yaw offset blend out together over a tunable duration, ending
    exactly on the current animated target, its position and its facing.
14. Reach is evaluated after the yaw hold and its ball-offset compensation (Technical Requirement 11): a fixed ball
    position does not imply an unchanged ankle position, because the held rotation moves the foot origin about the
    ball. A target that leaves the leg's reach blends out instead of hyperextending the leg or moving the actor.
15. Each foot plants and releases independently, including double support.
16. [NAV-001][nav-001] gates actor correction on the same planted state under the same rule. The intervals say only
    when a foot is planted; they grant nothing and authorise no actor movement. NAV-owned seam body alignment, where
    NAV-001 defines it, is eligible only while exactly one foot is planted; it is zero in double support and in Idle,
    frozen, or finished states, and it has no anchor when both feet swing. Strict phase agreement makes aligned Idle
    joins double-supported, so the contact observations below never enable movement there. This cross-references
    NAV-001 ownership; it creates no IK movement authority.

### Contact Observations

17. Foot planting exposes one read-only contact snapshot per foot, containing: the anatomical side; a continuity
    identity for the current planted interval; the physics tick it was last committed on; the metadata planted phase
    under the [Planted Combination Rule][planted-rule]; the solver's hold or release state; whether a release blend is
    unfinished; the retained world-planar pin; the captured facing; and the binding and reset identity of the rig
    binding that produced it. The snapshot is observed state, not movement permission. Foot planting computes no
    actor movement request and sends none; `CharacterIK` remains the sole foot-target actuator.
18. Snapshot history persists across animation seams, plan changes, movement requests, and request completion. Nothing
    resets at request start. Only an explicit rig rebind or teleport reset changes the binding and reset identity, and
    each such reset is identifiable as a reset, distinct from an ordinary new planted interval.
19. A consumer may read the previously committed snapshot as persistent history. It must not substitute that
    snapshot's pose or history for the current tick's final native pre-IK animation pose, which remains the only pose
    input foot planting resolves from (Technical Requirement 8).
20. The tick ordering for the snapshot and for every capture and release is rig-neutral: the AnimationTree's final
    native evaluation, then the rig's authoritative movement owner's single collision-realised actor movement
    operation — [NAV-001][nav-001] for NPC rigs and [CTRL-001][ctrl-001] for player rigs — then foot planting
    evaluates that same pose under the realised transform and commits each capture and release exactly once. The
    ordering holds on stationary and no-displacement ticks, whose movement operation composes zero displacement.
    Generic IK fixtures keep the same ordering semantics with an explicit no-displacement movement stage, without
    installing a navigation system. Player-rig adoption remains deferred as scoped under Out Of Scope; this naming
    adds no player implementation obligation. New contacts are captured after the tick's movement, never before it,
    and never from an intermediate `Advance(0)` evaluation.

### Validation

21. Focused fixtures cover both reference rigs with equivalent inputs: provider order and refusal, side isolation,
    pole fallback, interval start, hold, interval-end blend-out, crossfade and stop → Idle survival, sliced clips,
    unreachable pins, floating and sinking source feet, the missing-toe-bone bind error, and double support.
22. Yaw-hold fixtures cover, on both rigs: capture without a jump, including a capture that interrupts an unfinished
    release blend; facing persistence through crossfades, seams, and repeated movement requests; release blending that
    ends exactly on the animated position and facing; ball-point preservation under the yaw-offset rotation; animated
    ball height, tilt, and heel-roll preservation; both feet and double support; wrap-safe facing handling on the
    180°-rotated container; the degenerate facing-axis projection; and reach safety after yaw compensation. Expected
    facings and positions are derived independently of the foot-planting implementation. The scripted native-heading
    ±180° wrap scenario also guards the planar final-post-IK ball realisation across its full capture, hold, unplant,
    and release window on both rigs, within the `5 mm` user-accepted tolerance recorded under Technical Requirement
    26; every other guard in that scenario — height, heading, input, axis, capture counts, and reach release — is
    unchanged.
23. Contact-observation fixtures cover: every snapshot field of Technical Requirement 17; persistence across seams,
    plans, requests, and completion; the absence of a request-start reset; ordinary interval changes remaining
    distinguishable from rebind and teleport resets; exactly one commit per tick, after the tick's movement operation;
    and that no observation path computes or issues actor movement.
24. Per tick, diagnostics record for each foot: metadata planted state, pin position, captured facing, animated
    target, post-IK ball point, and post-IK facing. Post-IK output is captured inside
    `Skeleton3D.SkeletonUpdated`, as defined by the [Leg-Feet IK Contract](leg-feet-ik-contract.md).
25. Planted-foot slip is measured as defined by [NAV-001 A2][nav-a2]: per planted run under the
    [Planted Combination Rule][planted-rule], the greatest planar world (XZ) distance of the post-IK ball point from
    its position at the start of the run. It never uses the foot-planting implementation's own planted state. Rotation
    about the ball point, including heel lift, and vertical motion are not slip. The yaw hold does not change this
    definition: slip stays planar, and facing error is neither slip nor an allowance against it. Screenshots are
    optional diagnostics.
26. Planted-foot facing hold is measured per planted run under the [Planted Combination Rule][planted-rule]: the
    wrap-safe world-heading difference between the post-IK facing of the declared anatomical axis and the facing
    captured at the start of the run, computed against independently derived expected geometry, never from the
    foot-planting implementation's own state. Tilt and heel roll are separated from yaw by the declared axis, so roll
    about the ball is not facing error. The acceptance tolerance for this measurement is a declared value with a
    recorded derivation from evaluated-pose and solver precision; diagnostic twist thresholds are analysis input, not
    approved bounds, and a tolerance changes only by recorded derivation or explicit user acceptance, never to fit
    current output. A single explicit user acceptance is recorded: the scripted native-heading ±180° wrap scenario's
    planar final-post-IK ball realisation tolerance is `5 mm` (accepted 2026-10-03), covering that scenario's full
    capture, hold, unplant, and release window on both rigs. It is a user acceptance decision, not a measured
    solver-precision envelope, and it revises no other tolerance, including the [NAV-001 A2][nav-a2] planted-run
    slip bound. All such traces are supporting evidence; the user naturalness judgement (IK-UAC-005) remains
    mandatory.

## In Scope

- Generic lower-limb provider resolution, sole actuation, pole prediction, and two-bone solving.
- Animation-driven foot planting, its yaw hold, its read-only contact observations, its diagnostics, and focused
  fixtures on both reference rigs.
- Delivery on the NPC rig in the navigation test scene first.

## Out Of Scope

- Terrain probing, pelvis or whole-body repositioning, and movement policy.
- NAV-001 movement, seam-alignment, and correction policy, which NAV owns; IK-003 supplies read-only contact
  observations only.
- Player-rig adoption beyond the rig-neutral contract, which follows the [CTRL-001][ctrl-001] revision.
- Vertical float/sink correction and floor-height contact, deferred until uneven terrain and clothing are specified.

## Acceptance Criteria

### User Requirement Acceptance

1. **IK-UAC-001:** Both reference rigs show stable knee direction and target following in animated and planted poses.
2. **IK-UAC-002:** In the NAV-001 scenarios, the worst planted run's slip, measured under Technical Requirement 25, is
   at most `5 mm` ([NAV-001 A2][nav-a2]), including through crossfades and stop → Idle.
3. **IK-UAC-003:** Lift-off blends each foot back to its animated motion, in position and facing, without a
   single-tick jump.
4. **IK-UAC-004:** Traces show zero actor movement caused by foot planting.
5. **IK-UAC-005:** The user judges planted feet natural in the navigation test scene.
6. **IK-UAC-006:** In the NAV-001 scenarios, each planted run's facing error, measured under Technical Requirement 26,
   stays within the declared tolerance: a planted foot does not visibly twist about the ball of the foot, including
   across crossfades, seams, and repeated requests.

### Technical Requirement Acceptance

1. **IK-TAC-001:** Fixtures prove modifier order, provider precedence, side isolation, and `CharacterIK`-only
   actuation on both rigs.
2. **IK-TAC-002:** Fixtures prove planted state follows the ANIM-002 intervals at each clip's same-tick playback
   position, including slices, and is unaffected by foot-bone speed or height. They also prove pin and facing capture
   without a jump, planar hold of the ball point with its vertical position following the animation, roll and heel
   lift about the pinned ball point without planar displacement, blend-out ending on the animated target, and
   independent feet. They also prove an interval with no preceding swing (spawn in Idle, teleport, scene load) pins
   at the current animated ball position and captures its current animated facing, and that a pin blended out under
   Technical Requirement 14 is not recaptured within the same interval, the foot following the animation until the
   next one.
3. **IK-TAC-003:** A crossfade between clips and a stop → Idle transition keep a planted foot pinned, in position and
   facing, without recapture, and a crossfade to a clip marking the foot swinging releases it once that clip's weight
   exceeds the [Planted Combination Rule][planted-rule] threshold.
4. **IK-TAC-004:** An unreachable pin blends out without hyperextension or actor movement.
5. **IK-TAC-005:** In a source clip whose animated foot floats or sinks, the planted ball point holds its planar pin
   while keeping the animated height.
6. **IK-TAC-006:** NAV-001 correction gating, foot planting, and slip evidence all derive planted state from the
   ANIM-002 intervals under the [Planted Combination Rule][planted-rule], never from the foot-planting state.
7. **IK-TAC-007:** On both reference rigs, female and male, each foot's ball point resolves to the rest-pose origin of
   its toe bone in foot-local space. A rig missing either toe bone fails foot-planting binding with a clear error, and
   no foot plants on that rig.
8. **IK-TAC-008:** Fixtures prove the yaw-hold contract of Technical Requirements 9–14 on both rigs: capture without
   a jump, including an interrupted release blend; persistence through crossfades, seams, and repeated requests;
   orientation-compensated ball preservation; animated height, tilt, and heel roll; both feet and double support;
   wrap-safe handling on the 180°-rotated container; degenerate-axis handling; and reach safety after yaw
   compensation — each against independently derived expected geometry within tolerances declared under Technical
   Requirement 26, with the scripted native-heading ±180° wrap scenario's planar final-post-IK ball realisation
   within its user-accepted `5 mm` bound.
9. **IK-TAC-009:** Fixtures prove the contact-observation contract of Technical Requirements 17–20: snapshot fields;
   persistence across seams, plans, requests, and completion; no request-start reset; identifiable rebind and
   teleport resets, distinguishable from ordinary interval changes; exactly one commit per tick after the rig's
   authoritative movement operation under Technical Requirement 20, including in generic fixtures whose movement
   stage is an explicit no-displacement operation with no navigation system installed; and no actor-movement
   computation or request anywhere on the observation path. They also prove the states NAV-001's usable-anchor rule
   consumes: an actively held contact reports hold with no unfinished release blend; a reach release within a
   still-planted interval, an unfinished release blend, and a completed release are each reported as not holding;
   the committed-tick field distinguishes a latest commit from a stale one; and a new contact's snapshot is
   committed only after the tick's movement, never provisionally before it.

### Acceptance Traceability

- User Requirement 1 → IK-UAC-001, IK-TAC-001.
- User Requirement 2 → IK-UAC-002, IK-TAC-002, IK-TAC-005, IK-TAC-008.
- User Requirement 3 → IK-UAC-006, IK-TAC-008.
- User Requirement 4 → IK-UAC-002–003, IK-UAC-006, IK-TAC-003, IK-TAC-008.
- User Requirement 5 → IK-UAC-003, IK-TAC-002, IK-TAC-008.
- User Requirement 6 → IK-UAC-004, IK-TAC-004, IK-TAC-009.
- Technical Requirements 1–3 → IK-TAC-001.
- Technical Requirements 4–8 → IK-UAC-004, IK-TAC-001–002, IK-TAC-009.
- Technical Requirements 9–15 → IK-UAC-002–003, IK-UAC-006, IK-TAC-002–005, IK-TAC-007–008.
- Technical Requirement 16 → IK-TAC-006.
- Technical Requirements 17–20 → IK-TAC-009.
- Technical Requirements 21–26 → IK-UAC-002, IK-UAC-005–006, IK-TAC-001–009; NAV-001 A2.

## Current Coverage Status

This section is non-normative current status, not fresh verification or a claim of code/spec alignment. Technical
Requirement 22, IK-TAC-008, and IK-UAC-006 are unchanged and remain normative; nothing here weakens them.

By user decision on 2026-10-04, three failing integration facts in the foot-planting rig fixtures were removed as
reduced coverage. Each exercised real NAV movement requests; removal asserts no pass and fixes no behaviour:

- The repeated-request world-yaw guards across native seams, one per rig, are gone. They were the only automated
  guards for the repeated-movement-request persistence required by Technical Requirement 22, IK-TAC-008, and
  IK-UAC-006. That persistence now has no automated guard, and both removed guards were failing, with recorded
  heading deviations, before removal.
- The male-rig walk crossfade and stop-to-idle guard is gone; its female twin is retained, so this coverage is now
  female-rig-only. The removed male fact last failed because a reach release occurred mid-plant; that reach-release
  deficit remains unresolved.

Current automated yaw-hold coverage on both rigs still includes the scripted native-heading ±180° wrap scenario with
its user-accepted `5 mm` planar final-post-IK ball realisation tolerance (Technical Requirement 26), and on the
female rig the same-tick-interval walk crossfade and stop-to-idle fact; both were green in the recorded 2026-10-04
default matrix, run before the removals. All other IK-003 fixture coverage is untouched by this change. Both named
facts were removed by the 2026-10-10 suite redesign below; read this paragraph as the 2026-10-04 record, not current
coverage.

Because the same decision reduced [NAV-001][nav-001] outcome coverage, the "in the NAV-001 scenarios" evidence of
IK-UAC-002 and IK-UAC-006 now derives only from the NAV scenarios that keep an automated outcome guard. The
IK-UAC-005 user naturalness judgement has not been performed and is outstanding.

### Suite Redesign (2026-10-10)

By user decision on 2026-10-10, the NAV/CTRL integration suite was redesigned rather than optimised: the
implementation is incomplete with unresolved problems, the current `sad` ANIM-002 variant is placeholder content to be
replaced, and no male retarget setup exists. Tests that had come to encode current placeholder behaviour were
deleted, not fixed; removal asserts no pass and fixes no behaviour. This entry supersedes the coverage statements
above as the current record.

IK-003-scoped changes:

- `IK003FootPlantingRigIntegrationTests` is trimmed from 21 facts to one: only `Female_MissingToeBoneFailsClosed`
  remains, a cheap anatomical wiring refusal in which providers, modifiers, and navigation fail closed. The deleted
  twenty include the scripted native-heading ±180° wrap scenarios on both rigs, the world-yaw temporal matrices, the
  scripted interval capture/hold/blend-out facts, the female same-tick-interval walk crossfade and stop-to-idle fact,
  and every male twin. Automated yaw-hold coverage now rests only on the retained world-facing hold regressions; the
  scripted wrap guard of Technical Requirement 22 and IK-TAC-008, with its user-accepted `5 mm` tolerance, has no
  automated guard.
- `IK003NavigationFootPlantingIntegrationTests` is deleted. Its navigation planting coverage is replaced at outcome
  granularity by NAV-001 spine fact 2, `WalkToDestination_PlantedFeetHoldStance_NoFootSliding`: stance pinning
  against world anchors within the A2 `5 mm` planar bound, on the female default rig only.
- The runtime-vs-recorder parity machinery is removed with `NAV001PlantedStateRecorderValidationIntegrationTests` and
  `NPCSameTickPoseOrderingIntegrationTests`; generic graph-weight and planted-rule unit oracles remain.
- Retained unchanged: `IK003WorldFacingHoldRegressionIntegrationTests` (6 facts — the remaining content-coupled female
  live yaw-hold regressions, deliberately kept as a distinct IK safety guard beyond the positional NAV spine, which
  checks ball position, not world orientation) and `FootPlantingElapsedClockIntegrationTests` (5 facts — actual
  observed time, stage skip, cache/rebind, and lifecycle). The production simulated-clock actual-delta fix and its
  tests remain: that fix is a defect fix independent of the test-strategy change.
- The "in the NAV-001 scenarios" evidence of IK-UAC-002 and IK-UAC-006 now derives only from the spine's single
  female walk. No automated male-rig IK-003 locomotion coverage remains; rigs resume automated coverage when
  replacement content and male retargeting land.

## References

- [Project Specifications Index](../../index.md)
- [Leg-Feet IK Contract](leg-feet-ik-contract.md)
- [ANIM-002: Locomotive Animation Set](../../animation/002-locomotive-animation-set/index.md)
- [NAV-001: NPC Navigation][nav-001]
- [CTRL-001: Locomotion][ctrl-001]

[anim-planted]: ../../animation/002-locomotive-animation-set/index.md#motion-metadata
[planted-rule]: ../../animation/002-locomotive-animation-set/index.md#planted-combination-rule
[nav-001]: ../../nav/001-npc-navigation/index.md
[nav-a2]: ../../nav/001-npc-navigation/index.md#parent-acceptance-criteria
[nav-tree-use]: ../../nav/001-npc-navigation/index.md#animation-tree-use
[ctrl-001]: ../../ctrl/001-locomotion/index.md
