---
id: NAV-001
title: NPC Navigation
---

# NPC Navigation

## Requirement

Animate one NPC along Godot's native navigation-agent path in the existing scene, with in-process Godot/C#
MotionBricks generation on NVIDIA hardware and game-owned dynamic source conditioning under ANIM-002.
This is the implementation/review contract. Normal NAV uses the shared eight-role source set and native path/handoff,
including finite terminal-facing turns; broader content and generated-motion quality acceptance remain open.

## Goal

Let the user judge whether generated motion is naturally continuous and precise enough for navigation, without
preserving a parallel clip-planning implementation or disguising model limitations with corrective locomotion.

## User Requirements

1. Reuse `game/assets/testing/navigation/navigation_test.tscn`, its destination position/facing selection, authored
   waypoints, and static obstacle layout. Do not introduce a second navigation scene.
   - Right-drag orbits, middle-drag pans, and the wheel zooms; camera gestures do not submit destinations.
   - Blue hover previews the pointer's collider hit. Left press locks that position in amber; dragging previews facing
     live, and release submits once. Free hover resumes afterwards, hidden while navigation runs or no collider is hit;
     there is no persistent committed-destination marker.
   - Shift-click queues visible ordered pass-through waypoints without submitting; submission or Escape clears them.
   - Speed UI consumes input without starting world gestures. Releasing over UI ends camera gestures and cancels
     placement without submission; Escape cancels pending gestures as well as navigation.
2. The NPC departs idle, follows Godot's native route through static detours and pass-through waypoints, and plays an
   arrival sequence targeting the requested position/facing before settling into idle. Waypoints do not require stops.
    Sequence completion does not guarantee endpoint convergence; expose the realised endpoint for user evaluation.
    Modest generated deviation from the path is tolerable, not wide arcs, circling or substantial veering away.
    Do not reject a native-valid route because a custom motion guide or geometry proof cannot represent it.
3. A destination set during movement supersedes the old request from current realised motion, without an idle reset.
4. Requests accept optional normalised speed `0 < n <= 1`, relative to the selected locomotion variant's maximum.
   Provide a reasonable constant default and a playtest control. Requested speed is not an exact realised-speed promise.
5. An ordinary route/collision failure has a finite, truthful reported outcome followed by one generated transition
   into idle when inference is available. Escape cancels and generates an idle transition. If setup or inference is
   unavailable, report the cause and retain the last safe pose instead; do not attempt an elaborate fallback.
    Repeated shuffling or an endless attempt to correct the endpoint is not an acceptable outcome. External physics
    may block realised movement; this does not promise prediction of every obstruction or automatic stall recovery.
6. The user judges motion naturalness and endpoint precision in the playable scene. Diagnostics expose limitations;
   neither endpoint agreement nor a static screenshot proves acceptable motion.
7. Navigation selects source roles and posture appropriate to remaining travel, actual motion and desired facing.
   Nearby positioning favours direct forward/backward steps or non-crossing left/right sidesteps where feasible,
   rather than an unnecessary forward-facing loop, detour or circling. Longer journeys turn towards the valid route
   and walk forward, allowing turning while stepping rather than requiring a stationary turn before departure.
   Crossing lateral travel is used sparingly, only for bounded short relocation where retaining facing makes sense;
   it is not routine diagonal travel. Reconsider positioning and facing near the destination and after intent/route
   changes, without role chattering. Stationary turns remain available where appropriate, including final facing,
   but do not replace simple translation or introduce a recovery sequence. Lateral roles are repeatable, not mandatory
   transition clips. Variant/stage physical speeds govern movement intent; source-phase progression must not delay
    replacement/cancellation or require displayed support-foot matching. Idle remains externally owned and compatible
    with the selected posture.
   Once positioning is reached, an unchanged terminal-facing request uses a finite source-cycle/landmark turn plan.
   Preserve its ordered stepping cues through playback, then finish the requested facing and settle into compatible
   idle. A large turn should read as one large turn followed by any smaller remainder, without sustained
   rotation against the commanded direction or reversal of actor facing from generated hip motion.
   Ordinary lookahead/handoffs must not skip cues or create endless small adjustments. Replacement, cancellation,
   collision and genuine motion-provider failure remain truthful interruption boundaries, not promises to finish.
