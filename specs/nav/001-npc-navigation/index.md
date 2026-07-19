---
id: NAV-001
title: NPC Navigation
---

# NPC Navigation

## Requirement

Define poll-based NPC navigation that reports coherent travel and horizontal-facing intent without taking ownership of
actor movement.

## Goal

NPC-capable actors can request a destination, poll natural route-following intent, and choose how to move while callers
remain decoupled from Godot navigation nodes and production movement systems.

## User Requirements

1. A navigation request accepts a world-space destination transform. Its origin defines the destination position and
   its full basis remains authoritative intent, although this slice steers yaw only. Applying that yaw does not discard
   the actor's scale, pitch, or roll.
2. On long moves, an actor initially retains its standing facing, then gradually aligns with path travel while moving.
3. The initial transition uses a separately configurable `InitialFacingRampDistance`, measured along the path from the
   first valid sample.
4. Each interior waypoint requests the horizontal bearing from that waypoint to the next. The terminal waypoint
   requests the destination transform's horizontal facing.
5. Waypoint and terminal transitions use the same configurable `FacingRampDistance`, measured along the path as the
   waypoint is approached.
6. Facing ramps use smoothstep interpolation. A later downstream ramp takes precedence where ramps overlap, so facing
   requests at closely spaced waypoints may be skipped.
7. A configurable `ShortMoveDistance`, measured against total path length, requests terminal facing from the first
   valid sample. This permits short lateral side-steps when terminal and initial facing match.
8. Slow and sub-metre routes remain actionable rather than completing from a broad destination tolerance. Position
   completes only when the actor is close to the requested destination and to the terminal end of the remaining path,
   preventing early completion when the actor is Euclidean-close across an obstacle corner.
9. Navigation completes only when position and yaw-facing tolerances are both met. Reaching the position with facing
   outstanding exposes turn-in-place intent until facing also completes. A same-position request with an empty path
   remains valid and can expose that turn-in-place intent.
10. An initially unreachable request is rejected atomically and does not replace an existing valid request. A request
   made before the navigation map is ready is also rejected without changing request state and reports a distinct,
   retryable not-ready outcome rather than accepted or unreachable.
11. Existing VR player locomotion remains tracker-driven and unaffected.

## Technical Requirements

1. `NavigationBase` owns path, facing, ramp, tolerance, and completion policy, but never mutates the navigated actor.
2. A concrete implementation passes the actor's authoritative current `Transform3D` to each poll and receives one
   coherent, immutable navigation motion-intent snapshot. The base calculates against that current state and the stored
   destination request; the implementation decides how, or whether, to apply the resulting intent.
3. Each snapshot reports the next path position, travel direction, desired horizontal facing direction, signed yaw
   error, remaining and travelled path distances, and separate position, facing, and combined completion states.
4. Travel and facing remain separate so consumers can choose forward, diagonal, side-step, backwards, or turn-in-place
   movement. Public navigation APIs must not use animation or motion-matching vocabulary.
5. Each poll performs one coherent Godot path sample/advance operation. The concrete implementation controls polling
   frequency and caches the resulting snapshot between its own queries.
6. Initial facing is captured from the first valid sample of an accepted request. Travelled path distance commits only
   forward arc advancement projected from authoritative actor samples onto the active navigation path. Perpendicular
   and backward movement add no progress. Fallback publication, path-index changes, and replans re-anchor without jumps
   or rewinds; each sample still recomputes downstream waypoint and terminal intent from the current path.
7. Ramp interpolation uses smoothstep. The initial ramp blends from captured facing towards initial path travel over
   `InitialFacingRampDistance`; downstream ramps blend towards each requested facing over `FacingRampDistance` before
   its waypoint. Downstream precedence and the short-move override are deterministic.
8. Position completion requires both actor proximity to the requested destination within `DestinationReachedDistance`
   and terminal remaining-path proximity within `PathDesiredDistance`. An empty path satisfies the path condition only
   when the actor is already within the destination tolerance, preserving same-position turn-in-place requests.
9. `DirectTransformNavigation` defaults `PathDesiredDistance` and `DestinationReachedDistance`, which maps to
   `NavigationAgent3D.TargetDesiredDistance`, to 0.05 m. The defaults remain configurable for future consumers and keep
   destination tolerance materially below the default `ShortMoveDistance`.
10. Invalid or degenerate path positions, segment bearings, destination facing, or distance values produce finite,
   stable intent and cannot leak non-finite vectors, yaw errors, or distances to consumers.
11. Map readiness, destination validation, and initial reachability are resolved before request state is committed. A
   rejection leaves the current valid request, Godot target, accepted path, and cached intent unchanged. Synchronous
   reachability defaults to the base node's authoritative Node3D-ancestor world position. Implementations may override
   that start; the direct implementation uses its resolved actor target rather than potentially stale NavigationServer
   agent state.
12. `NavigationBase` is the Godot-backed `NavigationAgent3D` boundary; the public navigation facade must not expose the
      concrete agent. Request results distinguish accepted, unreachable, invalid, and map-not-ready requests.
13. `DirectTransformNavigation` remains the baseline and test implementation. It may apply snapshots directly to an
     explicitly configured `Node3D` target, with the closest `Node3D` ancestor as fallback. The target is normally an
     ancestor so the agent follows it; other targets require external transform synchronisation. It applies gradual yaw
     around world up to the complete existing world basis, preserving scale and non-yaw orientation beneath rotated and
     non-uniformly scaled parents. It must not assign the actor transform when elapsed time is zero or intent is already
     complete.
14. Navigable actors expose navigation through the component-holder pattern; navigation does not discover or own them.
15. Existing path inspection, threshold, and initial avoidance configuration remain available through the navigation
     abstraction. Safe-velocity callback mechanics are not part of this slice.

## In Scope

- Navigation component and holder contracts, request result, Godot-backed policy base, and immutable polled intent.
- Horizontal path-facing calculation, path-distance ramps, short-move override, and precise path-aware completion.
- Atomic validation of new requests and stable handling of invalid or degenerate path data.
- `DirectTransformNavigation` as a baseline and test consumer with precise, configurable distance defaults.
- Path inspection, threshold configuration, and initial avoidance configuration through the navigation abstraction.

## Out Of Scope

- Production root-motion integration and rich trajectory prediction.
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
6. Completion scenarios verify independent configurable position and yaw tolerances, combined completion, and
   turn-in-place intent after positional arrival while yaw remains outstanding. A same-position request with an empty
   path remains valid and completes only after its requested facing is reached.
7. Initially unreachable and map-not-ready requests leave an existing valid request unchanged. The not-ready result is
   distinct from unreachable, and VR tracker locomotion remains unaffected.

### Technical Requirement Acceptance

1. Tests confirm `NavigationBase` calculates policy without mutating an actor and the concrete implementation supplies
   the actor's authoritative current `Transform3D` to each poll. Snapshot calculations use that current transform while
   preserving the accepted destination `Transform3D` as separate stored request intent.
2. Each poll performs one Godot path sample/advance operation, exposes every required travel, facing, distance, and
   completion value from that operation, and remains cached between implementation-controlled polls.
3. Tests exercise forwards, diagonal, side-step, backwards, and turn-in-place intent without coupling travel direction
   to facing direction or introducing animation-specific public vocabulary.
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

## References

- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md)
- `game/src/Navigation/`
