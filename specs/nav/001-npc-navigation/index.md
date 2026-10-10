---
id: NAV-001
title: NPC Navigation
---

# NPC Navigation

## Requirement

Define how an NPC follows a route and arrives precisely and naturally at a target position and heading using real
ANIM-002 clips, with feet that stay planted and no movement after completion.

## Goal

NPCs walk, side-step, and turn like people: they choose a sensible movement, arrive within tolerance, and stand still.
Many useful targets are closer than a complete authored start plus stop, so short moves must be built from parts of
real clips rather than from synthetic sliding.

The design keeps one arrival planner, one movement owner, one heading policy, and one completion rule. Distance and
heading are corrected only while a foot is in the air, so a planted foot never slides and no repair happens after the
move ends.

Where two real pieces meet, the join reads as one motion: the planner prefers fewer pieces and direct continuations,
cuts an existing clip before its slow tail rather than playing the tail, and, while exactly one foot is planted,
aligns the body across the join instead of letting the planted foot jump.

## User Requirements

1. Each accepted request ends with exactly one outcome: `Completed`, `Blocked`, `Failed`, `Cancelled`, or `Superseded`.
2. A completed NPC is within `0.01 m` of the target position and `1°` of the target heading. These defaults are
   configurable; relaxing them requires measurement and user agreement.
3. Movement choice looks purposeful:
   - a distant lateral target arcs into forward walking;
   - a nearby lateral target uses a side-step;
   - a target behind the NPC causes a turn before walking;
   - a stationary heading change turns in place as one continuous motion, without an unnecessary stop between its
     steps;
   - a heading change while travelling is walked as an arc, not performed as a stationary turn: the two are not
     interchangeable; and
   - final heading shapes the arrival, not the whole route.
4. Short moves, including moves shorter than a full start plus stop, heading-only requests, and combined
   move-and-turn requests, use real authored motion: a start/stop slice pair, a side-step, or a pivot.
5. Planted feet stay planted. Rolling about the ball of the foot, including heel lift, is acceptable; visible foot
   sliding, body sliding over planted feet, foot pops, warping, and body jerks are not.
6. After completion the NPC does not creep, slide, settle, or step.
7. Repeated requests, lateral requests, and turn-only requests complete like any other request.
8. The NPC follows a route through any via-points in order without stopping at them. At a sharp corner it may adapt
   its arc or slow and turn largely in place when an arc would look unnatural or leave the route. The Waypoint Route
   scenario verifies this.
9. An NPC that cannot reach a target ends the request honestly and finitely. It never shuffles, retries, or walks in
   place indefinitely.
10. Naturalness is a manual user judgement, supported but not replaced by automated evidence.
11. A request is a destination and facing, optionally preceded by an ordered list of via-points. Arrival accuracy and
    stillness apply only at the final destination; via-points are passed through, not stopped at.
12. A move uses as few real pieces as the clips allow. Unnecessary Idle bridges, filler motion before locomotion, and
    slow wind-down playback are avoided where a feasible direct continuation exists, and smaller aligned joins are
    preferred over larger ones. Bounded purposeful corrective steps may complete a simpler plan; they never relax
    arrival accuracy.

## Technical Requirements

### Ownership

1. NAV owns request lifecycle, route following (including via-point requests under Technical Requirements 18–19),
   movement choice, arrival planning, heading policy, completion, and outcomes. It adds no obstacle-planning subsystem
   and reuses existing route and collision responsibilities.
2. Exactly one movement owner moves the NPC actor. Foot planting ([IK-003][ik-003]) and animation never move the actor
   independently of it.
3. Each physics tick has one actor movement operation, including collision. Its input is the AnimationTree's evaluated
   root motion for the tick, consumed once, plus any swing-phase correction under Technical Requirements 8–10 and any
   seam body alignment under Technical Requirements 23–28, composed into one proposal.
4. One heading policy owns yaw for the whole request. No second component turns the actor.

### Arrival Planner

5. One arrival planner builds each move as a sequence of real ANIM-002 clips selected by direction, distance, and
   heading change using [ANIM-002 root-motion metadata][anim-metadata]:
   - longer moves: entry → forward or directional cycles → exit;
   - short moves: a slice pair, the head of an entry clip joined to the tail of the matching exit clip (for example
     the head of `entry_forward_*` joined to the tail of `exit_forward_*`), or the equivalent for another direction;
   - nearby lateral positioning: a side-step;
   - heading-only or turn-first requests: one or more whole or partial pivots; and
   - **Plan ranking and composition:** feasible plans are ranked for naturalness and stationary turns are composed
     under Technical Requirements 21–22.
6. A slice pair is chosen by direction and distance together. The join occurs where the same anatomical foot is planted
   in both slices according to the [ANIM-002 per-foot planted intervals][anim-metadata], so the joined motion keeps a
   consistent support foot.
