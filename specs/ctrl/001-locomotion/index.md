---
id: CTRL-001
title: Locomotion
---

# Locomotion

## Requirement

Define responsive player locomotion that uses authored animation and metadata as adaptable motion input while preserving
collision, foot-planting, and control ownership.

## Goal

Player movement should look and feel better than a conventional eight-direction blendspace without IK or per-clip
metadata, while remaining practical for precise manual positioning.

Planners, implementers, and reviewers should consult
[current implementation and acceptance gaps](#current-implementation-and-acceptance-gaps) before claiming delivery.

## User Requirements

1. Movement has convincing body motion and stable supported feet. Rolling about the ball of the foot, including heel
   lift, and small inconspicuous slip are acceptable; visible sliding, warping, stiff legs, and body jerks are not
   acceptable precision fixes.
2. Releasing translation input ends ordinary travel demand on the first eligible physics tick, then stops the body
   within the release bounds on every lane: release-to-stop within 400 ms and post-release displacement within 200 mm
   (defaults, tunable). Stop responsiveness outranks continuation naturalness: remaining stop motion is compressed to
   fit the bounds so marker positioning, taps, and repeated nudges stay practical.
3. Fresh translation or yaw input is prompt and interruptible during starts, travel, stopping, and stationary movement.
   It does not wait for a clip or step to finish. A direction reversal is the bounded exception: its authored braking
   exit plays out before the opposite direction enters (User Requirement 9).
4. Runtime may adapt translation, yaw, stride, timing, and swing-foot placement to satisfy player intent. Authored root
   motion guides the result but is not immutable authority. Taps and nudges may be shorter than a full entry or exit;
   retained clips, slices, and coherent adaptation must support them without requiring new animation content.
5. Optional navigation yields before non-neutral manual movement. Permission loss produces a safe, finite response.
6. Releasing input at any point, including during an entry or crossfade, transitions into governed stop presentation
   and does not continue ordinary travel. Non-lateral lanes play their real stop animation, rate-compressed inside the
   release bounds (User Requirement 2); lateral-dominant releases take the neutral stop policy of User Requirement 11 —
   a bounded crossfade, not a hard idle drop. Feet that ANIM-002 marks planted stay planted through the stop or neutral
   crossfade into Idle.
7. Naturalness is ultimately a manual user judgement. Automated evidence supports diagnosis but does not replace that
   judgement.
8. A held turn with no translation input presents authored turn-in-place motion and turns the actor toward the
   requested side at the authored content's rate. Releasing the turn returns through the authored transition, and
   translation input takes precedence immediately without an idle interlude.
9. Reversing between forward and backward travel presents authored braking for the travelled direction instead of an
   instant opposite-gait contradiction of realised motion. The opposite direction then enters through the normal start
   path.
10. Held translation input moves the actor along the intended actor-frame direction regardless of the visual rig's own
    orientation or any rotated parent transform.
11. Releasing a lateral-dominant input never rotates the body's remaining velocity into another lane. Releasing a
    strafe or lateral-dominant diagonal stops in place through the neutral stop policy, with no forward lunge:
    post-release forward displacement on a pure-lateral release stays within 100 mm (default, tunable). Forward- and
    backward-dominant releases keep their scaled authored stops.

## Technical Requirements

1. CTRL owns player input policy, movement response, animation scheduling, and optional-navigation arbitration. It does
   not own NPC route policy, foot planting, or IK actuation.
2. Each eligible physics interval has one authoritative actor movement composition and collision operation. Evaluated
   animation motion and requested adaptation are recorded separately from collision-realised movement. Nonlinear
   collision results are not decomposed by subtraction.
3. Evaluated authored root is consumed at most once. Runtime adaptation may alter its translation, yaw, stride, timing,
   or swing-foot placement; no parallel root owner or duplicate animation-tree evaluation may move the actor. Coordinate
   body motion, timing, and swing landing while [IK-003 foot planting][ik-003] keeps planted feet in place and
   `CharacterIK` remains the sole foot actuator.
4. Neutral translation input ends ordinary travel demand and changes stop policy on the first eligible tick. Tail
   playback, meaning playback of the selected stop clip that takes the player from travel to Idle, may begin after
   bounded selection and graph-transition latency; that latency does not authorise continued ordinary travel demand.
   CTRL may use bounded stop-clip selection without routing every manoeuvre through it. Scheduling is presentation that
   follows current intent and never gates physical response; while the body still travels against fresh intent, the
   presented gait agrees with realised motion. Sustained travel playback rate is a demand-magnitude schedule onto the
   configurable minimum-to-maximum playback-rate bounds, and an authored exit holds the last published travel rate so
   its crossfade does not step the cadence; realised-speed coupling is not a runtime contract. Those bounds must
   actually govern stop playback: stop clips route through playback-rate governance (Technical Requirement 16)
   instead of playing outside it at a fixed 1.0×, and within the release bounds stop responsiveness outranks cadence
   continuity at the stop seam. Presentation degradation is reported with evidence rather than silently accepted, and a
   brief bounded idle-to-entry crossfade slide is accepted seam smoothing.
5. New input promptly invalidates incompatible stop or manoeuvre state, except the authored braking exit that a
   direction reversal routes through (Technical Requirement 14), which plays out while the body still travels against
   the fresh intent. Unchanged neutral input must not create a no-progress retry loop or walking-in-place fallback.
6. Foot planting follows [IK-003][ik-003]. It never moves the actor, steers, brakes, delays input, or gates stop
   selection.
7. Release at any point, including an entry or crossfade, accepts release on the first eligible tick, stops ordinary
   travel demand, and transitions into governed stop presentation: the lane's real production stop animation,
   rate-scaled inside the release bounds (Technical Requirement 16), or the neutral stop policy for lateral-dominant
   releases (Technical Requirement 17). Stop candidates are ranked by measured whole-motion suitability.
8. Actor-relative planar input uses the actor yaw frame (forward −Z, right +X). Imported rig-space conversion occurs
   once at its declared boundary.
9. Motion selection is bounded and event-driven. Per-frame animation-library search or metadata parsing is prohibited;
   immutable package metadata may be shared while mutable movement state remains actor-local.
10. Validation records the release tick, release-to-stop tick count, graph states, actual displacement and heading,
    evaluated animation motion, requested adaptation, collision-realised movement, selected metadata, interruption, and
    planted-foot horizontal slip of the ball point inside ANIM-002 planted intervals, as defined for NAV-001 A2. From
    these per-tick traces, tests assert the release bounds per lane — release-to-stop within 400 ms, post-release
    displacement within 200 mm, and pure-lateral forward displacement within 100 mm (defaults, tunable) — rather than
    merely recording them. Naturalness of the bounded stop remains the manual judgement of User Requirement 7 on the
    same evidence.
11. Stationary turning is authored content only: held turn input with no translation presents the wired turn-in-place
    states playing the authored pivot clips, loops while held, and releases through the authored crossfaded return,
    with a hard idle start only on permission loss. Movement wins, handing off directly into the movement entry with
    no idle interlude. Yaw reaches the actor solely through the authored root-rotation path; synthetic yaw and second
    yaw actuators are prohibited. The turn rate is owned by the authored pivot content — a content decision, not a
    runtime tuning knob. Degradation is availability-gated: reported finitely, and only when the wired tree lacks both
    turn states.
12. Authored root motion composes through the actor frame, never the rig's. The authored delta follows the fixed
    ANIM-002 skeleton-authoring convention — avatar forward is skeleton-local +Z and avatar right is −X — maps to the
    semantic actor yaw frame (forward −Z, right +X) as (−root.X, 0, −root.Z), and composes with the actor body's
    orthonormalised world basis. The rig container's own yaw never enters semantics, parent transforms compose through
    the body's global basis, and the production 180-degree rig behaviour is preserved by associativity.
    Movement-owner discipline is unchanged: one root-read pair, one guarded yaw application, one velocity assignment,
    and a single collision operation per interval (Technical Requirements 2–3).
13. Validation covers sustained travel and supported feet beyond the release trace of Technical Requirement 10.
    Production-rig trajectory pairing records per-tick actor transform, response model, evaluated graph state, and
    foot-bone world positions through the genuine controller-event input path, paired with captured frames for narrow
    visual acceptance. Synthetic fixtures must not present their own authoring as motion evidence: a fixture whose
    foot tracks are continuous sweeps with no stance phase cannot author a supported-foot drift bound, so gliding
    guards rest on frozen-pose progression plus the production pairing.
14. A travel-domain reversal — fresh intent whose domain differs from the travelled blend's under the backward-entry
    selection convention (forward versus backward-or-lateral) — routes through the authored exit for the travelled
    direction. The authored stop clips are the braking presentation: the player body's velocity is the authored root
    velocity, so there is no momentum model to decay and a positive-bounded playback rate cannot flip the authored
    sign — only authored content rooted in the travelled direction can present braking. The opposite direction enters
    afterwards through the normal idle-to-entry path, and while the exit plays the travelled blend and rate stay
    published, so no full-rate opposite-gait sample is presented. An opposing intent during the exit lets the stop
    play out; a same-direction resume keeps the no-interlude path. Lateral-within-lateral retargeting is not a
    reversal and keeps in-place retargeting.
15. The player may use a subset of ANIM-002 with relaxed seams: player intent, unlike an NPC route, is not known in
    advance, so the [NAV-001][nav-001] seam-alignment rules do not bind CTRL-001, while the ANIM-002 planted
    combination rule and package integrity contracts remain authoritative. Player consumers of catalogue distances or
    root-trajectory positions apply the [ANIM-002 scale rule][anim-scale]: multiply by the bound skeleton's motion
    scale; yaw is not scaled.
16. Stop playback is rate-governed per lane to compress the stop's playback time and presentation; because a stop
    clip's root displacement is rate-invariant — playing it faster shortens the time while the body still travels the
    authored distance — rate alone cannot meet the 200 mm displacement bound of User Requirement 2. That bound is
    enforced by the early-Idle cut permitted below together with the continuity clamp on translation adaptation
    authorised by User Requirement 4. The stop rate is computed from the selected
    clip and the bounds — by default `clipLength / min(400 ms, 200 mm × clipLength / authoredDisplacement)`, where
    `authoredDisplacement` is the clip's whole-clip displacement in actor units (catalogue endpoint times the
    [ANIM-002 scale rule][anim-scale]) — with headroom for the bounded selection and graph-transition latency of
    Technical Requirement 4, so the measured release-to-stop time satisfies the 400 ms bound of User Requirement 2,
    including the travel-to-stop crossfade's travel contribution. Stop rates clamp to configurable stop-rate bounds
    whose default maximum is raised (for example 4.0) and configured independently of the sustained-travel maximum.
    The early-Idle cut applies where a lane cannot meet both bounds by rate alone, and every such cut is reported as
    degradation evidence. This requirement binds release-initiated stops; the direction-reversal braking exit of
    Technical Requirement 14 keeps its own authored-exit contract.
