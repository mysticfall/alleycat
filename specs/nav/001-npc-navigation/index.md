---
id: NAV-001
title: NPC Navigation
---

# NPC Navigation

## Requirement

Define poll-based NPC navigation that reports coherent route intent and drives predictive NPC locomotion without taking
ownership of actor movement.

## Goal

NPC-capable actors can request or replace a destination and follow it with anticipated turns, braking, and terminal
facing. Callers remain decoupled from Godot navigation nodes, and root motion remains the sole physical authority.

## User Requirements

1. A navigation request accepts a world-space destination transform. Its origin defines the destination position and
   its full basis remains authoritative intent, although this slice steers yaw only. Applying that yaw does not discard
   the actor's scale, pitch, or roll.
2. Production NPCs follow routes with persistent, receding-horizon intent rather than reacting independently to each
    waypoint. They begin planned initial turns and moving turns before corners, brake before arrival, and settle into
    terminal arrival and destination facing without correction or pivot thrashing. A genuine post-arrival miss still
    returns to route recovery.
3. Route following favours forward walking and route-directed walking arcs that reduce remaining route distance and
   heading error. A short local rear or lateral correction enters at or below 1 m and uses a rear/lateral diagonal
    blend; it releases at or above 1.25 m. For a distant rear destination, a held looped signed pivot is used only when
    forward arc progress is unsuitable; otherwise the NPC turns while walking forward. A stationary deliberate facing
    correction holds the matching ordinary unarmed signed 90-degree pivot and releases directly to route movement,
    without a neutral or `Idle` interlude.
4. Small path deviations produce small, sustained corrections. NPCs do not accumulate drift before issuing an
   occasional sharp corrective turn, nor snap or warp back onto the path.
5. Replacing a destination or receiving revised path geometry changes the route plan immediately while preserving
   current control and animation continuity. The NPC blends coherently from its observed motion towards the new route.
6. The direct-transform consumer retains its existing path-facing ramps, short-move behaviour, and deterministic
   baseline semantics; predictive production-NPC planning does not alter that baseline.
7. Slow and sub-metre routes remain actionable rather than completing from a broad destination tolerance. Position
   completes only when the actor is close to the requested destination and to the terminal end of the remaining path,
   preventing early completion when the actor is Euclidean-close across an obstacle corner.
8. Navigation completes only when position and yaw-facing tolerances are both met. Reaching position with facing
   outstanding exposes the matching supported stationary pivot until facing completes. A same-position request with an
   empty path remains valid.
9. An initially unreachable request is rejected atomically and does not replace an existing valid request. A request
    made before the navigation map is ready is also rejected without changing request state and reports a distinct,
    retryable not-ready outcome rather than accepted or unreachable.
10. Production NPCs translate navigation intent into forward walking, bounded local rear/lateral diagonal correction,
    walking arcs, and held looped signed bilateral 90-degree pivots without navigation directly moving the actor.
11. NPC walking turns converge smoothly towards the requested facing and do not oscillate between full left and right
     input near the target yaw. Both stationary pivots remain interruptible.
12. Existing VR player locomotion remains tracker-driven and unaffected; AI navigation is not installed on players.
13. Direction changes and destinations behind the NPC remain visually continuous and converge correctly through the
    ANIM-003 role map. The reported destination transform remains the exact requested transform throughout replanning,
    without skeleton resets, neutral or `Idle` interludes, or repeated stationary-pivot restarts.

## Technical Requirements

1. `NavigationBase` owns path, facing, ramp, tolerance, and completion policy, but never mutates the navigated actor.
2. A concrete implementation passes the actor's authoritative current `Transform3D` to each poll and receives one
   coherent, immutable navigation motion-intent snapshot. The base calculates against that current state and the stored
   destination request; the implementation decides how, or whether, to apply the resulting intent.
3. Each snapshot reports the next path position, travel direction, desired horizontal facing direction, signed yaw
   error, remaining and travelled path distances, and separate position, facing, and combined completion states.
4. Travel and facing remain separate so consumers can choose supported forward, lateral, walking-arc, or stationary
   pivot behaviour. Public navigation APIs must not use animation or motion-matching vocabulary.
5. Each poll performs one coherent Godot path sample/advance operation. The concrete implementation controls polling
   frequency and caches the resulting snapshot between its own queries.