7. The planner predicts the endpoint of its sequence from metadata and replans only at bounded events such as request
   start, step boundaries, and waypoint changes. It performs no per-frame package parsing or unbounded search.
   - Root predictions use the [ANIM-002 root trajectory tables][anim-metadata], scaled by the bound skeleton's
     `motion_scale` under the ANIM-002 scale rule; yaw is unscaled. Until the pipeline publishes the tables, the
     planner uses the ANIM-002 interim root derivation at bind time.
   - **Ball-joint trajectories:** seam choice compares ball-joint positions. These depend on each rig's bone
     proportions, so they are rig-specific runtime data, not ANIM-002 package metadata. Once at bind time, never per
     frame, the planner evaluates each clip on the bound skeleton and tabulates, for each anatomical foot, the ball
     joint's position (defined by [IK-003 Foot Planting][ik-003-planting]) relative to the root, at the clip's native
     key rate and in that rig's runtime units. The table declares an error bound against the evaluated post-FK ball
     positions. It is never derived by scaling source units. The tables require both toe bones; if either is missing,
     navigation binding fails with a clear error.
   - **Planner set:** the seam table covers only the clip pairs and seam kinds the arrival planner uses to produce
     natural NPC navigation. The set is a planner design decision and may change. ANIM-002 clips or pairs outside it
     are excluded from planning; they are not defects, and the bind-time seam report lists them as excluded, distinct
     from unavailable.
   - **Seam alignment:** a seam is every crossfade the planner issues within its set: entry → cycle, cycle → cycle
     lane switches, cycle → exit, slice joins, manoeuvre ↔ Idle, and same-role pivot re-entry. A seam `(a, b)` names
     the outgoing clip's playback position `a` on the tick the transition is requested and the incoming clip's seek
     position `b`. On the
     entry tick the outgoing clip advances one physics tick while the incoming clip starts at `b`, so alignment is
     evaluated over the window the runtime actually plays: the outgoing clip from `a + dt` and the incoming clip from
     `b`, for the pair's crossfade duration minus `dt`, with both playback positions advancing through the blend, not
     only at the switch instant. Seam tables are therefore valid for one physics delta `dt`. `a` and `b` are chosen
     so that the window satisfies both parts of alignment:
     - **Phase agreement (strict, not tunable):** at every tick of the window, both clips mark every foot with the
       same planted or swing state under their [ANIM-002 per-foot planted intervals][anim-metadata], at their playback
       positions, so both clips are at the same stride phase.
     - **Pose agreement (tunable):** at every tick of the window, the separation between the two clips' ball-joint
       positions for each foot, root-relative, stays within the seam tolerance. Its default is derived from measured
       seam separations of the planner set only, with the existing clips, and reported to the user for agreement. Like
               the arrival tolerance of User Requirement 2, it is not relaxed without measurement and user agreement.
   - **Crossfade durations:** the crossfade duration may differ per clip pair in the planner set. It is tunable, with
     a base default and a floor. The AnimationTree's authored crossfade for each planner-set pair equals the duration
     the seam table uses for that pair, and no NPC transition is authored outside the planner set.
   - **Same-role re-entry:** a planner-set pair whose outgoing and incoming clips share one pivot role (a same-role
     pair, or self-pair) is realised in the authored graph through a continuation alias state: a second authored
     state that loops the same canonical clip as the role's canonical pivot state. The state machine cannot author a
     `from == to` transition, and travelling to the current state never re-enters it, so the alias arc is the
     authored realisation of the pair's crossfade. The alias arc carries the pair's crossfade duration, and the
     planner-set, alignment, and duration checks of this requirement assess the canonical and alias states of one
     role as that one pair at the level of the logical role. Native looping within one segment (Looping confined
     manoeuvres) is distinct: it plays on within a single state and issues no transition.
   - **Required continuation states:** every NPC rig's AnimationTree authors a continuation alias state for each
     pivot role — `NPCPivotLContinuation` and `NPCPivotRContinuation` — each looping the same clip as its canonical
     pivot state. Navigation binding fails with a clear error if either state is missing or does not loop its
     canonical clip: a rig without them cannot materialise same-role pivot re-entry, and no degrade path exists.
   - **Looping confined manoeuvres:** a side-step or pivot whose ANIM-002 `loop_intent` is true is played as looping.
     The planner may run a whole step from one double-support phase, across the authored loop point, to the next.
   - **Exits:** exit clips are selected by the current stride phase, not by the lane used on entry. An exit starts only
     at a seam time that satisfies alignment.
   - **No aligned seam:** a candidate seam that fails either part of alignment is excluded. The planner then uses other
     options: whole clips, side-steps, pivots, and corrective steps within budget. An unaligned crossfade is never
     issued. If no aligned plan remains, the request ends `Failed` under Technical Requirement 13.
   - **Cyclic clip availability:** at bind time, the planner's seam table determines, for each cyclic clip and each
     looping confined manoeuvre in the planner set, its aligned exit seams under this alignment rule, to any exit lane
     in the set selected by phase. Such a clip with no aligned exit seam in each cycle or loop is unavailable: the
     planner never enters it, and the bind-time seam report lists it. Every cyclic clip or looping confined manoeuvre
     the planner enters therefore has at least one aligned exit seam per cycle or loop.
   - **Bind-time seam report:** the report lists the planner set and the excluded clips and pairs, and records the
     seam tolerance in force, the measured seam-separation data it was derived from, the user-agreement record, and
     each pair's crossfade duration. It also flags each seam kind that the pair's crossfade duration makes impossible,
     because the window cannot fit within a same-phase segment of both clips, so that its absence is not mistaken for
     a planner defect.

### Correction and Completion

8. The residual between the predicted endpoint and the target, in position and heading, is corrected only while a foot
   is swinging. Correction is spread over the remaining swing phases and capped per step.
9. There is no correction while both feet are planted, on a frozen or finished pose, or in Idle. No settlement stage
   exists. Seam body alignment (Technical Requirements 23–28) is zero in exactly these states and phases.
10. The swing/planted state that gates correction comes from the ANIM-002 per-foot planted intervals of the clips
    being played, combined under the [ANIM-002 Planted Combination Rule][planted-rule], the same state
    [IK-003 foot planting][ik-003] uses. The intervals grant nothing; NAV alone decides and applies correction.
11. If the residual still exceeds tolerance when the planned sequence ends, the planner adds purposeful corrective
    steps, each a side-step or pivot, corrected under the same swing-only rule. Their number is small, bounded, and
    tunable.
12. A request is `Completed` when the collision-realised position and heading are within the configured tolerance and
    the final clip has returned to Idle. The planner's internal tolerances equal the configured spec tolerance.
13. After `Completed`, NAV applies no actor movement. `Blocked` means the route or target cannot be reached because of
    an obstruction. `Failed` means the target is reachable but still outside tolerance after the corrective steps, no
    aligned plan remains under Technical Requirement 7, or the request makes no progress within a bounded time and
    work budget. Replanning resets neither the corrective-step count nor the time and work budget.
14. Outcomes are exact-once and first-terminal-wins. Cancellation and supersession are prompt and stop or redirect the
    NPC through the same planner, using real clips and the swing-only correction rule; there is no settlement stage.
    - **Prompt:** the stopping or redirecting transition begins at the first aligned seam time under Technical
      Requirement 7. No motion discontinuity, such as a pose snap, root-motion jump, or unaligned crossfade, is used
      to begin it sooner.
    - **Promptness bound:** the tunable default bound on that delay is one stride cycle of the current clip. For a
      cyclic clip this is the clip's cycle duration; for a looping confined manoeuvre under Technical Requirement 7 it
      is the time until its next aligned seam, bounded by one loop duration; for any other non-cyclic clip (entry,
      exit, non-looping side-step or pivot, or slice) it is the remaining duration of that clip until its next aligned
      seam or its end. Alignment takes precedence: if the first aligned seam falls later than the bound, the
      transition still waits for it. Exceeding the bound is report-only, not a failure.
    - **No aligned seam before the end:** if a finite current clip has no aligned seam before its end, it plays to its
      end and continues through its authored successor, the clip boundary it was planned to hand off to (for example
      entry → cycle or exit → Idle), until the first aligned seam. The delay is reported. The rule applies to finite
      clips only: under the cyclic clip availability rule of Technical Requirement 7, every cyclic clip or looping
      confined manoeuvre the planner enters has an aligned exit seam in each cycle or loop.
    - A replacement request plans from the current collision-realised state, and A2 applies from the superseded
      request's issue through to completion of the replacement.

### Animation Tree Use

15. The AnimationTree is used as designed: a statically authored graph driven through its parameters. There is no
    runtime graph surgery, no parallel blend model, and no time-stretching of clips to a shared cadence, whether by
    blend-space sync or by custom timelines.