17. Lateral-dominant releases — pure-lateral lanes and diagonals whose lateral component dominates — use a neutral stop
    policy: a bounded direct-to-idle crossfade with no stop-clip routing, so realised velocity decays in its own lane
    instead of rotating into a forward glide. The neutral stop accepts release on the first eligible tick, ends
    ordinary travel demand, and its crossfade is bounded so the release bounds of User Requirement 2 hold.
    Post-release forward displacement on a pure-lateral release stays within 100 mm (default, tunable), asserted from
    per-tick traces. Non-lateral lanes keep their lane's rate-scaled authored stop (Technical Requirement 16), and foot
    planting never gates the stop-policy choice (Technical Requirement 6).

## In Scope

- Player translation and yaw response, stopping, nudges, interruption, scheduling, stationary turning,
  direction-reversal braking, and navigation arbitration.
- Adapted animation motion, movement composition, and collision accounting.
- Numerical, structural, real-content, and focused manual acceptance evidence.

## Out Of Scope

- NPC route and arrival policy.
- Live animation-variant switching.
- New animation generation, generic motion matching, procedural gait generation, and terrain adaptation.
- IK-003 player-rig foot-planting adoption. The player has no IK-003 foot-planting stage yet; adoption is a separate
  future task under the rig-neutral IK-003 contracts. The boundary rules of Technical Requirements 3 and 6 bind now
  and at adoption.