6. The same poll sample used for completion and progress publishes a protected, immutable route snapshot containing
   copied path points, active path index, destination transform, request generation, route revision, and fallback or
   replan status. Consumers must not reconstruct a sample by querying mutable path getters after polling.
7. Initial facing is captured from the first valid sample of an accepted request. Travelled path distance commits only
   forward arc advancement projected from authoritative actor samples onto the active navigation path. Perpendicular
   and backward movement add no progress. Fallback publication, path-index changes, and replans re-anchor without jumps
   or rewinds; each sample still recomputes downstream waypoint and terminal intent from the current path.
8. Ramp interpolation uses smoothstep. The initial ramp blends from captured facing towards initial path travel over
   `InitialFacingRampDistance`; downstream ramps blend towards each requested facing over `FacingRampDistance` before
   its waypoint. Downstream precedence and the short-move override are deterministic.
9. Position completion requires both actor proximity to the requested destination within `DestinationReachedDistance`
   and terminal remaining-path proximity within `PathDesiredDistance`. An empty path satisfies the path condition only
    when the actor is already within the destination tolerance, preserving same-position facing requests.
10. `DirectTransformNavigation` defaults `PathDesiredDistance` and `DestinationReachedDistance`, which maps to
   `NavigationAgent3D.TargetDesiredDistance`, to 0.05 m. The defaults remain configurable for future consumers and keep
   destination tolerance materially below the default `ShortMoveDistance`.
11. Invalid or degenerate path positions, segment bearings, destination facing, or distance values produce finite,
   stable intent and cannot leak non-finite vectors, yaw errors, or distances to consumers.
12. Map readiness, destination validation, and initial reachability are resolved before request state is committed. A
   rejection leaves the current valid request, Godot target, accepted path, and cached intent unchanged. Synchronous
   reachability defaults to the base node's authoritative Node3D-ancestor world position. Implementations may override
   that start; the direct implementation uses its resolved actor target rather than potentially stale NavigationServer
   agent state.
13. `NavigationBase` is the Godot-backed `NavigationAgent3D` boundary; the public navigation facade must not expose the
      concrete agent. Request results distinguish accepted, unreachable, invalid, and map-not-ready requests.
14. `DirectTransformNavigation` remains the baseline and test implementation. It may apply snapshots directly to an
     explicitly configured `Node3D` target, with the closest `Node3D` ancestor as fallback. The target is normally an
     ancestor so the agent follows it; other targets require external transform synchronisation. It applies gradual yaw
     around world up to the complete existing world basis, preserving scale and non-yaw orientation beneath rotated and
     non-uniformly scaled parents. It must not assign the actor transform when elapsed time is zero or intent is already
     complete.
15. Navigable actors expose navigation through the component-holder pattern; navigation does not discover or own them.
16. Existing path inspection, threshold, and initial avoidance configuration remain available through the navigation
    abstraction. Safe-velocity callback mechanics are not part of this slice.
17. `LocomotiveNavigation` is the production NPC consumer. It retains `DirectTransformNavigation` as a deterministic
    baseline rather than replacing or weakening that implementation.
18. `LocomotiveNavigation` requires one explicitly exported actor node. The same node must be a `Node3D`, for
    authoritative world-transform sampling, and implement `ILocomotive`, for command publication. Missing or invalid
    binding disables command publication and produces a clear validation failure; navigation must not discover, move,
    or own the actor implicitly.
19. `LocomotiveNavigation` polls exactly once per navigation physics tick with the bound actor's current global
    transform and owns a persistent, pure C# receding-horizon planner. It compiles each coherent route revision into
    arc-length segments, corners, turn-start regions, braking intent, terminal facing, and short endpoint manoeuvres.
20. Each physics tick, the planner projects observed root-motion progress onto the persistent route and evaluates
    candidate movement and turn controls over 0.2, 0.5, and 1.0-second horizons. Scoring covers route progress,
    cross-track and heading error, stop overshoot, terminal facing, control-rate change, and reversal. For large rear
    routes, it prefers route-directed forward walking arcs that reduce remaining route distance and heading error.