16. Clip slices are played through standard AnimationTree playback controls on the authored graph.
    - **Crossfade timing:** a crossfade is requested only at a seam `(a, b)` chosen under the seam alignment rule of
      Technical Requirement 7: on the tick the outgoing clip is at `a`, the incoming clip seeks to `b`. The crossfade
      uses the pair's authored duration, so it runs for the window that alignment was evaluated over.
    - **Same-role re-entry:** a planned segment that re-enters the pivot role currently playing is issued as a
      crossfaded transition to that role's continuation alias state (Technical Requirement 7), alternating the
      canonical and alias physical states. The crossfade timing and mid-clip entry rules of this requirement apply
      unchanged, so re-entry is a blended seam at a planned seek rather than a playback reset, and no pose teleport
      occurs.
    - **Mid-clip entry:** entering a clip at a slice start may use a same-tick, zero-delta pre-evaluation of the
      AnimationTree (`Advance(0)`) before the seek takes effect. This is sanctioned use under Technical Requirement 15.
    - **Entry tick:** the entered clip is evaluated at the slice start on the entry tick itself, so there is no pose
      pop, and the entered clip contributes zero root motion on that tick. During a crossfade, the outgoing clip's
      weighted root-motion contribution continues normally on the entry tick. The seek never integrates root motion
      across the jump.
    - **Final evaluation only:** the pre-evaluation leaves an intermediate state with the entered clip at position
      `0`. Only the tick's final AnimationTree evaluation is observed: by the [IK-003 foot-planting][ik-003]
      provider's input pose, by planted-state evaluation under the [Planted Combination Rule][planted-rule], by the
      actor movement operation's root-motion read under Technical Requirement 3, and by the Technical Requirement 17
      recording. The pre-evaluation is not a separate tick and produces no observable sample: no IK solve, no
      planted-state step, no recorded sample, and no root-motion read.

### Validation Instrumentation

17. The navigation test scene (`game/assets/testing/navigation/navigation_test.tscn`) records per tick: request
    identity, planned sequence and current clip, per-foot ANIM-002 planted state, pre-IK and post-IK ball points (the
    ball joint defined by [IK-003][ik-003]), the tick's movement decomposition — consumed root motion, seam-alignment
    contribution, and residual-correction contribution, recorded separately with their combined totals against the
    configured budgets — the per-foot contact identity and retained pin consumed from the
    [IK-003 contact observations][ik-003-contact], collision-realised actor position and heading, via-points passed,
    residual, predicted reach margin, and outcome. It also records the AnimationTree graph state (blend parameters,
    transition progress, and each clip's playback position) so that the A2 and NAV-TAC-008 measurements compute
    effective clip weights and planted state under the [Planted Combination Rule][planted-rule] from the recording,
    independently of the foot-planting code. Post-IK ball points come from the IK-003 post-solve capture; planted-foot
    yaw verification is normatively [IK-003][ik-003]'s (its Technical Requirement 26), and A2 remains planar. Recording
    continues for `1 s` after completion. Screenshots are optional diagnostics.

### Requests and Via-Points

18. A request carries a destination, a final facing, and an optional ordered list of via-points. The planner plans the
    path through each via-point's pass radius in order, then to the destination. A via-point counts as passed when the
    NPC comes within that tunable radius; it is never a stopping point, and no stop clip, completion, or arrival
    correction occurs there. A missed via-point is not a reason to backtrack or stop.
19. Arrival rules (Technical Requirements 11–13, A1, and A3) apply only at the final destination. Final facing shapes
    only the final approach. The corrective-step count and time and work budget cover the whole request.

### Test Scene Route Authoring

20. In the navigation test scene, shift-click drops a visible waypoint marker on the ground and queues it as a
    via-point. A normal click, with drag for facing, sets the destination and issues the request with the queued
    via-points. Starting a new route clears the previous markers.

### Plan Selection and Composition

21. The planner ranks feasible plans and issues a minimal-cost one from its bounded candidate set.
    - **Feasible:** every join is an aligned seam under Technical Requirement 7, the sequence is playable through the
      authored AnimationTree controls, and the predicted endpoint is reachable within the configured tolerances using
      the combined bounded swing correction (Technical Requirements 8–10) and the bounded purposeful corrective steps
      of Technical Requirement 11. An exactly matching uncorrected endpoint is not required, and a passing seam table
      alone is not sufficient.
    - **Naturalness cost:** lower cost means purposeful movement with fewer unnecessary pieces, fewer Idle bridges,
      less slow-tail playback, and smaller joins, measured by the seam pose separation of Technical Requirement 7
      within the configured tolerance. Cost terms and weights, and any slow-tail detector, are tunable configuration
      with declared measurements behind them; diagnostic thresholds are not acceptance bounds.
    - **Repeated-pivot generation:** chains of repeated pivot segments (Technical Requirement 22) are generated per
      request and enter the normal ranking path with no priority over bind-time families; every composed chain is
      charged to the shared composition budget, and a chain whose yaw residual alone cannot reach the finalists'
      threshold is skipped before composition, exactly as the travel families pre-filter theirs. The bind-time seam
      menu keeps its legacy capacity and contents — no slot is reserved or evicted, and per-request generated
      recipes never displace bind-time ones. Chain duration estimates omit entry seeks under the existing family
      convention, but re-materialised finalists are predicted exactly, so ranking decides on exact values.
    - Ranking never trades away arrival accuracy: tolerances, budgets, and corrective-step bounds are constraints, not
      cost terms. It never eliminates an authorised movement family (slice pairs, side-steps, pivots, arcs) to satisfy
      a test; small sliced moves and pivots remain available where genuinely required, and smooth continuations are
      preferred over avoidable resets to Idle.
    - Ranking applies to plan selection only. It never delays or overrides the promptness rule of Technical
      Requirement 14: cancellation and supersession still begin at the first aligned seam, and the finite-end and
      corrective-step requirements are unchanged.
22. Stationary turns and walking arcs are not interchangeable: a stationary heading change turns in place with pivots;
    a heading change while travelling is walked as an arc.
    - **Stationary-turn continuity:** the successive pivot pieces of one stationary turn join through aligned seams as
      one continuous motion. The planner does not insert an Idle return between turn units (for example after each
      `45°` pivot) unless no aligned join between the pieces exists, in which case the no-aligned-seam options of
      Technical Requirement 7 apply.
    - **Repeated same-role chains:** a stationary turn may be planned as a homogeneous chain of restart units of one
      looping pivot's self seam: Idle → entry → N ≥ 2 pivot segments of that role, each joined to the next at the
      self seam through Technical Requirement 7's same-role re-entry → Idle. The family is admitted only when the
      requested heading change exceeds three native cycles of the role and matches the role's turning direction.
    - **Phase-complete unit admission:** a restart unit is admitted only when its advancing incoming window — the
      restart crossfade, the native solo back to the seam's outgoing time, and the entry seek — fully contains one
      swing interval of each foot per the clip's planted-interval metadata: a complete alternating swing cycle. A
      window covering only a single foot's swing is not a restart unit.
    - **Shared native reserve:** per chain, each additional restart and each explicit extra native loop (placed
      after the last restart) is charged against the same extra-native reserve — `MaxPivotLoops` native loops' worth
      of both yaw and time — with every unit's entry seek included on the time axis. A necessary wrap inside a
      partial segment is not a charged extra loop.
    - **Truthful materialisation:** each repeated segment derives its own exit-cycle count from its real fade, solo,
      and extra-loop elapsed; counts are never copied between segments, and a wrapped partial plays with zero extra
      cycles. `Steps` keeps its segments-plus-loops meaning; no count is relabelled as a swing count.
    - **Slow-tail slicing:** a pivot may be sliced before a wind-down tail and joined to the next piece at an aligned
      seam, so the tail is not played. A sliced join is issued only when its complete crossfade satisfies both parts
      of alignment under Technical Requirement 7, the composed schedule is geometrically feasible under Technical
      Requirements 29–31, and it is playable through the authored AnimationTree controls of Technical Requirements
      15–16. Joins enabled this way, such as pivot → pivot or pivot → entry, may be added to the planner set and seam
      table. The statically authored graph edges may change to enable them; there is no runtime graph surgery, no
      second blend controller, no new clips, no time-stretching, and no change to native clip playback speeds.

### Seam Body Alignment

23. NAV alone computes, budgets, and applies seam body alignment: a bounded, NAV-owned actor translation that
    supports the feasibility of a planted foot's retained world pin across a join, instead of the pin being moved or
    the foot sliding. Alignment is not an exact pre-IK coincidence guarantee between the native ball point and its
    pin; three layers stay distinct:
    - the **ideal native-ball demand** is the exact planar offset of Technical Requirement 27, a geometric target;
    - the **applied alignment** is the bounded actor displacement actually scheduled under the shared budgets of
      Technical Requirement 28; and
    - the **post-IK pin constraint** is what the IK-003 planted hold then achieves.
    A remaining native-ball offset after the applied displacement is permitted only where the combined schedule keeps
    the retained pin reachable and continuous and A2 still holds with its bound unchanged; it never authorises an
    instant full-demand shift, a cap relaxation, or an A2 relaxation. IK-003 supplies read-only committed
    [contact observations][ik-003-contact]; it never issues a movement request, `CharacterIK` remains the sole
    foot-target actuator, and IK-003 gains no hips or actor authority ([IK-003][ik-003] Technical Requirements
    16–20). Alignment is NAV-owned movement under Technical Requirements 1–4 and the single movement operation of
    Technical Requirement 3.
24. Alignment is eligible only on a tick where exactly one foot is planted under the [Planted Combination
    Rule][planted-rule] and that foot has a retained, usable anchor (Technical Requirement 25). It is exactly zero:
    - while both feet are planted (double support), which includes every strict phase-aligned Idle join, because Idle
      marks both feet planted throughout;
    - in Idle, on a frozen or finished pose, and after completion; and
    - while both feet swing, which has no planted anchor; the existing flight-phase residual correction is unchanged.
    Where an aligned Idle join would need body alignment, the planner selects a feasible alternative plan or schedules
    preparatory correction earlier during eligible single-support motion. No exception to A4 exists.
25. NAV retains contact and release history across animation seams, plan changes, requests, and completion. Nothing
    resets at request start, and no replant is manufactured to create an anchor. A usable anchor requires that the
    foot's latest committed IK-003 contact snapshot:
    - is actively holding: the solver state is hold. Releasing — including an unfinished release blend —
      release-complete, and free states are excluded, so a pin reach-released by IK-003 while metadata still marks
      the foot planted is never a usable anchor;
    - was committed by the immediately preceding completed physics tick (the tick order of Technical Requirement 26),
      with the foot's metadata contact under the [Planted Combination Rule][planted-rule] continuous from that
      snapshot's planted interval through the current tick; and
    - carries the interval, binding, and reset identities matching the foot's current continuous planted interval
      and the rig, and a finite retained pin.
    Missing or stale history grants no alignment. History is preserved; it is never reset or recaptured to regain
    permission. A new contact has no usable anchor before its post-movement IK commitment: its acquisition tick gains
    no alignment through provisional capture, and it becomes eligible on a following tick only if every condition
    above holds. The previous committed snapshot is history: the current tick's final native pre-IK pose is the only
    pose input, never the snapshot's pose ([IK-003][ik-003] Technical Requirement 19).
26. The tick pose order is exactly:
    1. the AnimationTree's final native evaluation of the tick, never an intermediate `Advance(0)` state (Technical
       Requirement 16);
    2. one root-motion read, consumed once (Technical Requirement 3);
    3. one composed NAV movement proposal for the tick — root motion, seam alignment, and endpoint-residual
       correction together — computed from the current actor-relative pre-IK pose and the retained history;
    4. one collision resolution of that proposal; and
    5. IK-003 commits each capture and release from that same pose under the realised transform (its Technical
       Requirement 20): new contacts are captured after the tick's movement, never provisionally before it.
