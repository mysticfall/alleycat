---
id: NAV-001
title: NPC Navigation
---

# NPC Navigation

## Requirement

Define NPC route execution, exact finite stopping, and durable request outcomes without sustained exact animation
forecasting or post-transition recovery.

## Goal

NPCs should follow long routes at ordinary gait, stop through one complete authored forward-to-idle transition, and
produce an unambiguous durable outcome without visible endpoint sliding, snapping, or hidden motion after the
transition.

## User Requirements

1. An accepted NPC navigation request has a stable identity and produces exactly one durable outcome: `Completed`,
   `Blocked`, `Failed`, `Cancelled`, or `Superseded`.
2. While the NPC is far from its stopping region, it travels at its current ordinary gait. Navigation must not introduce
   repeated hesitation or visible corrections caused by full-route animation forecasting.
3. Near the stopping region, the NPC chooses from only the next compatible same-lane and opposite-lane footfalls. It
   stops through one complete, zero-offset, ordinary-rate `f2i` that does not loop, restart, slice, stretch, or slow.
   The stop has no measurable terminal sliding, snapping, popping, hesitation, or recovery movement.
4. Normal position and yaw convergence begins only with the transition into `f2i` and finishes with the final consumed
   movement interval of that transition. There is no armed `wf` correction.
5. After `f2i`, the NPC remains stationary while completion is confirmed. Navigation applies no translation, yaw,
   contact contribution, collision move, snap-back, replan, or synthetic recovery after the final movement boundary.
6. Completion requires the NPC to remain within the configured planar and yaw tolerances for four consecutive stationary
   physics frames. The default tolerances are `0.001 m` and `0.1°` and remain configurable.
7. Being outside tolerance at the final `f2i` movement boundary, or leaving tolerance during confirmation, fails the
   request immediately and durably. Failure reporting exposes the requested and final transforms, both measured errors,
   both configured tolerances, and signed margins calculated as `tolerance - error`.
8. Displacement after a `Completed` outcome belongs to later gameplay or a later request; it cannot revise or invalidate
   the completed request.
9. Contact-lock-active and contact-lock-neutralised execution follows the same NAV route, stopping, hard-boundary, and
   outcome policy. Contact fallback appearance remains owned by ANIM-002 and IK-003.

## Technical Requirements

### Ownership Boundaries

1. **NAV Ownership:** NAV-001 owns NPC request identity and lifecycle, route and gait progress, stopping estimates,
   imminent candidate selection, transition-time convergence, collision-realised movement accounting, stationary
   confirmation, and durable outcomes.
2. **ANIM Ownership:** ANIM-002 owns `wf` release compatibility, `f2i` identity, exact duration, ordinary playback,
   finite phase publication, root movement, and authored contact evidence. NAV consumes these contracts without changing
   clip content or contact permission.
3. **IK Ownership:** IK-003 owns per-foot provider selection, contact lifecycle, anchors, target resolution, handoff,
   and foot actuation. It may return one already-authorised contact contribution while NAV movement is permitted. It
   cannot select a route or candidate, correct an outcome, or move the actor after the hard boundary.
4. **CTRL And CHAR Ownership:** CTRL-001 remains player-only and defines no NPC navigation policy. CHAR-003 owns
   physical response intent and constraints, but cannot commit or revise a NAV request outcome. NAV alone accounts for
   the collision-realised NPC movement used by this specification.

### Far-Route Estimate

5. Far sustained `wf` uses a cheap estimate based only on cached remaining route distance, current gait state, and
   cached ordinary-rate animation movement data. It must not materialise or sample an exact animation forecast over the
   full route on any far frame.
6. Route construction or revision may compute cumulative segment distances once. Per-frame far-path work reads cached
   progress and fixed-size gait/animation state, performs constant-time arithmetic, and allocates no memory. Its cost is
   independent of route length.
7. NAV periodically recalibrates estimated progress from collision-realised progress along the current route. The
   configurable trigger may be time-, tick-, or distance-based, but one production configuration must use a stable
   trigger, deterministic ordering, and the same realised samples for equivalent runs. Recalibration replaces
   accumulated estimate error; it does not apply actor correction.
8. Request acceptance, route replacement, route revision, gait change, teleport/reset, and navigation-clock generation
   change invalidate the affected cached basis. NAV re-anchors distance and gait state at the realised transform before
   making another estimate. No stale generation may contribute progress or candidate evidence.
9. The stopping-region threshold is derived from cached ordinary-rate movement needed for a bounded lead-in, transition
   crossfade, and complete `f2i`, plus configurable estimation uncertainty and final tolerance allowance. NAV enters the
   region when cached remaining route distance reaches that threshold. Tuning may change the threshold inputs but may
   not introduce full-route exact sampling.