21. Candidate selection is forward-biased and constrained by the signed, character-aware locomotion response profile
    defined by [ANIM-003](../../animation/003-standing-locomotion-catalogue/index.md). At a rear or lateral correction
    distance of 1 m or less, it may select only the `BackwardWalk` plus matching lateral-role diagonal blend; it retains
    that mode until distance reaches 1.25 m. Beyond that release distance, rear intent uses forward walking arcs unless
    forward arc progress is unsuitable for route-distance and heading-error reduction. It may then select the matching
    held looped signed stationary 90-degree pivot. It holds while its signed facing correction remains
    necessary and, on release, transitions directly to route movement without a neutral or `Idle` interlude; moving
    changes otherwise use walk arcs. This route policy must not alter ANIM-003 root-motion response data or graph blend
    weights. Only the first control is published; future intent remains available for replanning from the next observed
    Root-motion displacement.
22. Destination replacement or asynchronous route revision rebuilds route geometry immediately. Current controls and
    animation phase are retained, then changed through configurable finite slew and acceleration limits rather than
    reset. Clear and completion stop smoothly; lifecycle invalidation still neutralises safely.
23. Cross-track correction is continuous and supplementary. The rear/lateral diagonal correction enters only at or below
    1 m and releases only at or above 1.25 m; these thresholds define its required hysteresis. It must not become a
    second movement authority, cut the accepted navigation polyline, or produce dead-zone accumulation, sign-flipping,
    saturated pulses, or motion warping.
24. Navigation physics processing must run before `CharacterLocomotion` physics processing, so each locomotion step
    consumes commands published from the current navigation sample rather than the previous physics tick.
25. `LocomotiveNavigation` brings controls smoothly to neutral after combined completion or a successful clear. Invalid
    actor sampling, disablement, or tree exit neutralises safely without preserving stale commands. A rejected
    replacement request preserves the active request and controls until a valid sample or lifecycle event changes them.
26. Route compilation, trajectory evaluation, world-to-local conversion, and control limiting must be isolated as
    deterministic, Godot-runtime-independent logic for focused unit validation.
27. `LocomotiveNavigation` is installed only by NPC role templates and navigation playtests. Production wiring and
    playtest controllers depend on `INavigation`, the explicit actor, and `ILocomotive`, not on fields specific to
    `DirectTransformNavigation`.
28. Sharp-turn playback normatively depends on the deterministic Hips heading neutralisation defined by
    [ANIM-003](../../animation/003-standing-locomotion-catalogue/index.md). Root remains the sole physical planar
    translation and yaw authority; navigation must not use runtime clamps, motion warps, or direct actor, Root, or
    skeleton corrections as substitutes.
29. Navigation consumes only the nine-role ANIM-003 response profile and role map. It may select `BackwardWalk` only
    through the bounded local correction policy. It must not select run, start/stop, combat, weapon, sad, hunched,
    raised-arm, or unused roles, and must not bind, runtime-mirror, or synthesise a pivot. A fully processed,
    ANIM-003-approved derived travel role remains a normal library binding.
30. For a stationary facing correction, the planner selects and holds the looped pivot whose Root-yaw sign matches the
    signed yaw error. It releases near the target facing directly to route movement, without a neutral or `Idle`
    interlude or a repeated pivot restart. It preserves continuous root-motion consumption across loop wraps through the
    CTRL-001 `GeneralSkeleton:Root` unique-name track contract. Corrected imported Root yaw maps directly to actor yaw;
    navigation must not apply a coordinate, sign, or compensating-yaw conversion. Navigation remains responsible for
    convergent route intent, not direct actor, Root, or skeleton correction.
31. The immutable route snapshot retains and reports the exact requested destination `Transform3D` across route
    compilation, revision, and replanning. Reported transform convergence must target that requested transform rather
    than an intermediate route point or a locomotion-response approximation.
32. Terminal arrival uses a bounded settle/release policy. While observed position and facing remain within configured
    terminal conditions, it settles or releases the endpoint manoeuvre without repeated correction or pivot restarts.
    An observed post-arrival position or facing deviation that exceeds the configured release condition returns to
    normal route recovery. The policy must treat nearby route lengths consistently and must not change endpoint
    manoeuvre solely because a route crosses an endpoint-length boundary.

## In Scope

- Navigation component and holder contracts, request result, Godot-backed policy base, and immutable polled intent.
- Horizontal path-facing calculation, path-distance ramps, short-move override, and precise path-aware completion.
- Atomic validation of new requests and stable handling of invalid or degenerate path data.
- `DirectTransformNavigation` as a baseline and test consumer with precise, configurable distance defaults.
- `LocomotiveNavigation` as the production NPC consumer, including explicit binding, command conversion, lifecycle
  neutralisation, coherent route snapshots, persistent receding-horizon planning, and publish-before-consume ordering.