27. Alignment geometry is signed and frame-explicit. The reference transform `T` is the tick's pre-alignment
    candidate actor world transform: the composition of the tick's evaluated root motion, NAV's planned heading, and
    the candidate residual correction — including that correction's yaw-pivot origin displacement, counted exactly
    once — before that candidate's seam-alignment translation is added. Alignment is never included in `T` and then
    applied again. With the actor-relative native ball point `b_i` of the planted foot from that tick's final pre-IK
    pose, and the persistent world-planar pin `P_i` retained for that foot, the exact planar demand is
    `d_i = P_i − XZ(T · b_i)`, where `XZ` projects onto the world horizontal plane. `d_i` is the ideal native-ball
    demand of Technical Requirement 23 — a geometric demand, not permission for an instantaneous full shift:
    - Alignment is applied as a bounded continuous schedule through the join, with no snap on entry and no snap-back
      on exit.
    - Root motion, alignment, and residual correction are jointly accounted: the composed proposal must keep pins and
      reach feasible, residual correction may not cancel alignment without checking the resulting pin and reach
      geometry, and a yaw pivot's origin shift is counted exactly once. Changing the candidate residual correction
      changes its pre-alignment reference consistently, and the final schedule jointly checks and caps both
      contributions.
    - Two planted feet generally demand different translations: violations are not averaged away, one foot is not
      silently chosen, reach is not enlarged, and pins are not released or replanted to manufacture phase permission.
      Double support keeps zero alignment regardless.
28. Alignment and residual correction are charged together:
    - Both draw on the same per-step and per-request synthetic movement budgets, with no fresh allowance at a seam or
      replan. Their contributions are recorded separately, and their combined total is what A4 and the per-step cap
      evidence check. Numeric caps remain configuration; this specification sets none.
    - The collision-realised transform, not the proposal, feeds every subsequent residual, pin, reach, and completion
      check, and no second collision-bypassing translation is applied.

### Prediction and Feasibility

29. Plan prediction carries the complete planned synthetic movement schedule — per-tick root motion, alignment, and
    residual correction — together with the retained contact and release history, not only sequence endpoints.
    Feasibility and naturalness ranking evaluate that combined schedule.
30. Predicted geometry is bound-rig native-pose-compatible: ball positions, foot orientation and ankle transforms,
    upper-leg positions, foot-local ball offsets, and chain reach, each with a declared, validated error bound. It
    extends the bind-time rig sampling of Technical Requirement 7 and the [ANIM-002 scale rule][anim-metadata]; it
    introduces no second playback controller and no new package format.
    - Linearly point-blended ball positions alone are not reach evidence: feasibility is verified against
      independently observed final native pre-IK poses.
    - Body translation does not change root-relative seam pose separation, so it cannot legitimise an otherwise
      unaligned join.