### Bounded Imminent Check

10. Exact checking occurs only in the stopping region. One check evaluates at most two candidates: the next compatible
    release on the current footfall lane and the next compatible release on the opposite lane. It covers only each
    candidate's short lead-in, moving crossfade, and complete `f2i`.
11. Candidate compatibility requires a matching ANIM-002 release/exit mapping, current request and clock generation,
    finite exact interval coverage, ordinary-rate playback, and a projected correction that respects the configured
    transition envelope and no-crossing rules. Missing or stale evidence rejects only that candidate.
12. Selection is deterministic. Feasible candidates are ordered by earliest release crossing, then current lane before
    opposite lane when crossings are equal, then stable ANIM event identity. If neither candidate is feasible, NAV keeps
    ordinary uncorrected route travel and repeats the bounded check for the next imminent pair while the request remains
    actionable. It never searches an unbounded command or occurrence set.
13. Exact checking and candidate diagnostics use fixed-size value state, allocate no memory per frame, and have cost
    independent of total route length. A far frame performs zero exact candidate checks; an imminent check evaluates no
    more than two candidates.

### Transition and Hard Motion Boundary

14. The selected transition plays one full, zero-offset, ordinary-rate, non-looping `f2i`. NAV does not retain a
    forecast reservation, perform authored-command search, rearm an occurrence, or use phase pause/resume to manufacture
    convergence. No configuration, adapter, or compatibility mode may restore those paths or endpoint-tail recovery.
15. NAV position/yaw convergence has one envelope: it opens on the first movement interval of the transition into `f2i`
    and closes after the final consumed movement interval of `f2i`. Lead-in `wf`, producer latency, and any later state
    own no NAV convergence correction.
16. Each permitted movement interval composes authored root movement, at most one already-authorised IK contact
    contribution, and NAV convergence into one collision move. NAV records requested movement, collision-realised
    movement, and collision remainder. No component may issue a second move or silently compensate for the remainder.
17. Transition correction must not cross the requested planar stopping point or yaw target. Exact projection and runtime
    use the same interval, coordinate, wrapping, cap, and no-crossing semantics. Collision-realised progress, rather
    than requested movement, is authoritative for the next interval and final measurement.
18. AnimationTree latency is handled by tagged producer/consumer interval identity and clock generation. The final
    boundary occurs after the one composed collision move that consumes the clip's final non-zero root/yaw interval, not
    merely when a producer first publishes phase `1`. Stale, skipped, duplicate, or wrong-generation intervals cannot
    move the actor or close the envelope.
19. Beginning immediately after that final composed move, NAV applies no actor translation, yaw, contact contribution,
    collision move, synthetic correction, or authored root contribution. NAV clears accumulated planar and yaw velocity
    state before confirmation and rejects or gates stale velocity state so it cannot contribute after the boundary.
    There is no held endpoint tail, phase pause, terminal recovery, snap-back, or collision replan. Animation and IK may
    update stationary visual targets but cannot contribute actor motion.

### Final Measurement and Confirmation

20. The final movement boundary measures planar distance and wrapped yaw error against the request's retained transform.
    It uses the request's retained tolerances, whose configurable defaults are `0.001 m` and `0.1°`. Signed planar and
    yaw margins are exactly `tolerance - error`; a negative margin is failure.
21. If either final margin is negative, NAV immediately commits `Failed`. It performs no confirmation, snap-back,
    replan, or recovery. The terminal record retains the measured requested/final transforms, errors, tolerances, and
    margins.
22. If both margins are non-negative, NAV enters stationary confirmation with a count of zero. Each of the next four
    physics frames performs no NAV movement and remeasures the realised transform. A negative margin immediately commits
    `Failed`; four consecutive in-tolerance frames commit `Completed`.
23. Terminal commitment is exact-once and first-terminal-wins. Once committed, later transforms, callbacks, clock
    changes, cancellation, replacement, diagnostics, or acknowledgements cannot change the outcome.

### Requests and Durable Outcomes

24. Acceptance reserves a stable request identity and terminal-record capacity before replacing active work. An invalid,
    not-ready, unreachable, or capacity-blocked attempt is rejected without changing the active request. Request
    identity is never reused across reset or clock generations.
25. Every terminal record is immutable and contains: request identity; request revision and clock generation; ordered
    terminal sequence; outcome and reason; requested and final transforms; planar and wrapped-yaw errors; configured
    tolerances; signed margins; acceptance and terminal physics ticks; and successor identity for `Superseded`, when
    present. `Blocked` records the known obstruction context; `Cancelled` records the cancellation source.