8. Automatically record ordinary play in the existing navigation test scene, with no additional recording switch.
   Let the user mark a bad scenario, save feedback while continuing play, and share the folder for recorded-state
   inspection and focused reproduction/regression work before manually repeating the scenario. Show numbered attempts
   (including refusals), accepted request identity, session path and save/recording health. Do not upload automatically;
   accumulated recordings remain for the user to delete manually. This feedback loop does not require routine video
   capture or an exhaustive motion-quality campaign.

## Technical Requirements

### Runtime and Content

1. Run inference and required MotionBricks runtime helpers in-process through Godot/C#, initially for one NPC on
   NVIDIA hardware. No Python sidecar, Smart Primitives port, or full Python demo port is permitted.
2. Establish the exact ONNX binding, execution provider, and deployment dependencies through a representative
    execution probe. Installed libraries or upstream examples alone are not proof of an operational runtime.
    The current path uses ONNX Runtime GPU with CUDA and a separately provisioned SOMA FP32 model (ANIM TR6).
    The exported `ModelPath` defaults to `res://models/motionbricks/onnx/mmm_v6_soma_fp32.onnx`; an optional non-blank
    `MOTIONBRICKS_MODEL` takes precedence. Globalise the selected path on the scene thread before native worker IO.
    Ordinary source-checkout playtests need neither the override nor `LD_PRELOAD` on the verified host; this is not
    a universal CUDA compatibility claim. Retain host-specific dependency checks and the prerequisites in the
    [runtime README](../../../game/src/Motion/README.md#launch).
3. The [shared animation contract](../../animation/index.md) and
   [ANIM-002](../../animation/002-locomotive-animation-set/index.md) are normative for generic speed stages, dynamic
   source windows, idle/action target compatibility, SOMA conversion, realised feedback and licensing;
   [ANIM-003](../../animation/003-animation-catalogue/index.md) supplies idle targets and the locomotion view.
   Normal installation loads the reviewed set resolved by that view (default `feminine`, binding
   `reviewed-feminine/set.json`, shared with CTRL and bound wholly to the current `landing-ablation-01.json`
   source). No source/controller activation flag or prototype movement fallback is required.
4. Demonstrate generated walk-to-idle on the actual MakeHuman rig with realised-pose feedback before expanding route
   integration. The NPC presents generated motion through native retargeting with digits mapped and feeds back the
   final target pose under [ANIM TR4–5](../../animation/index.md#technical-requirements). Bone-name correspondence
   alone is insufficient.

### Navigation and Motion Ownership

5. Godot's `NavigationAgent3D` owns path selection, native path-point progression and rerouting, including static
    detours. The game preserves ordered requested pass-through vias using native routing, adapts the current native
    path into animation targets, and owns request identity/replacement, collision-realised movement and terminal
    outcomes. MotionBricks bridges supplied target pose windows, not semantic intentions or a replacement route planner.
    Accept native-valid paths without bespoke mesh-shape, topology/connectivity, forecast-centre or turn-radius
    admission proofs. Do not require a single flat rectangular navigation region or refuse vias categorically.
    Preserve finite-input, native availability and request/route ownership checks; accepting a path does not authorise
    consuming stale work or ignoring actual collisions and failures.
   Preserve the character-facing `INavigation`/`INavigator` capability boundary rather than creating a parallel
   character hierarchy. The normal NPC role installs `MotionBricksNavigation` as its `NavigationBase` and rebinds
   `Character.Navigation`; this is not a diagnostic controller beside the production role.
6. A minimal motion adapter samples targets by distance ahead along the same ordered native path, using the agent's
    current progress, selected variant/stage physical metadata, requested normalised speed and horizon. It does not
    construct a replacement route, custom turn arc or off-path guide. Destination facing remains game-owned intent;
    source role selection does not replace native path authority or require every turn before translation.
    Realise generated root motion through Godot's `MoveAndCollide` using the actor's actual root collider, following
    the [ANIM TR4 root and scale rule](../../animation/index.md#technical-requirements) for once-applied planar
    demand, game-owned yaw and source-hips travel removal.
    Feed actual realised motion back into generation; do not silently teleport the actor into endpoint agreement.
     `MotionBricksNavigation` is the single collision-root movement owner. Targets follow native path order through
     corners and vias, without obstacle shortcuts or requiring every target window to fit one straight segment.
    Modest realised deviation under UR2
    is not a reason to reject the native path. Speed changes target-displacement intent, not playback time or a
    guarantee of measured speed. Do not apply a walking-specific distance to crawling or confuse phase-local velocity
    with cycle/reference speed.
    Native path validity and route ownership constrain navigation intent, not guaranteed physical clearance. Do not add
    bespoke predictive physics admission: no swept-body/clearance forecast, support-floor proof or `TestMove` preflight.
    Actual collisions and external physics may limit realised movement; this is not a physics-aware planner.
7. Destination replacement uses current realised motion rather than replaying departure from idle. Ordinary
    navigation-agent rerouting is allowed; new stall-triggered rerouting is deferred. Detection and reporting of
    supported navigation failures remain navigation responsibilities;
   MotionBricks supplies the transition into idle after failure, not a corrective manoeuvre planner or collision AI.
    Terminalise ordinary failure before that one transition, using realised history; keep engine collision handling
    active throughout without triggering another transition. Do not replace it with a fixed-duration blend or
    retry loop.
8. Do not retain a parallel legacy implementation, compatibility machinery, clip-seam correction, or add a foot-IK
    rescue system. Preserve unrelated character, VR, arm/hand IK, and physical interaction behaviour.

#### Outcome and Worker Contract

The following clauses refine TR5–8 and are covered by NAV-TAC-002:

- `Completed` with `ArrivalSequenceCompleted` means the generated arrival sequence ended, not that the destination
  converged. Record the measured transform, position/facing errors, configured tolerances and signed margins as
  diagnostics. For stationary-turn facing, measure actual actor yaw under the [shared actor-facing rule][turn-facing],
  not planned yaw as a proxy for observation. Otherwise, facing comes from realised root/hips orientation, not an
  unchanged actor-node basis. Commit the outcome before ordinary idle playback. Preserve convergence-based
  `TryComplete` semantics for other consumers.
- Setup/inference unavailability logs and retains its cause, fails accepted requests with `MotionUnavailable`, and
  preserves that cause on subsequent requests. Retain the last safe pose under normal scheduling, including pose-input
  reapplication where modifier output is transient. No generated idle, authored-idle reset or elaborate blend/retry is
  required when inference is unavailable.
- Replacement invalidates old ownership and uses current displayed context without an idle reset. Copy submitted pose
  arrays; retain only the latest queued request and reject stale results. Worker factories/callbacks must not access
  live scene nodes. Submission, cancellation and result consumption belong to the scene thread; native work does not.
- The MotionBricks model is a shared backend under [CORE-011](../../core/011-shared-model-services/index.md), which
  is normative for its loading, scheduling, failure caching and disposal. Each NPC's worker is a per-consumer handle:
  it owns its codec state, request ownership and latest-wins supersession, not model construction or native disposal.
  Ordinary scene exit releases the handle without waiting and observes completion/faults asynchronously without
  retaining scene nodes. The shared native session is disposed only when the owning `Game`'s lifetime ends (CORE-011
  TR24), after any in-flight native call returns, never concurrently with it. Menu exit and root-window close share
  one application-shutdown boundary: reject new MotionBricks work, and asynchronously await completion, including
  native disposal, before final application quit.
  Cancellation and the 30-second request budget are logical: they cannot interrupt native load/CUDA calls. A native
  call that never returns prevents graceful application exit indefinitely; do not promise bounded native shutdown.
  Forced process termination and direct `SceneTree.Quit` bypass this boundary and are not supported graceful-exit paths.

### Validation and Delivery

9. Focused automated checks cover runtime contracts, coordinate/pose conversion, realised-context sampling, state
    ownership, request replacement, engine collision-realised movement, and finite reported navigation outcomes.
   Playtest selection uses physics-tick raycasts with collision mask 1, not an infinite ground plane or a navigability
    filter: obstacle hits may preview without a valid native route. Do not reject a native-valid route merely because
    the hit is on an obstacle. Check the viewport/GUI gesture boundary as well.
10. Capture full temporal evidence on the displayed MakeHuman rig: initial idle, departure, sustained walking,
    turns/detours, waypoint passage, arrival/facing transition, and settled idle. Separately capture replacement
    while moving and navigation failure through idle and the reported terminal outcome.
11. Measure endpoint position/facing error, foot slip, discontinuities, and inference latency diagnostically. Do not
    inherit legacy numerical quality gates or invent arbitrary thresholds. Distinguish integration defects from
    model limitations; escalate material design changes or model unsuitability rather than adding corrective policy.
12. Before playable handoff, run relevant automated checks and repository format/build validation, inspect temporal
    evidence under the visual-verification guidance, and have a dedicated reviewer independently inspect source,
    captured images, and temporal evidence.
     Fix admitted blockers and re-review. Report failures and their scope honestly, including player failures.
    Required commands are `dotnet format --verify-no-changes AlleyCat.sln` and
    `dotnet build AlleyCat.sln -warnaserror`; relevant Godot tests follow the integration-testing guidance.
13. Delivery identifies the scene and launch instructions, required local model/runtime setup, evidence paths,
     diagnostics, review disposition, separate player scope, and the outstanding user suitability decision.
     Do not commit bulky model artefacts or secrets.
14. **Conditioning Controller:** navigation owns variant, state, role, virtual source clock/phase, expected source foot
    phase and target selection under the [ANIM-002
    lifecycle](../../animation/002-locomotive-animation-set/index.md#technical-requirements). Select the role before
    constructing its motion guide, under the selection policy below. Sample future consecutive source windows using
    virtual playback and target horizon outside the finite terminal-facing exception below, preserving local motion
    while adapting the native path under TR5–6; engine physics determines realised movement. Apply the shared advance,
    loop/end, role/stage/variant-switch and overlapping-request rules. Snapshot phase/window/placement with request
    ownership; model duration/completion must not set elapsed source-clock pace. The finite plan's service cursor
    advances only when its owned landmark endpoint is actually played. Replacement, cancellation and terminal outcomes
    supersede old progression immediately; do not wait for source-cycle completion or require a matching displayed
    support foot. A displayed catalogue-idle or arrival bridge keeps playing until its replacement is installed, under
    [ANIM-003 TR20–21](../../animation/003-animation-catalogue/index.md#transitions). Connect from actual context to a
    new role or compatible ANIM-003 idle/external action targets. Missing/incompatible inputs follow the explicit
    unavailable-motion policy, without a fabricated idle anchor, procedural fallback or retry loop. Preserve
    route/outcome, single collision-root and worker guarantees; no leading-foot variants, dedicated arc-turn or
    start/stop/reversal clips are prerequisites.
15. **Playtest Feedback Recording (UR8):** restrict automatic recording to the existing navigation test scene. The
    [recorder workflow and schema](../../../game/src/Navigation/Recording/README.md) are normative dependencies for
    controls, storage and bundle interpretation. Correlate attempts/bookmarks, accepted requests and inference tickets
    without conflating them. Preserve full command inputs (actual starting pose/facing, destination transform, ordered
    vias, speed, replacement, cancellation and refusals), effective settings/source identity, submitted context/targets,
    actual returned model chunks including stale results, role decisions, post-modifier actor/bones/blendshapes,
    aligned camera/timing and truthful outcomes. Observe read-only: do not change source selection, realised history,
    collision-root ownership, pacing, routing, outcomes or worker ownership to make recording succeed. Bound memory;
    use asynchronous storage, explicit loss/truncation/error reporting and atomic checkpoints readable during continued
    play; recording failures must not become navigation/inference failures. Drain native workers before finalising the
    recorder. Record no secrets or full environment dump. Support inspection of recorded sampled states without fresh
    inference; do not promise identical fresh inference/live physics, every rendered frame, pixel-perfect reproduction
    or a whole-asset archive. Clearly label incomplete/lossy sessions rather than describing them as exact.

#### Role Selection Policy

The following clauses refine TR14 and UR7; NAV owns this policy, not CTRL's joystick-component mapping:

- Base selection on actual realised orientation/motion, intended route travel, remaining journey/positioning distance
  and desired facing. Use post-modifier realised context under the shared animation contract, not actor-node facing,
  source phase or a previously constructed forward guide as a substitute for current motion.
- For nearby feasible positioning, favour forward/backward steps or non-crossing sidesteps according to displacement
  relative to realised facing. A short diagonal does not universally require crossing, nor a short rear move a turn.
  Reserve crossing roles for bounded short relocation where preserving facing is appropriate. For longer journeys,
  turn towards valid route travel and walk forward rather than sustaining backward, sideways or crossing travel.
  Remaining destination and route context govern this distinction: short local navmesh segments do not make a long
  journey into a series of positioning moves. Preserve valid detours and waypoint order; direct positioning does not
  authorise obstacle shortcuts or destination projection. Turning may accompany forward stepping.
- Re-evaluate remaining displacement/facing near the destination and after intent or route changes. Use configurable
  distance/angular bounds and bounded hysteresis for stable choices, not universal gait or motion-quality thresholds.
  Hysteresis must not delay cancellation/replacement or force source-cycle completion. Final stationary turning is
  available when appropriate, not a mandatory stage or substitute for feasible simple translation.
- Expose the selection inputs and decision before guide/window placement. Each owned request snapshots the selected
  role, variant/stage, intended phase, actual context, source window and route-bounded placement. A changed selection
  invalidates incompatible queued/in-flight work; late results must not restore the old role or guide. Apply ANIM-002
  phase transfer/initialisation and compatible-overlap rules without treating source phase as actual displayed motion.
  Replacement/cancellation supersede old ownership immediately, with no idle reset or mandatory cycle completion.
  During the finite terminal-facing plan below, ordinary role/lookahead reconsideration must not abandon the unchanged
  request's remaining landmarks; its explicit replacement/cancellation and safety interruption rules still apply.

#### Finite Terminal-Facing Turn Plan

These clauses refine UR2/5/7 and TR14 only after position is reached and terminal facing still requires a stationary
turn. They do not require turn-first departure or change native routing. Use the normative ANIM-002
[stationary-turn angular mapping][turn-mapping] shared with CTRL-001, including its [actor-facing rule][turn-facing]:
the game sets actor facing from planned cycle yaw, independently of generated articulation.

[turn-mapping]: ../../animation/002-locomotive-animation-set/index.md#stationary-turn-angular-mapping
[turn-facing]: ../../animation/002-locomotive-animation-set/index.md#actor-facing-during-stationary-turns

- **Finite Angular Allocation:** bind the unchanged request, actual starting orientation, signed remaining arc and
  selected source. Allocate the remaining arc greedily up to the shared per-cycle maximum, then allocate the
  remainder: 180 degrees becomes 120+60, not repeated receding lookahead targets. Do not impose a minimum cycle
  angle. Two primary lifts per source cycle yield four ordered lift targets over that example.
- **Endpoint Service and Ownership:** keep planned source phase/landmark cursor separate from elapsed observation
  time, context horizon and variable generated duration. Source phase is an ordered plan coordinate, not a clock
  advanced by model output length. Each planned primary landmark must be the first guide and reach its actually
  played endpoint before the next ordinary handoff can truncate it. Planning the next connection ahead is allowed;
  consuming it early, skipping cycles/cues or replacing them with elapsed-lookahead samples is not. Snapshot plan,
  cycle, landmark, mapping and request identity immutably. Each submission uses actual post-modifier realised context,
  never a sampled source pose or unplayed prediction. Endpoint service proves playback, not lift, support or contact.
- **Finite Finish:** after the last primary lift, complete the remaining mapped cycle angle and connect from actual
  context to a compatible ANIM-003 idle target at the requested final position/facing, rather than stopping with a
  raised-foot target. This is a finite end sequence, not a new idle system.
  The existing reached-position contract/configured tolerance admits this end sequence; the turn source's incidental
  translation distance must not re-gate final arrival or restart small-turn/cue-catch-up loops. Completion remains
  sequence completion independent of convergence, with realised errors reported, not a new threshold or repair.
- **Safety and Feasibility:** replacement/cancellation invalidate the plan and stale results immediately; actual
  collision or genuine provider failure interrupts truthfully under TR5–8. Preserve native path authority, the
  request-local lockout fix, worker lifetime, generated-root `MoveAndCollide`, rig modifiers and endpoint handling.
  No corrective actor rotation, output repair, foot IK, forced generated duration or unbounded debt/recovery planner is
  permitted. Correct mapping, context, supplied cues, untruncated playback and rig fidelity make poor motion evidence
  about source suitability for this angular mapping and/or the MotionBricks connector, not proof of either's sole
  fault. Reached landmarks alone do not guarantee coordinated stepping, acceptable pace or non-sliding settled arrival.

## In Scope

- Single-NPC animation along native-agent paths, existing-scene controls, engine collision-realised root movement,
  and idle.
- Actual-rig conversion and feedback, runtime setup, source/licence checks, diagnostics, and automated/temporal
  evidence.
- Upfront replacement of obsolete locomotion machinery and independent review before playable handoff.
- Game-owned source role/phase control, physical-speed placement and dynamic sampling with ANIM-003 idle integration.

## Out Of Scope

- Player locomotion restoration, multi-NPC batching, broad hardware support, and ARDY integration.
- Terrain/contact adaptation and a general action/idle catalogue. ANIM-002 role coverage, generic variants/stages,
  physical speed metadata and lifecycle remain required; pending migration is not an exclusion from delivery.
- Smart Primitives. AgenticMind coordinate navigation tools consume this navigation contract under
  [AI-015](../../ai/015-coordinate-navigation-tools/index.md).
- Physics-aware planning, bespoke predictive physical admission, and new stall detection/recovery or stall-triggered
  rerouting. Existing navigation-agent rerouting and actual engine collision handling remain in scope.
- Wrist/finger fidelity refinement and a future evidence-derived endpoint quality gate. These do not waive gross-body
  posture, safe-pose retention, truthful diagnostics or full-window verification for this playable slice.

Player delivery is separate under [CTRL-001](../../ctrl/001-locomotion/index.md); NPC acceptance does not establish
player readiness. A broken NPC playtest is not a delivered prototype.

## Acceptance Criteria

### User Requirement Acceptance

1. **NAV-UAC-001 (UR1–2):** In the reused scene, temporal evidence shows the full idle-to-route-to-idle sequence,
   including static detours, pass-through waypoints, and the arrival sequence targeting the requested facing. Inspect
   the full window, including inference holds, for the pronounced diagonal/raised-spread posture defect; isolated
    favourable frames or diagnostic angles do not establish its absence. Compare actual motion with the native path:
    modest deviation is acceptable, but wide arcs, circling and substantial veering are not. A successful component
    check does not establish the manual journey or motion quality.
   Interactive checks show orbit/pan/zoom without submission, blue collider-hit hover, amber locked placement with
   live facing, one submission on release, and free preview resuming only when navigation is not running and a hit
   exists. Verify visible ordered vias and clearing on submission/Escape, speed UI consumption, and UI-release/Escape
    cancellation without stuck gestures or accidental submission. Distinguish native-unreachable selections from
    native-valid submissions; a visible preview does not promise a route, but a custom guide must not veto a valid one.
2. **NAV-UAC-002 (UR3–5):** Interactive checks show speed control/default behaviour, a replacement destination during
   motion without idle reset, Escape-to-idle, and a finite reported ordinary failure followed by generated idle.
   Unavailable-inference evidence instead shows truthful failure, preserved cause on later requests and last-safe-pose
    retention across the failure boundary and subsequent observation window. Report an externally blocked/collision
    outcome truthfully when observed; predictive physical clearance and new stall recovery are not acceptance gates.
3. **NAV-UAC-003 (UR6):** The playable candidate and measured diagnostics are presented for the user's naturalness
   and endpoint-precision judgement. Prototype readiness is reported separately from that suitability decision.
4. **NAV-UAC-004 (UR7):** Full actual-rig temporal demonstrations cover representative nearby left/right, front/rear
   and diagonal positioning, contrasted with longer journeys. Feasible small lateral moves use non-crossing sidesteps
   without unnecessary forward detours or circling; short front/rear moves favour direct steps. Longer lateral,
   diagonal and rear destinations turn towards the valid route and walk forward, allowing turning while stepping.
   Show crossing used sparingly for short facing-preserving relocation, not routine diagonal or sustained side travel.
   Include desired-facing differences, a genuine obstacle detour, near-destination reconsideration and a changed
   destination during motion. Choices remain stable without delaying replacement/cancellation; stationary final
   turning does not replace feasible simple translation. Inspect variant-appropriate speed/posture, repeatable lateral
   motion and compatible ANIM-003 idle. Do not require every short diagonal to cross, every rear move to turn,
    displayed-foot matching, fixed step counts or mandatory transition clips.
   For the finite terminal-facing plan, inspect the complete fixed-request turn and settled arrival on the actual rig,
   including inference holds and every planned primary cue. Check a 180-degree request reads as one large turn plus
   a smaller finish, without sustained counter-rotation or hip-driven reversal of actor facing. The user judges
   coordinated stepping, acceptable pace and non-sliding arrival; endpoint agreement and synthetic service checks
   do not close those outcomes.
5. **NAV-UAC-005 (UR8):** In the existing scene, recording starts without an extra switch; the user can identify an
   attempt (including a refusal or visually bad completed request), bookmark it, save a readable session while play
   continues, and share its folder for focused investigation and manual repetition. Path, identity, save progress and
   loss/error status are visible. No automatic upload or retention deletion occurs.

### Technical Requirement Acceptance

1. **NAV-TAC-001 (TR1–4):** A representative in-process NVIDIA execution and actual MakeHuman walk-to-idle run prove
   the selected runtime, required helpers, native retargeting with mapped digits, and final-target realised SOMA
    feedback against the shared animation contract, including ANIM-TAC-002's blocked-step and perturbation check.
    Verify the globalised exported default and non-blank override precedence, and record representative
     loading/generation without `MOTIONBRICKS_MODEL` or `LD_PRELOAD` on the supported host.
   Normal scene installation without source activation variables binds all eight roles and angular metadata to the
   same source as CTRL; missing/inconsistent content is an explicit failure, not fallback motion.
2. **NAV-TAC-002 (TR5–8):** Automated checks and runtime traces demonstrate navigation/motion ownership, engine
    collision-realised movement, request replacement, and truthful outcomes without endpoint teleportation or corrective
    manoeuvre policy. Source review confirms actual-root `MoveAndCollide` handling without bespoke predictive physics
    admission, while preserving native path authority, route ownership and realised feedback. Focused checks cover
    native-valid request acceptance and ordered target progression through a corner and requested vias without custom
    geometry, connectivity, forecast-centre or turn-radius admission gates. Separate actual native unavailability or
    invalid/stale ownership from limitations of the animation adapter. Route intent is not a physical
    clearance guarantee; do not require a substitute forecast or new stall detector/recovery.
    Source/composition review confirms no parallel legacy runtime or foot-IK rescue is installed.
    Verify sequence completion independently of convergence, preserving diagnostics outside configured tolerances and
    the unchanged convergence API. Check copied latest-request ownership, stale rejection, and nonblocking ordinary
    scene exit during actual native load/generation, with shared-session disposal only after native return under
    CORE-011-TAC-008. Verify menu exit and OS root-window close through actual process exit without a crash, with
    shared backend disposal preceding final quit after NPC scenes are removed. Check rejection of new work once
    shutdown starts. Do not treat direct `SceneTree.Quit` or forced termination as graceful-exit evidence, or successful
    representative exits as universal provider safety. Verify timeout/failure handling separately;
    successful calls or model-load exceptions do not prove timeout handling or failure during moving generation.
    Record safely exercised cases and unresolved evidence gaps; inducing a genuine GPU hang is not required.
3. **NAV-TAC-003 (TR9–13):** Focused checks, full temporal captures, diagnostic measurements, repository validation,
   independent review, and the setup/evidence handoff are recorded. Deferred failures are not reported as passes.
    Source review and interactive checks confirm mask-1 physics-hit selection and safe viewport/GUI gesture handling.
4. **NAV-TAC-004 (TR14, TR3, TR5–8):** Apply ANIM-002 sampling/lifecycle acceptance to route-driven requests.
   Focused selection checks and runtime traces cover NAV-UAC-004's short/long contrasts, including realised-facing
   changes, remaining-route context versus short local subdivisions, desired facing, obstacle and waypoint constraints.
   Verify role selection precedes guide construction and metadata-based window placement, with inspectable inputs and
   decisions rather than a forward-guide bias or inherited joystick rule. Check near-destination/intent/route
   re-evaluation and configurable bounded hysteresis, independently of generated-motion quality. Role changes snapshot
   actual context separately from intended phase and invalidate incompatible work; stale completion cannot restore
   the previous role/guide. Immediate replacement/cancellation must not wait for hysteresis or source-cycle completion.
   Check physical reference/local velocity distinctions, sample-ahead and loop boundaries, role/stage/variant
   switches, arrival/failure/cancellation handoffs and compatible overlaps. Trace intended phase separately from
    actual context and generated duration; stale results cannot restore old route/state. Preserve native path order,
    collision-realised feedback, finite outcomes and safe native shutdown under the dynamic source controller.
   For finite terminal-facing plans, verify 180→120+60 allocation and the same mapping component as CTRL under
   ANIM-002-TAC-001–002. Check scale, angular placement at both primary lifts, consecutive four-pose windows and
   retained local reversals/boundaries. Verify unchanged source timing and compare displayed cycle durations with the
   source, including inference holds and ordinary handoffs. Apply ANIM-002-TAC-002's per-tick planned-yaw check,
   including hip independence, once-applied yaw, unchanged endpoint and actual post-modifier worker context.
   Verify immutable fixed-request authority, actual-context provenance and that every first-guide endpoint actually
   plays before ordinary handoff, rather than just being submitted/generated. Check final-angle/compatible-idle
   completion without source-translation gating or
   repeated small-turn loops, plus replacement/cancellation, stale rejection and truthful collision/provider failure.
   Keep planned phase/service separate from elapsed time and native duration. Synthetic checks establish technical
   mechanics only; NAV-UAC-004's full actual-rig turn/settled window remains mandatory.
5. **NAV-TAC-005 (TR15, UR8):** Focused bundle inspection and ownership review establish correlated full inputs,
   submitted/returned motion and final sampled display/camera states, readable ongoing checkpoints, bounded-memory
   nonfatal recording and worker-before-recorder finalisation. Distinguish transport completion from navigation success,
   report loss/truncation and reproduction limits, and preserve the existing motion/route/worker contracts. Synthetic
   evidence establishes only the exercised technical recording contract, not native quality or manual suitability.

## Implementation Status

Normal NAV loads the set resolved by the ANIM-003 locomotion view (default `feminine`, `reviewed-feminine/set.json`),
wholly bound to `landing-ablation-01.json`, without activation flags. Native catalogue idles, idle↔idle and
idle↔locomotion bridges, and interruption with velocity blending are delivered under
[ANIM-003](../../animation/003-animation-catalogue/index.md#implementation-status).
The existing scene uses Godot's native path authority, source-rate target placement/native handoff and the finite
terminal-facing plan above. Both primary lifts are source-angle mapped per allocation and actually serviced before
the next planned submission; fresh post-`SkeletonUpdated` context precedes one finite ANIM-003 idle arrival connection.
Request-local overrun/lockout handling, stale-result rejection, provider-failure retention, generated-root
`MoveAndCollide` and worker disposal remain required. The automatic scene recorder remains part of normal play.
Planned-yaw facing during stationary turns is in progress. Migration to TR4 native presentation and final-target
feedback is in progress and not yet verified.

Technical checks cover shared source identity/normal installation, finite cue service, route replacement/cancellation,
actual-context/handoff and overrun preservation checks, with clean format/build results. Combined independent review of
this normal-source migration remains pending; native-free component evidence is not native gait, arrival quality or
performance proof. Source stepping/foot-sliding mitigation is user accepted with initial twitch deferred, not certified
no-slip motion. Full actual-rig route/turn/settled-arrival and user naturalness/pace judgement remain open, including
the unresolved around-corner/arrival outcomes. Raw repetition admission, broader stage/default content and previews,
headset and known native-shutdown risks remain deferred-with-risk. Plan steps 5/6 and final acceptance remain open. No
new source generation, model-call budget or shutdown retry follows from this documentation alignment.

## References

- [Project Specifications](../../index.md)
- [AI-015: Coordinate Navigation Tools](../../ai/015-coordinate-navigation-tools/index.md)
- [Shared Animation Contract](../../animation/index.md)
- [ANIM-002: Locomotive Conditioning Sources](../../animation/002-locomotive-animation-set/index.md)
- [CORE-011: Shared Model Services](../../core/011-shared-model-services/index.md)
- [Runtime Setup and Evidence Limits](../../../game/src/Motion/README.md)
- [Navigation Recorder Workflow and Schema](../../../game/src/Navigation/Recording/README.md)
- [Character Root](../../character/002-character-root/index.md)
- [Leg and Feet IK](../../ik/003-leg-feet-ik/index.md)
- [Visual Verification Scope](../../testing/002-visual-verification-scope/index.md)
- Upstream reference checkout: `/home/mysticfall/workspace/motionbricks`, especially `docs/runtime_api.md`,
  `docs/onnx_inference.md`, `docs/soma_model.md`, and `docs/scene_preview.md`. These establish model/runtime facts,
  not proof of this project's implementation.