31. Reach and feasibility:
    - Ankle-target reach is evaluated per [IK-003][ik-003] Technical Requirement 14, yaw-compensated about the ball,
      for both legs, over the complete transition and the resulting stance, including the first pure incoming sample
      (the first tick the incoming clip alone drives the pose). A fixed ball is not an unchanged ankle reach.
    - A candidate schedule whose combined movement leaves any retained pin or ankle target unreachable is infeasible
      and is rejected. The planner then uses bounded feasible alternatives and corrective steps, keeps honest
      `Blocked` for obstruction and `Failed` for exhausted or no-aligned-plan cases under Technical Requirement 13,
      and never weakens its finite lifecycle. IK-003 reach release remains a safety fallback, never an A2 success.

Tuning values such as correction caps, the corrective-step count, the via-point pass radius, budgets, the seam
tolerance, the per-pair crossfade durations and their base default and floor, the NAV-TAC-008 swing-travel bound, the
promptness bound, the plan-ranking cost weights, slow-tail detection thresholds, and the seam-alignment schedule
bounds are configuration, not spec constants. Implementation still delivers a documented default for each, with the
measurements that justify and validate it; leaving exact values configurable does not defer that delivery or its
validation. The seam tolerance is also subject to the measurement and user-agreement rule of Technical Requirement 7.
Phase agreement is not a tuning value.

## In Scope

- Request lifecycle, route and via-point following, movement choice, arrival planning, correction, completion, and
  outcomes.
- Short moves through slice pairs, side-steps, and pivots of existing ANIM-002 clips.
- Naturalness-ranked plan selection, stationary-turn composition, and slow-tail slicing within existing ANIM-002
  clips.
- NAV-owned single-support seam body alignment, its contact-history, prediction, and feasibility contracts, and their
  validation instrumentation.
- Integration with IK-003 foot planting on the NPC rig.
- The navigation test scene, its route-authoring controls, and its outcome instrumentation.

## Out Of Scope

- Player input and movement policy ([CTRL-001][ctrl-001]).
- Obstacle planning, off-mesh traversal, terrain adaptation, generic motion matching, and live variant switching.
- New short-step or turn clips. These may be considered only as a measured fallback after the user approves them.
- Synthetic body alignment in double support, Idle, frozen, or finished states; a wider alignment policy needs a
  separate user decision.
- Retuning the arrival tolerance or relaxing the seam tolerance without measurement and user approval.

## Acceptance Criteria

### Scenarios

All acceptance criteria apply to these scenarios in the navigation test scene, except as noted below the table:

| Scenario | Request |
| -------- | ------- |
| Oblique | `0.79 m` at a bearing `39°` left of forward, final heading `39°` left. |
| Terminal Slide | `1.50 m` at a bearing `19°` right of forward, final heading `19°` right. |
| Terminal Slide Mirror | Terminal Slide mirrored about the actor's forward axis. |
| Repeated Requests | Oblique; on its completion tick, the Oblique target plus `0.3 m` along its final facing. |
| Lateral | `0.5 m` to the side, unchanged heading. |
| Distant Lateral | About `3 m` to the side, unchanged heading. |
| Behind | `1.5 m` behind, unchanged heading. |
| Turn-Only | `90°` heading change without translation. |
| Turn-Only Reverse | `180°` heading change without translation. |
| Long Straight | `4 m` straight ahead, unchanged heading. |
| Waypoint Route | From the origin facing `-Z`: via-point `(0, 0, -2)`, destination `(2, 0, -2)`, final facing `+X`. |

The Waypoint Route scenario is an L-shaped route with a sharp corner. It is checked against A1–A4 and judged by the user
under A6; A5 does not apply to it.

### Parent Acceptance Criteria

These criteria must not be weakened.

1. **A1:** Every scenario reports `Completed` within `0.01 m` and `1°`.
2. **A2:** Every planted run of each foot, from request issue to `1 s` after completion, is evaluated; the worst run's
   slip is at most `5 mm`.
   - **Planted run:** a continuous run of ticks on which the foot is planted under the ANIM-002
     [Planted Combination Rule][planted-rule], never the foot-planting implementation's own planted state. Each run
     has its own anchor. A split run, even by a single-tick flicker, restarts the anchor at the start of the new run.
   - **Slip:** the greatest horizontal (planar world XZ) distance of the post-IK ball joint from its position at the
     start of the run, that is, the maximum excursion from the planted-start position.
   - **Not slip:** rotation about the ball joint, including heel lift, and vertical motion.
   - **Report-only:** summed per-tick path length and net start-to-end displacement per run are diagnostics, not
     criteria.
3. **A3:** Actor movement after completion is zero, from completion to `1 s` after.
4. **A4:** No synthetic actor correction is applied while both feet are planted.
5. **A5:** Movement choice is correct: distant lateral targets arc into forward walking, nearby lateral targets
   side-step, and targets behind turn first.
6. **A6:** The user judges the result natural in the test scene.

### Technical Requirement Acceptance

1. **NAV-TAC-001:** Traces show one movement owner, one actor movement operation per tick, root motion consumed once,
   and one heading policy. On the entry tick of every mid-clip slice entry, the recorded playback position of the
   entered clip equals the slice start (no pose pop) and the entered clip contributes zero root motion (no root-motion
   jump); during a crossfade from seam `(a, b)`, the outgoing clip's recorded playback position on the entry tick is
   `a + dt`, and the tick's root motion equals the outgoing clip's normal weighted contribution. On every
   such entry tick there is exactly one IK solve, one planted-state step, one recorded sample, and one root-motion
   read, and all of them come from the final evaluation only: the IK-003 provider's input pose, the planted state
   under the Planted Combination Rule, and the recorded sample all reflect the entered clip at the slice start, never
   the pre-evaluation state at position `0`.
2. **NAV-TAC-002:** Traces show each request's planned clip sequence, and short scenarios use a slice pair, side-step,
   or pivot joined where the same foot is planted in both slices. Replanning occurs only at the bounded events of
   Technical Requirement 7, and replanning never resets the corrective-step count or the time and work budget.
   Predicted root distances are scaled by the bound skeleton's `motion_scale` under [ANIM-002-TAC-006][anim-002]. On
   each reference rig, the bind-time ball-joint table matches the evaluated post-FK ball positions on that rig,
   relative to the root, within its declared error bound, is built once at bind time, and is not scaled from source
   units. A rig missing either toe bone fails navigation binding with a clear error.
3. **NAV-TAC-003:** Correction appears only on ticks where a foot is swinging under the [Planted Combination
   Rule][planted-rule], and stays within the configured per-step cap. Seam body alignment appears only on ticks where
   exactly one foot is planted under that rule (Technical Requirement 24), and its contribution together with residual
   correction stays within the configured per-step cap; A4 and cap evidence check the combined total. A2 evidence is,
   per planted run, the maximum planar excursion of the recorded ball point from its planted-start position, as
   defined by A2. Planted state and effective clip weights for that evidence are computed from the recorded graph
   state and animation positions, independently of the foot-planting code.