26. Terminal outcomes persist in bounded ordered storage until idempotently acknowledged by request identity and
    terminal sequence. Storage never overwrites an unacknowledged outcome. When capacity cannot be reserved, NAV reports
    outcome backpressure and leaves active work unchanged. Delivery and acknowledgement are bounded operations.
27. Runtime status and terminal records use reusable or value-backed storage. Production behaviour cannot depend on test
    APIs, and steady-state physics processing performs no per-frame outcome allocation.
28. Explicit cancellation, navigation disable, owner removal, or reset commits `Cancelled` for active accepted work
    before clearing its route, estimate, candidate, and clock state. Reset establishes a new generation and cannot erase
    an unacknowledged record.
29. Replacement reserves identity and outcome capacity first, commits the old request as `Superseded` with the successor
    identity, and then activates the successor from the realised transform. If reservation fails, the old request
    remains active and unchanged.
30. A route or gait revision for the same request clears imminent evidence and deterministically re-anchors the
    estimate. A clock-generation change before transition commitment invalidates the candidate and returns to bounded
    selection. An unexpected generation change after `f2i` begins commits `Failed`; it cannot restart, rearm, or recover
    the transition.
31. A known route obstruction may commit `Blocked` before `f2i`. Any contract, clock, interval, or final-convergence
    failure commits `Failed`. Neither outcome permits post-commit movement for that request.

### Diagnostics and Validation

32. Diagnostics are read-only and behaviour-neutral. Enabling, disabling, dropping, or consuming diagnostic records
    cannot affect estimates, recalibration, candidate ordering, movement, confirmation, or outcomes.
33. Objective validation must use a deterministic contiguous physics trace without coupling production implementation to
    test APIs. The trace must cover request identity and route/gait generation; selected lane and ANIM event mapping;
    the complete finite transition; transition-seam and per-foot continuity state; each composed movement contribution
    and its requested, realised, and remainder values; the final movement boundary and realised terminal location; four
    immutable confirmation-frame measurements; and terminal-record commitment.
34. Visual evidence is limited to fixture setup: direction-marker readability, Origin, Destination, and hard-boundary
    placement, and camera framing. Images are not evidence of motion smoothness, naturalness, stationarity, or the
    absence of terminal sliding.

## In Scope

- NPC request, route/gait estimate, stopping-region, bounded candidate, `wf → f2i`, collision accounting, confirmation,
  and durable-outcome contracts.
- Forward ordinary-walk stopping through ANIM-002-compatible `f2i_l` and `f2i_r` transitions.
- NPC consumption of one IK-authorised contact contribution only while NAV movement remains permitted.
- Behaviour-neutral diagnostics, deterministic runtime assertions, and visual fixture-setup evidence.

## Out Of Scope

- Player movement or travel-release policy, which remains owned by CTRL-001.
- Terrain adaptation, off-mesh traversal, semantic gameplay waypoints, gait synthesis, and animation or IK reauthoring.
- Final tuning beyond the stated default tolerances and structural bounds.
- Broad animation/IK seam-quality remediation beyond the required `f2i` seam and foot-continuity trace assertions,
  including Oblique leg or foot continuity.

## Acceptance Criteria

### User Requirement Acceptance

1. **NAV-UAC-001:** Deterministic runtime scenarios prove ordinary sustained travel without forecast-induced
   hesitation. Each stop uses one complete, zero-offset, ordinary-rate `f2i` and has no measurable terminal sliding,
   snapping, popping, hesitation, or recovery movement.
2. **NAV-UAC-002:** Active and neutralised contact scenarios follow the same route, candidate, hard-boundary,
   confirmation, and outcome policy without claiming equivalent foot-contact appearance.
3. **NAV-UAC-003:** In-tolerance scenarios remain stationary for four confirmation physics frames and then produce one
   durable `Completed` record. Movement after success does not alter that record.
4. **NAV-UAC-004:** Final-boundary and confirmation excursions outside either tolerance immediately produce one durable
   `Failed` record containing the requested measurements and negative failing margin, with no snap-back or replan.
5. **NAV-UAC-005:** Cancellation, obstruction, replacement, and failure scenarios expose stable, unambiguous durable
   outcomes to consumers without losing or revising an earlier terminal result.

### Technical Requirement Acceptance

1. **NAV-TAC-001:** Instrumented far-route runs prove zero exact checks on far frames, no full-route animation sampling,
   deterministic collision-realised recalibration, zero per-frame allocation, and constant cost as route length grows.
2. **NAV-TAC-002:** Stopping-region tests prove derivation from cached lead-in/crossfade/`f2i` demand and uncertainty.
    Candidate tests prove no check evaluates more than the next two compatible lanes and deterministic tie-breaking
    selects the same result for equivalent state. Imminent-check instrumentation proves fixed-size value state, zero
    per-frame allocation, and constant check cost as total route length grows.