- Re-authored or shortened stop clips, and authored lateral stop clips: future content options that may improve
  naturalness inside the release bounds. They are not required to deliver the stop-rate governance of Technical
  Requirement 16 or the neutral stop policy of Technical Requirement 17; the neutral policy exists precisely because
  authored lateral stops are unavailable now.
- VR wall-clock perception measurement of responsiveness: the release bounds are asserted in physics-tick time and
  millimetres, not as headset wall-clock perception studies.

## Acceptance Criteria

### User Requirement Acceptance

1. **CTRL-UAC-001:** Real selected-content play demonstrates credible cardinal, diagonal, turning, release, marker,
   tap, and repeated-nudge movement that is visibly better than the conventional blendspace baseline.
2. **CTRL-UAC-002:** Release ends ordinary travel demand on the first eligible tick, and every lane stops inside the
   release bounds: release-to-stop within 400 ms and post-release displacement within 200 mm (defaults, tunable),
   asserted per lane from per-tick traces rather than judged from aggregates. Within the bounds, stop responsiveness
   outranks continuation naturalness; naturalness of the bounded stop remains the separate manual judgement of
   CTRL-UAC-005.
3. **CTRL-UAC-003:** Fresh translation, reversal, and yaw input interrupt incompatible motion promptly without waiting
   for clip completion.