- Planned initial turns, corner anticipation, braking, terminal facing, forward-biased route-directed walking arcs,
   conditional held looped signed pivots for large rear routes, direct release to route movement, and bounded
   rear/lateral diagonal correction with the required hysteresis.
- Direction-change and behind-destination continuity, convergence, bilateral pivot selection, and lateral regression
  coverage within the ANIM-003 role map.
- Interface-based NPC template and navigation-playtest composition without player installation.
- Path inspection, threshold configuration, and initial avoidance configuration through the navigation abstraction.

## Out Of Scope

- Full motion matching, motion warping, start/stop one-shots, and player-specific snap turning.
- Navigation-owned animation selection or root-motion application; predictive locomotion controls and their publication
  to `ILocomotive` remain in scope.
- Future dynamic route-blockage interruption and replanning, including stuck or deadlock detection policy.
- Future dynamic speed-based short-move thresholds; `ShortMoveDistance` is directly configurable in this slice.
- Future custom facing-ramp curves beyond the initial smoothstep implementation.
- Off-mesh link traversal, custom route semantics, and future per-consumer tuning beyond the required baseline defaults.
- Safe-velocity events, callbacks, and detailed avoidance steering policy.
- Changes to VR player tracker locomotion.

## Acceptance Criteria

### User Requirement Acceptance

1. An accepted request preserves the full destination transform as intent while deriving steering from horizontal yaw.
2. A long route retains initial facing at its first valid sample, then smoothstep-aligns towards path travel over the
   configured initial path distance.
3. Route scenarios verify interior segment bearings, terminal destination facing, shared downstream ramp distance,
   downstream overlap precedence, and permissible skipping of closely spaced waypoint facing.
4. A route at or below the configured short-move threshold requests terminal facing from its first valid sample,
   including a lateral side-step whose terminal facing equals its initial facing.
5. Slow lateral route scenarios across 0.10–0.90 m remain actionable and settle within the configured destination
   tolerance. A route whose destination is Euclidean-close across an obstacle corner continues along its path until
   terminal path proximity is also met.
6. Completion scenarios verify independent configurable position and yaw tolerances, combined completion, and matching
   stationary-pivot intent after positional arrival while yaw remains outstanding. A same-position request with an empty
   path remains valid and completes only after its requested facing is reached.
7. Initially unreachable and map-not-ready requests leave an existing valid request unchanged. The not-ready result is
    distinct from unreachable, and VR tracker locomotion remains unaffected.
8. Production NPC scenarios demonstrate forward walking, local rear/lateral diagonal correction, walking arcs, and both
   stationary pivots without navigation directly mutating the actor transform.
9. NPC turning converges smoothly, can be interrupted or reversed immediately, and does not oscillate near the
   requested facing. Player scenes retain their existing tracker-driven locomotion without AI navigation.
10. The final user playtest confirms that the NPC turns before corners, follows continuous forward arcs, corrects small
    deviations gradually, and does not produce occasional sharp corrective turns.
11. The same playtest confirms smooth destination replacement, controlled non-oscillating terminal stopping and facing,
    diagonal correction only at or below 1 m, release at or above 1.25 m, and route-directed forward walking arcs for
   a distant rear destination. When forward arc progress is unsuitable, the matching signed pivot remains held and
   looped until its direct release to route movement; no neutral or `Idle` interlude occurs. The slice is not accepted
   until the user approves these cues.
12. Direction-change and behind-destination playtests remain visually continuous and reach the requested route and
    facing with the matching held looped bilateral pivot, no skeleton reset, no incorrect turn reversal, no neutral or
    `Idle` interlude before route movement, and no repeated pivot restart at rest.

### Technical Requirement Acceptance

1. Tests confirm `NavigationBase` calculates policy without mutating an actor and the concrete implementation supplies
   the actor's authoritative current `Transform3D` to each poll. Snapshot calculations use that current transform while
   preserving the accepted destination `Transform3D` as separate stored request intent.
2. Each poll performs one Godot path sample/advance operation, exposes every required travel, facing, distance, and
   completion value from that operation, and remains cached between implementation-controlled polls.
3. Tests exercise supported forward, backward, side-step, walking-arc, and bilateral stationary-pivot intent without
   coupling travel direction to facing direction or introducing animation-specific public vocabulary.