3. **NAV-TAC-003:** Interval tests prove exact checking covers only the short lead-in, crossfade, and complete `f2i`;
   playback is full, zero-offset, ordinary-rate, and non-looping; and no forecast reservation, command search, rearm,
   phase pause, endpoint tail, or recovery path participates.
4. **NAV-TAC-004:** Latency and mutation tests prove correction starts on transition entry, finalises only after the
   exact final consumed movement interval, rejects stale or wrong-generation intervals, and applies no armed `wf`
   correction.
5. **NAV-TAC-005:** Collision accounting proves each moving tick uses one composed move, retains requested, realised,
   and remainder values, obeys no-crossing semantics, and issues no second or compensating move.
6. **NAV-TAC-006:** Deterministic contiguous-physics-trace assertions cover the selected lane and ANIM event mapping,
   the complete finite `f2i` transition, transition-seam and per-foot continuity state, every composed movement
   contribution and its requested, realised, and remainder values, the final movement boundary and realised terminal
   location, and four immutable confirmation-frame measurements. Hard-boundary assertions prove zero NAV translation,
   yaw, contact contribution, collision moves, synthetic correction, and authored root contribution after the final
   consumed `f2i` interval, including all four confirmation frames and after every terminal outcome. They verify
   accumulated planar and yaw velocity state is cleared before confirmation and injected stale state is rejected or
   gated without actor motion.
7. **NAV-TAC-007:** Boundary tests use configurable tolerances with defaults of `0.001 m` and `0.1°`, verify inclusive
   in-tolerance comparison, and verify exact signed margins. They cover immediate final failure, confirmation failure on
   frames one through four, and completion only after four consecutive in-tolerance frames.
8. **NAV-TAC-008:** Lifecycle tests prove stable non-reused identity, first-terminal-wins exact-once commitment,
   immutable ordered outcomes, bounded persistence, idempotent acknowledgement, and no steady-state per-frame outcome
   allocation or production dependency on test APIs. Every applicable terminal record asserts request identity,
   revision, clock generation, terminal sequence, outcome, reason, requested and final transforms, planar and
   wrapped-yaw errors, tolerances, signed margins, and acceptance and terminal ticks. `Blocked`, `Cancelled`, and
   `Superseded` records additionally assert obstruction context, cancellation source, and successor identity,
   respectively. Invalid, not-ready, unreachable, and capacity-blocked attempts, including outcome backpressure, assert
   that the active request, route, estimate, candidate, and reserved outcome state remain unchanged.
9. **NAV-TAC-009:** Reset, cancellation, disable, owner removal, replacement, route/gait revision, and pre- and
   post-transition clock-generation tests prove the required clearing, re-anchoring, successor, and fail-closed
   behaviour.
10. **NAV-TAC-010:** Diagnostics-on, diagnostics-off, and dropped-diagnostics runs produce identical candidate,
    movement, confirmation, and outcome results. Visual fixture review verifies only direction-marker readability,
    Origin, Destination, and hard-boundary placement, and camera framing. Its images are not evidence of motion
    smoothness, naturalness, stationarity, or the absence of terminal sliding; deterministic runtime assertions verify
    the hard no-motion boundary.
11. **NAV-TAC-011:** Ownership checks prove ANIM supplies content and contact evidence, IK alone resolves feet and
    authorises contact contributions, NAV alone owns NPC movement/outcomes, CTRL remains player-only, and CHAR cannot
    revise NAV outcomes.

### Acceptance Traceability

- User Requirements 1–9 → NAV-UAC-001–005.
- Technical Requirements 1–4 → NAV-TAC-011.
- Technical Requirements 5–9 → NAV-TAC-001–002 and NAV-TAC-009.
- Technical Requirements 10–13 → NAV-TAC-002–003, including NAV-TAC-002 allocation and route-length cost assertions.
- Technical Requirements 14–19 → NAV-TAC-003–006.
- Technical Requirements 20–23 → NAV-TAC-006–007.
- Technical Requirements 24–31 → NAV-TAC-008–009, including NAV-TAC-008 rejection and terminal-field assertions.
- Technical Requirements 32–34 → NAV-TAC-006 and NAV-TAC-010.

## References

- [Project Specifications Index](../../index.md)
- [ANIM-002: Locomotive Animation Set](../../animation/002-locomotive-animation-set/index.md)
- [IK-003: Leg And Feet IK System](../../ik/003-leg-feet-ik/index.md)
- [Contact-Lock Coordinator Contract](../../ik/003-leg-feet-ik/contact-lock-coordinator-contract.md)
- [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md)
- [CHAR-003: Character Physical Response](../../character/003-physical-response/index.md)