4. **CTRL-UAC-004:** Manual movement cancels optional navigation before CTRL consumes the sample; permission loss is
   safe and finite.
5. **CTRL-UAC-005:** Focused user review accepts supported-foot stability and motion naturalness, or records acceptance
   as blocked with the observed defect.
6. **CTRL-UAC-006:** Release during entry, crossfade, and sustained travel transitions into governed stop presentation
   — the rate-scaled production stop for non-lateral lanes, or the neutral stop policy for lateral-dominant releases
   (User Requirement 11) — without a hard idle drop or continued travel, and planted feet stay planted through the stop
   or neutral crossfade into Idle.
7. **CTRL-UAC-007:** A held stationary turn with no translation presents the authored turn-in-place pivot, turns the
   actor toward the requested side, sustains while held, returns through the authored crossfade on release, and yields
   to translation input immediately without an idle interlude.
8. **CTRL-UAC-008:** Reversing forward/backward travel presents authored braking for the travelled direction — the
   body decelerates through real stop content with no instant full-rate opposite gait — and the opposite direction
   enters normally afterwards.
9. **CTRL-UAC-009:** Held input moves the actor along the intended actor-frame world direction under the production
   180-degree rig, an identity rig, and a rotated parent transform alike.
10. **CTRL-UAC-010:** Production-rig trajectory pairing shows supported feet during sustained travel on production
    clips, with supported-foot step ratios in the recorded production envelope (0.30–0.50 in current measurements)
    presented as evidence for the manual judgement, not as a threshold.
11. **CTRL-UAC-011:** Releasing a lateral-dominant input stops without cross-lane velocity rotation: no forward lunge,
    pure-lateral post-release forward displacement within 100 mm (default, tunable), while non-lateral releases still
    present their scaled authored stops.

### Technical Requirement Acceptance

1. **CTRL-TAC-001:** Traces prove one collision operation per interval, one authored-root consumption, actor-frame input
   conversion, and truthful requested versus collision-realised movement records. Real retained-content taps and nudges
   below full transition displacement verify coordinated body/timing/foot adaptation, not foot planting alone.
2. **CTRL-TAC-002:** Tests retain an uninterrupted trace for every physics tick from accepted release until stop
   completion (Idle entry) or a named bounded refusal occurs. Each tick records:
   - zero ordinary-travel demand;
   - graph/state identity and clock where applicable;
   - evaluated animation/root motion;
   - requested adaptation;
   - composed requested actor movement; and
   - collision-realised movement.

   The schedule terminates finitely in completed stop presentation or that refusal. No retry, replay, unexplained
   tick, or intervening tick may be omitted. From the trace, tests assert per lane the release bounds — release-to-stop
   within 400 ms and post-release displacement within 200 mm (defaults, tunable; User Requirement 2, Technical
   Requirement 16) — and the pure-lateral forward-displacement bound of 100 mm (default, tunable; User Requirement 11,
   Technical Requirement 17). The same traces remain evidence for the manual naturalness judgement (User Requirement 7)
   beyond the asserted bounds. Tests also cover immediate interruption, bounded selection, actor-local state, and no
   idle-time retry.
3. **CTRL-TAC-003:** Traces prove foot planting causes zero actor movement and does not gate stop selection, and that
   `CharacterIK` remains the sole foot actuator.