4. **NAV-TAC-004:** Completion uses the configured tolerance directly, with no stricter or looser internal value, and
   no correction occurs after the final clip returns to Idle.
5. **NAV-TAC-005:** Corrective steps stay within the configured count. An obstructed request ends as `Blocked`; a
   reachable request still outside tolerance after its corrective steps, left with no aligned plan under Technical
   Requirement 7, or making no progress within its budget, ends as `Failed`. Cancellation and supersession are
   exact-once and stop through the planner with no settlement stage. They are prompt under Technical Requirement 14:
   traces show the transition beginning at the first aligned seam time after the cancellation or supersession, with
   no motion discontinuity before it, and report the delay against the configured promptness bound. A delay beyond
   that bound is reported, not failed. Where a finite current clip had no aligned seam before its end, traces show it
   playing to its end and continuing through its authored successor until the first aligned seam, with the delay
   reported.
6. **NAV-TAC-006:** An inspection confirms the AnimationTree graph is not modified at runtime, no parallel blend model
   exists, and clips are not time-stretched to a shared cadence by blend-space sync or custom timelines. Mid-clip
   entry uses only the sanctioned zero-delta pre-evaluation of Technical Requirement 16.
7. **NAV-TAC-007:** In the Waypoint Route scenario, traces show via-points passed in order within the configured radius
   (a missed via-point fails this criterion), no stop clip, completion, or arrival correction at any via-point, and
   arrival rules applied only at the final destination. A test-scene check proves shift-click queues a visible marker, a
   normal click issues the request with the queued via-points, and a new route clears old markers.