4. Path-change tests cover fallback publication, longer and shorter replans, and path-index transitions. They preserve
   captured facing and monotonic forward path distance while recomputing downstream intent. Tests reject perpendicular
   and backward progress. Empty, single-point, and degenerate samples keep exposed values finite and stable.
5. Completion tests independently falsify requested-destination proximity and terminal remaining-path proximity to
   prove both are required. They cover close-across-corner traversal, precision arrival, and the valid empty-path
   same-position case.
6. Default-value regression tests confirm `DirectTransformNavigation` uses 0.05 m for `PathDesiredDistance` and
   `DestinationReachedDistance`/`TargetDesiredDistance`, keeps both configurable, and keeps the destination tolerance
   materially below the default `ShortMoveDistance`.
7. Facade tests preserve the Godot-agent boundary, all request outcomes, path inspection, thresholds, and initial
   avoidance configuration. Integration tests prove that every rejection commits no partial state and that synchronous
   reachability starts from the direct implementation's authoritative actor position.
8. Direct-consumer integration tests prove local and world scale preservation, gradual world-up yaw without pitch or
    roll loss, correct conversion beneath a rotated and non-uniformly scaled parent, and exact transform equality for
    zero-delta and already-complete no-op samples.
9. Unit tests verify actor-local sign conventions for forward and backward travel, both lateral directions, and walking
   arcs, plus finite, bounded, continuous walking-turn output on both sides of zero and around neutral.
10. `LocomotiveNavigation` integration tests prove explicit `Node3D` and `ILocomotive` binding, one authoritative actor
    sample per physics tick, no direct transform mutation, and command publication before `CharacterLocomotion`
    consumption.
11. Lifecycle tests verify smooth neutralisation after completion and clear, safe immediate neutralisation after invalid
    sampling, disablement, and tree exit, and preservation of the prior request after a rejected replacement.
12. NPC template and navigation-playtest inspection confirms interface-based production binding. Player templates do
    not contain `LocomotiveNavigation`, and baseline direct-consumer tests continue to pass.
13. Route-snapshot tests prove that completion, progress, copied geometry, destination, generations, revisions, and
    fallback status come from one poll sample and remain immutable despite later Godot path changes.
14. Focused planner tests cover route compilation, corner anticipation, forward bias, signed response prediction,
    bounded control-rate changes, braking, terminal facing, replacement continuity, and continuous drift recovery
    without sign-flipping or sharp pulses. They prove local correction enters at 1 m or less, releases at 1.25 m or
    more, and uses the rear/lateral diagonal response. For a distant rear destination, they prove route-directed
    forward arcs reduce route distance and heading error, while a held looped signed pivot is used only when forward
    arc progress is unsuitable. They also prove stationary pivot selection follows signed yaw, remains held while its
    correction is required, exits near target directly to route movement, and neither enters a neutral or `Idle`
    interlude nor restarts while stationary. Godot-running coverage proves the held pivot continues Root yaw without a
    discontinuity or double application at loop wraps through the `GeneralSkeleton:Root` track. End-to-end
    near-forward route coverage on both signed sides of forward proves signed navigation yaw carries corrected imported
    Root yaw directly to actor yaw, without an opposite-sign or compensating conversion.
15. Godot-running checks cover right walk-arc interpolation, navigation-before-animation ordering, retained direct-
    transform behaviour, and root-motion traversal of straight, right-angle, S-curve, and short endpoint routes. They
    confirm that root motion alone changes the actor transform and that no motion warping occurs.
16. Planner and graph checks prove all nine role bindings, both required stationary pivot bindings, and the bounded
    correction policy; they reject runtime mirroring, a mirrored pivot substitute, or synthetic yaw. Independent visual
    checks and user playtests
    prove route progress and facing convergence through the supported role map, including both lateral destination
    offsets and distant rear destinations, without skeleton resets, incorrect turn reversal, sad or hunched posture, or
    raised-arm/combat poses.
17. Snapshot and replan checks prove the reported destination `Transform3D` remains exact and converges to the requested
    transform across large rear routes; they reject substitution of intermediate route points or root-motion response
    approximations. Graph and response-profile checks prove the policy changes neither root-motion response data nor
    blend weights.
18. Focused deterministic regressions exercise route lengths on either side of the former endpoint boundary and an
    observed post-arrival position or facing deviation. They prove consistent endpoint manoeuvre treatment, stable
    settling without correction or pivot thrashing, and return to route recovery for a real miss.

## References

- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- `game/src/Navigation/`