4. **CTRL-TAC-004:** Real selected-content tests prove graph entry and actual stop-clip-driven movement against the
   selected package. Synthetic mechanism checks alone do not close player acceptance.
5. **CTRL-TAC-005:** Tests prove stationary turning through authored content only: the wired pivot states, looping
   while held, the authored crossfaded return with a hard start only on permission loss, movement priority with a
   direct entry hand-off, yaw applied solely through the authored root-rotation path with no synthetic yaw, and a
   degradation report that fires only when the wired tree lacks both turn states.
6. **CTRL-TAC-006:** Tests prove rig-independent actor-frame semantics — signed world displacement against
   independently placed markers under production-rig, identity-rig, and rotated-parent topologies — and the reversal
   contract: travelled-direction authored-exit routing, the stop playing out under opposing intent, no-interlude
   same-direction resume, and retained travel rate through authored exits.
7. **CTRL-TAC-007:** Tests prove stop playback is genuinely rate-governed: the configured stop-rate bounds reach stop
   playback, observed stop rates follow the per-lane computation of Technical Requirement 16 instead of a fixed 1.0×,
   the raised default maximum (for example 4.0) is configurable independently of the sustained-travel maximum, and an
   early-Idle cut fires only for lanes that cannot meet both release bounds by rate alone and is reported. Tests also
   prove the neutral stop policy: lateral-dominant releases take the bounded direct-to-idle crossfade with no
   forward-stop routing and no out-of-lane velocity rotation, with the pure-lateral forward bound asserted from
   per-tick traces. Direction-reversal exits stay covered by the reversal contract of CTRL-TAC-006, not by these
   release-bound assertions.

### Acceptance Traceability

- User Requirement 1 → CTRL-UAC-001, CTRL-UAC-005, CTRL-UAC-010.
- User Requirements 2–3 → CTRL-UAC-002–003, CTRL-UAC-006, CTRL-TAC-002.
- User Requirements 4–5 → CTRL-UAC-001, CTRL-UAC-004.
- User Requirement 6 → CTRL-UAC-006 and CTRL-TAC-003–004.
- User Requirement 7 → CTRL-UAC-005, CTRL-UAC-010.
- User Requirement 8 → CTRL-UAC-007, CTRL-TAC-005.
- User Requirement 9 → CTRL-UAC-008, CTRL-TAC-006.
- User Requirement 10 → CTRL-UAC-009, CTRL-TAC-006.
- User Requirement 11 → CTRL-UAC-011, CTRL-TAC-002, CTRL-TAC-007.
- Technical Requirements 1–3 → CTRL-TAC-001.
- Technical Requirements 4–5 → CTRL-TAC-002, CTRL-TAC-006–007.
- Technical Requirements 6–7 → CTRL-TAC-003–004, CTRL-TAC-007.
- Technical Requirements 8–9 → CTRL-TAC-001–002.
- Technical Requirement 10 → CTRL-TAC-002, CTRL-TAC-004.
- Technical Requirement 11 → CTRL-TAC-005.
- Technical Requirement 12 → CTRL-TAC-006.
- Technical Requirement 13 → CTRL-TAC-004, CTRL-UAC-010.
- Technical Requirement 14 → CTRL-TAC-006.
- Technical Requirement 15 → CTRL-TAC-004.
- Technical Requirements 16–17 → CTRL-TAC-002, CTRL-TAC-007.

## Current Implementation and Acceptance Gaps

This section is non-normative current status, not fresh verification or a claim of code/spec alignment.

### Delivered and Reviewed

The retained CTRL-001 implementation deltas are delivered and independently reviewed Ready:

- Rig-independent actor-frame semantics (Technical Requirement 12), together with the fixture lateral-root correction
  and the availability-gated stop-degradation evidence.
- Stationary turning through authored pivots (Technical Requirement 11); the previously failing stick-yaw cases pass
  with assertions unchanged.
- Direction-reversal braking (Technical Requirement 14); the reversal cadence defect is closed by travelled-direction
  authored-exit routing.

### Known Boundaries