8. **NAV-TAC-008:** For every crossfade the planner issues in every scenario (entry → cycle, cycle → cycle lane
   switches, cycle → exit, slice joins, manoeuvre ↔ Idle, and same-role pivot re-entry), at every tick of the
   crossfade window actually played
   (the outgoing clip from `a + dt` and the incoming clip from `b`, for the pair's duration minus `dt`):
   - **Phase agreement:** both clips mark every foot with the same planted or swing state under their ANIM-002
     intervals at their recorded playback positions;
   - **Pose agreement:** for each foot, the separation between the two clips' ball-joint positions, taken from the
     bind-time ball-joint tables at the recorded playback positions, stays within the configured seam tolerance. This
     check uses the planner's own bind-time tables; independent pose evidence comes from the swing-travel check and
     A2; and
   - **Swing travel:** the pre-IK swing-foot ball-joint travel does not exceed the travel predicted by the two clips'
     ball-joint trajectories, blended by their effective weights, by more than a tunable bound.
   - **Frame and independence:** pre-IK ball points are compared root-relative: each recorded world position is
     expressed in that tick's recorded actor frame (its position and heading), matching the ball-joint tables. The
     measurement uses the recorded pre-IK ball points, graph state, and playback positions of Technical
     Requirement 17, independently of the foot-planting code.
   - Traces also show that no unaligned crossfade was issued, and that a request left with no aligned plan ended
     `Failed`.
   - **Cyclic clip availability:** on each reference rig, the bind-time seam report lists, for every cyclic clip and
     looping confined manoeuvre in the planner set, its aligned exit seams per cycle or loop, and lists each such clip
     that has none as unavailable. Traces show the planner never enters an unavailable clip.
   - **Planner set:** the bind-time seam report lists the planner set and, separately from unavailable clips, the
     excluded ANIM-002 clips and pairs. Traces show the planner never issues a crossfade outside the set.
   - **Authored crossfades:** the authored crossfade is the per-transition `xfade_time` of an
     `AnimationNodeStateMachineTransition` in the NPC AnimationTree state machine. Arcs map to pairs at the level of
     the logical role, so a same-role pair's continuation alias arc is that pair's transition. An inspection fails
     if a planner-set pair has no such transition, if any authored NPC arc, canonical or alias, has an `xfade_time`
     that differs from the seam table's duration for its pair, or if an NPC transition exists outside the planner
     set.
   - **Required continuation states:** an inspection of each reference rig confirms both continuation alias states
     are authored and each loops the same clip as its canonical pivot state. A rig whose AnimationTree lacks either
     fails navigation binding with a clear error, and its runtime binding never activates.
   - **Seam report contents:** the bind-time seam report contains the seam tolerance in force, the measured
     seam-separation data of the planner set it was derived from, the user-agreement record, each pair's crossfade
     duration, and a flag for each seam kind made impossible by that pair's crossfade duration.
9. **NAV-TAC-009:** Plan selection and composition traces, in every scenario:
   - the issued plan is feasible under Technical Requirement 21, and the planner's recorded candidate costs show it is
     a minimal-cost feasible plan within the evaluated candidate set under the declared configuration;
   - no authorised movement family is unavailable in selection; small sliced moves and pivots still appear where
     genuinely required;
   - the Turn-Only scenarios (`90°` and `180°`) show stationary turns composed through aligned seams without an
     intervening Idle return, except where no aligned join exists and the Technical Requirement 7 fallback applies;
   - a repeated chain traces its per-chain reserve ledger — restart units and explicit extra loops charged jointly on
     the yaw and time axes, entry seeks included — its phase-complete unit coverage of both feet, its per-segment
     derived exit cycles, and the exact finalist prediction of the issued chain, with no Idle between its turn units;
   - any pivot sliced before a slow tail joins at an aligned seam and is played through authored controls, with no
     new clip, time-stretch, or native-speed change (checked with NAV-TAC-006 and NAV-TAC-008), and any joins added
     to the planner set carry authored crossfades under Technical Requirement 7;
   - traces show no avoidable filler pieces before locomotion and no avoidable Idle bridges between pieces; these
     structural checks support, and do not replace, the A6 user judgement; and
   - promptness is unchanged: cancellation and supersession transitions still begin at the first aligned seam
     (NAV-TAC-005).
10. **NAV-TAC-010:** Seam body alignment traces show, per tick:
    - alignment is non-zero only when exactly one foot is metadata-planted with a retained usable anchor, and exactly
      zero in double support (including strict phase-aligned Idle joins), Idle, frozen or finished states, and after
      completion; both-feet-swing ticks keep the unchanged flight-residual correction, recorded separately;
    - the usable-anchor rule of Technical Requirement 25 holds per tick: alignment appears only when the foot's
      latest committed snapshot is actively holding, was committed by the immediately preceding completed physics
      tick, and has uninterrupted metadata contact through the current tick with matching interval, binding, and
      reset identities and a finite pin; a reach-released pin within a still-planted interval, an unfinished or
      completed release, a stale or missing snapshot, and a new contact's acquisition tick before its post-movement
      commitment each show zero alignment, with no reset or recapture used to restore permission;
    - exactly one root-motion read and one composed movement proposal per tick, collision-resolved once, with no
      second collision-bypassing translation, and subsequent residual, pin, reach, and completion checks using the
      collision-realised transform;
    - the ideal planar demand `d_i` against the pre-alignment reference transform of Technical Requirement 27, the
      bounded alignment displacement actually applied, and the resulting post-IK pin offset are recorded separately.
      A remaining native-ball offset is permitted only where the combined schedule keeps full-stance reach and
      continuity and A2 still holds, and a nonzero offset alone is never a failure: traces distinguish a cap-limited
      remaining offset safely held by IK-003 planting from a genuinely infeasible schedule rejected in favour of
      bounded alternatives (NAV-TAC-011);
    - the alignment series is continuous through the join: within the configured per-tick bound at entry and exit,
      with no snap-back after the join;
    - alignment and residual correction are recorded separately and their combined total stays within the configured
      per-step and per-request budgets, with no fresh allowance at a seam or replan; and
    - contact and release history persists across seams, plans, requests, and completion, with no request-start
      reset, and rebind or teleport resets are identifiable through the IK-003 identity fields.
11. **NAV-TAC-011:** Prediction and feasibility evidence:
    - an oracle independent of the planner verifies, against recorded final native pre-IK poses through crossfades,
      the predicted ball, ankle, upper-leg, and foot-local-offset geometry within the declared error bounds, and
      point-blended ball estimates are never the reach evidence. Its numeric coverage includes a crossfaded
      same-role pivot re-entry issued through the continuation alias path, so the self-join is verified numerically;
    - yaw-compensated ankle reach is evaluated for both legs over complete transitions and resulting stances,
      including the first pure incoming sample, and schedules that violate a retained pin or ankle reach are rejected
      rather than averaged, foot-picked, reach-enlarged, or replanted;
    - rejected schedules are replaced by bounded feasible alternatives or corrective steps; obstruction still ends
      `Blocked` and exhausted or no-aligned-plan requests still end `Failed`; IK-003 reach releases are recorded and
      never counted as A2 success.

### Acceptance Traceability

- User Requirements 1, 7, 9 → A1, NAV-TAC-005.
- User Requirements 2, 4 → A1, NAV-TAC-002, NAV-TAC-004.
- User Requirement 3 → A5–A6, NAV-TAC-002, NAV-TAC-009.
- User Requirements 5–6 → A2–A4, NAV-TAC-003–004, NAV-TAC-008, NAV-TAC-010.
- User Requirements 8, 11 → Waypoint Route scenario (A1–A4, A6), NAV-TAC-007.
- User Requirement 10 → A5–A6.
- User Requirement 12 → A5–A6, NAV-TAC-009.
- Technical Requirements 1–4 → NAV-TAC-001; Technical Requirement 1 route following → NAV-TAC-007; Technical
  Requirement 3 composed proposal → NAV-TAC-010.
- Technical Requirements 5–7 → NAV-TAC-001–002, Technical Requirement 5 plan ranking → NAV-TAC-009; Technical
  Requirement 7 seam alignment (phase and pose agreement, per-pair crossfade durations, no aligned seam, same-role
  re-entry, required continuation states) → NAV-TAC-005, NAV-TAC-008.
- Technical Requirements 8–10 → A4, NAV-TAC-003; Technical Requirement 9 alignment-zero states → NAV-TAC-010.
- Technical Requirements 11–14 → A1–A3, NAV-TAC-002, NAV-TAC-004–005; Technical Requirement 13 no-aligned-plan
  `Failed` → NAV-TAC-005, NAV-TAC-008; Technical Requirement 14 promptness → NAV-TAC-005, NAV-TAC-008.
- Technical Requirements 15–16 → NAV-TAC-006; Technical Requirement 16 mid-clip entry → NAV-TAC-001; Technical
  Requirement 16 crossfade timing and same-role re-entry → NAV-TAC-008.
- Technical Requirement 17 → A1–A5, NAV-TAC-008–011.
- Technical Requirements 18–20 → NAV-TAC-007, Waypoint Route scenario.
- Technical Requirements 21–22 → A5–A6, NAV-TAC-002, NAV-TAC-006, NAV-TAC-008–009; Technical Requirement 21
  repeated-pivot generation and Technical Requirement 22 repeated chains → NAV-TAC-009; Technical Requirement 21
  feasibility → NAV-TAC-011; Technical Requirement 21 prompt precedence → NAV-TAC-005.
- Technical Requirements 23–28 → A2–A4, NAV-TAC-001, NAV-TAC-003, NAV-TAC-010.
- Technical Requirements 29–31 → NAV-TAC-002, NAV-TAC-011; Technical Requirement 31 outcomes → NAV-TAC-005.

## Current Status

This section is non-normative current status, not fresh verification or a claim of code/spec alignment. The scenario
table and every acceptance criterion above remain normative and unchanged; nothing here weakens A1, A2, or any bound.

### Automated Outcome Coverage

By user decision on 2026-10-04, nine failing NAV outcome facts were removed as reduced coverage. Removal asserts no
pass and fixes no behaviour:

- Arrival outcomes removed: Oblique, Terminal Slide, Repeated Requests, Distant Lateral, Behind, Long Straight, and
  the user-shuffle arrival.
- Corner regressions removed: the authored around-cylinder corner and the from-idle west-quadrant destination.

Automated outcome coverage currently spans Terminal Slide Mirror, Lateral, Turn-Only, the Waypoint Route, and the
from-idle corner obstacle scenario. Those retained facts assert the full arrival criteria set on unchanged assertions
and were green in the recorded 2026-10-04 default matrix, run before the removals. Every scenario or regression
listed above as removed now has no automated outcome guard.

### Suite Redesign (2026-10-10)

By user decision on 2026-10-10, the NAV/CTRL integration suite was redesigned rather than optimised. The NAV-001
implementation is incomplete with unresolved problems, the current `sad` ANIM-002 variant is placeholder content to be
replaced, no male retarget setup exists, and the expensive per-tick replay and diagnostic classes had come to encode
current placeholder behaviour as if it were a contract. Those tests were deleted, not fixed; removal asserts no pass
and fixes no behaviour. This entry supersedes the retained-facts list above: the 2026-10-04 retained outcome classes
were themselves removed on 2026-10-10.

The current automated end-to-end coverage is a two-fact spine, `NAV001LocomotionEndToEndIntegrationTests`, female
default rig only:

1. `WalkToDestination_ArrivesAndSettles_WithSmoothStitching` (foot IK disabled): a multi-metre turning walk reaches
   its destination within the A1 tolerance (`0.01 m`, `1°`), ends `Completed`, and settles to unfaded Idle across the
   post-completion window; maximum per-tick actor displacement and inter-sample hips/ball bone deltas stay inside the
   spine's envelopes.
2. `WalkToDestination_PlantedFeetHoldStance_NoFootSliding` (foot IK enabled): every observed contact stance holds its
   world anchor within the A2 `5 mm` planar bound, both feet across multiple stances, and the realised terminal actor
   transform agrees with the independently observed settled actor. Ownership evidence is outcome-level agreement
   only, not exclusive per-tick mutation authority.

The settled-drift (`1 mm`) and per-tick envelopes (actor `150 mm`, hips/ball `250 mm`) are loose engineering bounds
pending replacement content — not measured `sad` thresholds, not aesthetic seam-quality claims, and not spec numbers
(numeric caps remain configuration, not spec constants). They must be reviewed and replaced with content-independent
bounds when replacement content lands, and must not be tightened to current `sad` output.

Removed automated coverage (per-class inventory: `game/temp/suite-redesign-v13/report.md`):

- Superseded outcome scenarios: `NAV001ArrivalOutcomeIntegrationTests`, `NAV001WaypointRouteOutcomeIntegrationTests`,
  and `NAV001CornerRouteOutcomeIntegrationTests` — the 2026-10-04 retained spine is itself superseded.
- Heavy replay and diagnostic classes: `NAV001CorrectedArrivalIntegrationTests` (corrected-arrival population and pin
  evidence), `NAV001SequencePredictorOracleIntegrationTests`, `NAV001ArrivalPlannerIntegrationTests` (planner seam and
  planner-set), `NAV001PinFeasibilityIntegrationTests`, `NAV001SplicePruningIntegrationTests`,
  `NAV001StationaryPartialTurnIntegrationTests`, `NAV001PlantedSeamSeparationIntegrationTests`, and
  `NAV001ReplayPacingIntegrationTests`.
- Diagnostic probes: `NAV001RoboticMotionProbeIntegrationTests`, `NAV001NativeSpeedGraphIntegrationTests`,
  `NAV001MidClipEntryIntegrationTests`, `NAV001PivotContinuationIntegrationTests`,
  `NAV001PlantedStateRecorderValidationIntegrationTests`, `NAV001ComposedSectionContainmentIntegrationTests`,
  `NAV001SectionEndpointBoundIntegrationTests`, `NAV001TerminalContinuationAdmissibilityIntegrationTests`,
  `NPCSameTickPoseOrderingIntegrationTests`, `SeamTableIntegrationTests`, and
  `NPCNavigationQueuedManoeuvreIntegrationTests`.
- `IK003NavigationFootPlantingIntegrationTests`, replaced by spine fact 2; the IK-side removals, including the
  `IK003FootPlantingRigIntegrationTests` trim from 21 facts to one, are recorded under IK-003's status.
- Non-physics tautology and redundancy removals: `DirectTransformNavigationIntegrationTests` trimmed from 24 facts to
  four atomic request-boundary guards, `NPCNavigationTrajectoryBindingIntegrationTests` reduced to its bound
  `motion_scale` equation fact, and two `INavigationFacadeTests` declaration/enum restatements deleted.

Criteria coverage after the redesign — every criterion keeps its normative force; the table records only its current
automated guard:

| Criterion | Current Automated Guard |
| --------- | ----------------------- |
| A1 | Spine fact 1 only (single female default-rig walk); no per-scenario guard. |
| A2 | Spine fact 2 only (observed contact stances on that walk). |
| A3 | Spine fact 1's settle window only. |
| A4 | None. |
| A5 | None; unit-level movement-choice algorithm tests remain. |
| A6 | Manual judgement; not yet performed. |
| NAV-TAC-001 | Outcome-level ownership agreement in spine fact 2 only; per-tick trace checks removed. |
| NAV-TAC-002 | Bound `motion_scale` equation fact retained; sequence, slice-pair, and ball-table checks removed. |
| NAV-TAC-003 | None. |
| NAV-TAC-004 | Completion exercised by spine fact 1; tolerance-trace checks removed. |
| NAV-TAC-005 | Atomicity guards and unit lifecycle tests retained; promptness and cancel/replace traces removed. |
| NAV-TAC-006 | None (inspection criterion). |
| NAV-TAC-007 | Route-authoring UI tests retained; via-point traversal outcome guard removed. |
| NAV-TAC-008 | None; synthetic seam unit tests remain. |
| NAV-TAC-009 | None; synthetic planner unit tests remain. |
| NAV-TAC-010 | None. |
| NAV-TAC-011 | None; synthetic geometry unit tests remain. |

Retained NAV-side guards: the four atomic request-boundary facts of `DirectTransformNavigationIntegrationTests`
(map-not-ready rejection, replacement atomicity, zero-delta and already-complete non-mutation), the bound
`motion_scale` equation fact of `NPCNavigationTrajectoryBindingIntegrationTests`, the production-package boundary
completeness facts, the non-reference mirror-room bind-crash regression, the playtest controller and startup-gate
facts (17 facts, including destination-marker and via-authoring UI), and the synthetic planner, seam, predictor,
budget, route, and lifecycle unit tests, which use independent synthetic inputs rather than recorded placeholder
output.

No automated male-rig locomotion coverage remains: the male replay twins were deleted, no retarget setup exists, and
the retained male selection/bind facts are not retarget verification. Rigs resume automated coverage when replacement
content and male retargeting land.

The post-redesign full windowed suite gate is 1213 passed / 0 failed / 1 skipped (one pre-existing, unrelated
dynamic-rig fixture skip) in 11 m 36.8 s wall time — roughly 60% shorter than the 29 m 11 s pre-redesign baseline.
The reduction comes from deliberately smaller scope, not faster equivalent coverage, and establishes no criterion as
met.

### Unresolved Outcomes

Measured in the removed facts' final failing runs; each remains a live product deficit:

- Planted-run slip (A2) exceeded the unchanged `5 mm` bound in the removed scenarios that failed it, by 45–161 mm
  (Oblique and Repeated Requests 161.018 mm; Long Straight 45.217 mm; the cylinder corner 147.576 mm).
- The west-quadrant corner destination does not converge: `ConvergenceFailed` at 1543.56 mm position error and
  15.77° heading error, outside A1's `0.01 m` and `1°`.
- The around-cylinder corner inserted an idle bridge — 3 settled single-clip Idle ticks inside the replacement
  window — against User Requirement 12.
- NAV-TAC-008 swing-travel excesses of 49–142 mm were recorded across the removed scenarios, against the harness's
  provisional, not-yet-user-agreed `50 mm` swing-travel bound. The bound stays tunable configuration; validating or
  revising it remains an open user decision, unchanged by the removal.

### Remaining Acceptance Work

- A6 manual naturalness judgement has not been performed and is outstanding.
- The deficits above are unresolved and unguarded after the removal; nothing in this section may be read as A1–A6
  achieved.
- The 2026-10-10 spine's engineering envelopes must be reviewed and replaced with content-independent bounds when
  replacement animation content and male retargeting land; automated guards for the criteria listed above as
  unguarded must be rebuilt on that content.

## References

- [Project Specifications Index](../../index.md)
- [ANIM-002: Locomotive Animation Set][anim-002]
- [IK-003: Leg and Feet IK System][ik-003]
- [CTRL-001: Locomotion][ctrl-001]

[anim-002]: ../../animation/002-locomotive-animation-set/index.md
[anim-metadata]: ../../animation/002-locomotive-animation-set/index.md#motion-metadata
[planted-rule]: ../../animation/002-locomotive-animation-set/index.md#planted-combination-rule
[ik-003]: ../../ik/003-leg-feet-ik/index.md
[ik-003-planting]: ../../ik/003-leg-feet-ik/index.md#foot-planting
[ik-003-contact]: ../../ik/003-leg-feet-ik/index.md#contact-observations
[ctrl-001]: ../../ctrl/001-locomotion/index.md