- Stop responsiveness (User Requirements 2 and 11, Technical Requirements 16–17) is implemented and independently
  reviewed against the 2026-09-30 baseline (`game/temp/ctrl001-stop-latency/run-01/report.md`): all 12 measured lanes
  stop within 400 ms / 200 mm, lateral lanes at 17 ms with 0 mm forward displacement, and stop rates are genuinely
  governed by the configured bounds. Every lane uses the early-Idle cut — root displacement is rate-invariant, so the
  200 mm bound is enforced by the cut plus the User Requirement 4 continuity clamp (Technical Requirement 16) — and
  the cut is reported. Foot slide during scaled braking is an accepted risk under the responsiveness-priority
  decision, for VR manual acceptance to judge.
- Lateral-to-lateral flips, for example right-to-left strafe, retarget in place; the reversal routing does not treat
  them as reversals and no test pins the boundary.
- Forward-to-pure-lateral intent routes through the authored exit as a domain flip — consistent with the domain model
  and uncovered by tests.
- A reversal costs the authored-stop duration before the opposite direction enters; a deliberate
  responsiveness-for-braking trade.
- Travelling turns ride the arc-blend channel.
- Stationary turning is available only when the wired tree has both turn states; otherwise the degradation is reported
  finitely (Technical Requirement 11).
- The stationary turn rate follows the authored pivot content (measured ~0.48 rad/s live versus the fixture's 1.87) —
  a content decision, not a runtime knob.

### Suite Redesign (2026-10-10)

By user decision on 2026-10-10, the NAV/CTRL integration suite was redesigned rather than optimised: the NAV/CTRL
implementation is incomplete with unresolved problems, the current `sad` ANIM-002 variant is placeholder content to be
replaced, and no male retarget setup exists. Expensive per-tick replay tests that encoded current placeholder
behaviour were deleted, not fixed; removal asserts no pass and fixes no behaviour. CTRL-001's criteria are unchanged
and normative; this entry records reduced shared automated evidence only.

CTRL-scoped effects:

- Retained unchanged: the playtest controller and startup-gate facts (17 facts covering startup/map/gate ownership,
  scene selection, input queuing, structured request logging, camera interactions, and destination-marker and
  via-authoring UI), and the unit-level control cadence, frame, contact, stop, and manual-ownership tests, which
  exercise player control rather than NPC animation output and are not superseded by the NAV spine. The retained
  explicit male pointer-rebinding fact is selection and bind coverage only, not retarget verification.
- NPC locomotion end-to-end automated evidence now flows only through the NAV-001 two-fact spine (female default rig);
  see [NAV-001 Current Status][nav-status].
- The post-redesign full windowed suite gate is 1213 passed / 0 failed / 1 skipped (one pre-existing, unrelated
  dynamic-rig fixture skip) in 11 m 36.8 s wall time, against the 29 m 11 s pre-redesign baseline — a reduction from
  deliberately smaller scope, not faster equivalent coverage.

### Remaining Acceptance Work

- Focused manual naturalness and positioning acceptance (User Requirement 7) on real selected content, including VR
  review of the bounded stop's naturalness and the accepted foot-slide risk (CTRL-UAC-002, CTRL-UAC-005); NPC local
  acceptance cannot close player outcomes.
- IK-003 player-rig foot-planting adoption, a separate future task; the planted-foot clauses of User Requirement 6 and
  CTRL-TAC-003 bind at adoption, not before it.
- Variant/performance checks and final validation against the acceptance criteria above.

### Carried Validation Notes

- The fixture models both lanes of a lateral side at ±0.744 while production's right-right lane authors 0.705 — an
  alignment note for any future exact-rate use of the fixture.
- ResourceSaver silently drops `.tres` header uids on scripted resave; any scripted asset mutation must re-verify the
  uid afterwards.
- Fixture turn states author ±π/4 yaw at fixture timing while production pivots author their own magnitudes —
  sufficient for sign and rate assertions; revisit before any rate-exact assertion.

## References

- [Project Specifications Index](../../index.md)
- [ANIM-002: Locomotive Animation Set](../../animation/002-locomotive-animation-set/index.md)
- [NAV-001: NPC Navigation](../../nav/001-npc-navigation/index.md)
- [IK-003: Leg and Feet IK System][ik-003]

[ik-003]: ../../ik/003-leg-feet-ik/index.md
[anim-scale]: ../../animation/002-locomotive-animation-set/index.md#motion-metadata
[nav-001]: ../../nav/001-npc-navigation/index.md#arrival-planner
[nav-status]: ../../nav/001-npc-navigation/index.md#current-status
