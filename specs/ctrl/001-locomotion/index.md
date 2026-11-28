---
id: CTRL-001
title: Player Locomotion
---

# Player Locomotion

## Requirement

Deliver headset-playable MotionBricks player locomotion through the existing player startup route, with game-owned
source playback and dynamic conditioning under ANIM-002. Normal installation uses the shared eight-role feminine
source set and source-bound stationary-turn conditioning without activation flags. This is the implementation/review
contract, not a claim of completed content, headset or generated-motion quality acceptance.

## Goal

Let the user judge stick responsiveness and body-turn feel with live tracking before expanding locomotion or polishing
motion. Implementers and reviewers must preserve existing XR, hand, gaze, and interaction behaviour.

## User Requirements

1. Left-controller stick direction follows the existing player input convention; its magnitude supplies normalised
   speed intent. Different magnitudes request different speeds, not an exact measured-speed guarantee.
2. Right-controller stick requests stationary avatar-body turning in either direction. Movement takes priority over
   stationary turning; during travel, right input steers walking under UR7. Full deflection requests about 120 degrees
   per source cycle at source pace, continuing while held. Partial deflection requests proportionally smaller cycles
   at the same pace. Changing strength in the same direction applies from the next cycle without cancelling or
   restarting the current turn. The turn must not sustain rotation against the commanded direction or reverse
   actor facing because of generated hip motion. The head/body divergence limit in UR4 is separate.
3. Releasing movement or stationary-turn input immediately begins settling towards idle and discards remaining intent.
   Releasing a stationary turn must not cause a visible snap in actor facing as settling begins.
   Releasing walking steering alone instead retains the acquired course under UR7. Do not require the
   full queued motion segment or stale turn to finish, or add recovery-step sequences. This is not instant inference.
   MotionBricks generates the settle towards the selected idle stance without driving the collider along the ground.
   Fresh movement or turn input interrupts the settle without resetting the displayed stance at the first replacement.
   Release is distinct from tracked-idle entry: after settling, return to tracked idle only when both sticks are
   neutral and no replacement movement or turn is pending.
4. Tracked head motion stays live. Ordinary body turning must not simply rotate the view with the avatar. Introduce
   body/view separation with a tunable head/body yaw divergence limit defaulting to 100 degrees per side. This relaxed
   allowance accounts for physical hip turning that the avatar does not reproduce, avoiding premature disturbing
   compensation. Beyond the limit use existing XR-origin compensation, not a new body-stop policy. Retain existing
   positional/anatomical compensation within the existing physical scope in TR6; this is not an unconditional
   camera-lock or physical head-to-wall collision guarantee.
5. MotionBricks generates movement, turning footwork, and idle transitions on the reference Female player, presented
   through native retargeting under the [shared animation contract](../../animation/index.md). The player is not
   always animation-driven; tracked idle and modified posing remain part of the experience, and the player's observed
   fingers are never overridden by generated motion.
6. Provide a normal launch path, the implemented stick mapping, known limitations, and a short headset checklist.
   The user's headset judgement decides responsiveness and feel; synthetic input cannot establish comfort.
7. Source-guided walking predicts near-cycle travel and supplies a separate farther generation
   guide without continually discarding useful work under small stick fluctuations. Left travel must not request
   changes to body heading, including strafe/backward travel. While walking, holding right stick continuously steers
   intended travel and heading together; stronger deflection turns faster. Releasing right stick stops steering but
   continues walking along the acquired course. Movement priority selects walking steering, not simultaneous
   stationary turning. Preserve immediate movement
   release/sharp-change handling, a stable movement reference across magnitude changes, and distinct cycle/submitted/
   active diagnostics. Held travel chains useful work without redundant unchanged-input snapshot resubmission.
   Source adoption and chaining do not establish headset usability or resolution of awkwardness, sway or comfort.
   Expose backward requested speed as a tunable ratio of forward requested speed at the same original stick magnitude
   and authored stage. Preserve forward/lateral requests;
   the ratio adjusts intent, not guaranteed actual speed or comfort.
8. Player intent selects distinct forward/backward, non-crossing left/right interaction-positioning sidesteps,
   crossing left/right lateral travel, and stationary turn roles from the selected variant. Both lateral types may
   repeat. Posture and physical speed follow that variant/stage; source phase must not delay release or require a
   displayed support-foot match. Idle remains externally supplied, not an ANIM-002 locomotion anchor.

## Technical Requirements

1. Wire a player-side intent owner through production controller input and the existing startup/content spawning route
   using `game/assets/characters/templates/reference_female/reference_female_player.tscn`. Install the driver for
   the reviewed set resolved by the character's [ANIM-003](../../animation/003-animation-catalogue/index.md)
   locomotion view (default `feminine`, binding `reviewed-feminine/set.json`), without a source activation switch or
   fallback movement catalogue.
   Trace that route rather than
   inferring missing startup from `empty.tscn`. Do not create another scene or adapt NPC destination navigation into
   stick control. Preserve NPC navigation and unrelated tracking, hands, gaze, and interaction functionality.
2. The [shared animation contract](../../animation/index.md) is normative for content, inference helpers, conversion,
   native retargeting (ANIM TR4), final-target realised-pose feedback with its unobservable-channel policy (ANIM TR5)
   and licensing.
   Reuse the in-process Godot/C# NVIDIA SOMA FP32 runtime. The
   [ANIM-002 source contract](../../animation/002-locomotive-animation-set/index.md) is normative for role/variant
   sources and dynamic sampling; ANIM-003 idle/external action targets follow its compatibility boundary. Apply
   [NAV-001 TR2](../../nav/001-npc-navigation/index.md#runtime-and-content) for shared model provisioning and path
   selection:
   exported default `res://models/motionbricks/onnx/mmm_v6_soma_fp32.onnx`, with neither `MOTIONBRICKS_MODEL` nor
   `LD_PRELOAD` required for ordinary launch on the verified host. This does not import NPC route/arrival policy.
3. Maintain one character-root movement owner, distinct from `PlayerVRIK`'s XR-origin authority. Outside player
    settling, realise generated planar travel through Godot's `MoveAndCollide` using the actual root collider. Follow
    the [ANIM TR4 root and scale rule](../../animation/index.md#technical-requirements) for once-applied planar demand,
    game-owned yaw and source-hips travel removal. Do this without bespoke predictive physics admission
    such as swept-body/clearance forecasts, support-floor proofs or `TestMove` preflights. External physics may block or
    limit realised movement; input targets do not guarantee physical clearance. Preserve actual collision handling and
     truthful blocked/collision diagnostics, not a physics-aware planner. Applying planned actor yaw under TR10
      does not replace generated footwork. No procedural turn, teleport, foot-IK rescue, authored-idle reset or
      corrective locomotion may substitute for it.
    During player settling, interpret generated planar hip motion as absolute local articulation in the release frame,
    not collider demand. The articulation follows the actor; do not pin or teleport the actor against external
    movement. Preserve ordinary collision handling and NPC movement policy.
4. Condition generation on actual realised player poses after applicable tracking and skeleton modifiers, following
   ANIM TR5's normal-scheduling observation boundary and timestamped context. Do not use authored idle, pre-modifier
    snapshots, source-skeleton samples or unplayed predictions as realised feedback. Validate the actual final-target
    boundary in production, after VRIK, hand tracking and the optical-last finger modifier.
    Generate the settle towards the selected idle stance from the evaluated player `AnimationTree`, before native
    retargeting and downstream modifiers. This destination is separate from the four realised history samples used
    for conditioning. Where an outgoing generated pose is displayed, copy its pre-modifier input separately for
    [presentation start correction](../../animation/index.md#technical-requirements), using the actual presented
    source root in the actor's frame rather than the outgoing travel demand. It replaces only the start argument
    of the existing endpoint pass after inference, not its destination or any realised history.
   The player `AnimationTree` keeps its physics-callback update beneath the native retarget stage. While generated
   motion owns the body, the player's `RetargetModifier3D` writes every mapped body bone including the wrist, and the
   controller owns root motion; tree root motion is unused. The player profile never writes hand descendants while
   actual fingers are observed: [INTR-003](../../interaction/003-hands/index.md) authored poses or
   [XR-002](../../xr/002-optical-hand-tracking/index.md) optical tracking keep them. Eyes and other unmapped layers
   stay live. Entering tracked idle is an explicit ownership transition that stops native body writes; holding the
   source on its last sample does not relinquish ownership and is not tracked idle. Stick release and generated
   settling towards idle are not themselves tracked-idle entry. Entry after generated motion follows this order:
   record the settling segment's final visible target pose at `skeleton_updated` and retain it in conditioning
   history; on the next physics tick, if both sticks are within their neutral deadzones and no replacement movement
   or turn request is queued or in flight, disable the native retarget modifier. Otherwise ownership stays with
   generated motion.
   Reactivate the modifier only when applying a new generated pose, not on input or request submission.
   The native retarget modifier starts inactive, so the player is in tracked idle before the first generated pose;
   it activates only when that pose is applied. In tracked idle, the player shows the tree's own posture: the IK-004
   standing/crouching continuum at standing head height, with no idle or locomotion loop.
5. Current input owns movement/turn requests. Movement/stationary-turn release and incompatible trajectory changes
   invalidate superseded queued/in-flight intent; reject stale results and start the appropriate transition without
    waiting for a whole stale segment, using realised context. Fresh movement or turn input retires the active or
    pending settle immediately. Hold the outgoing stance until the replacement applies and preserve it at the first
    replacement pose using TR4's start correction and the held actor-local planar hip reference. Apply replacement
    travel through collision once from the realised submission anchor, not from that local reference. Carry the
    reference through ensuing travel; the next settle replaces it rather than adding another offset, and tracked-idle
    entry clears it. TR9 retains compatible walking work across small direction changes. Separately, while stationary
    turning owns the body (unchanged role, zero old/new movement, no settling), a finite same-direction magnitude
    change above deadzone preserves the active segment and any pending request. Their ownership epoch, tickets,
    source phase, play time, targets and submitted magnitudes remain immutable. Use the latest magnitude when
   allocating the next uncommitted cycle under TR10, without requiring another input event.
   Re-centring, reversal, or a movement/role change retires the incompatible
   turn immediately; historical tickets and late returns cannot confer or revive ownership. Adapt only scheduling
   needed for responsiveness.
   Preserve copied requests,
   scene-thread submission/consumption, shared native ownership and disposal under
   [CORE-011](../../core/011-shared-model-services/index.md), and graceful application-exit draining under the
   [shared worker contract](../../nav/001-npc-navigation/index.md#outcome-and-worker-contract).
   Those worker guarantees do not impose NPC destination outcomes. Report unavailable setup/inference explicitly;
   preserve a safe pose without concealing it through fallback locomotion.
6. Preserve `PlayerVRIK` head-target, post-solve and XR-origin ownership, body/view separation and the
   unconstrained/reachable compensation skip. The default yaw divergence limit is 100 degrees per side and
   configurable. Within the limit, skip compensation when the
   tracked head is unconstrained and reachable; retain existing constrained position/orientation compensation for
   positional/anatomical constraints and beyond the limit. Preservation of wall-related behaviour does not require
   adding physical head collision: [RIG-001](../../rigging/001-physical-collision/index.md) excludes head collision,
   and a wall alone need not constrain a shapeless head target. Existing positional constraints and head-limit
   compensation remain required. Do not conceal the known hand-origin/epoch issue,
   or introduce new hands, tracking, gaze, or interaction regressions. The
   [XR runtime boundary](../../xr/001-xr-manager/index.md) and
   [player IK integration](../../ik/implementation-notes.md#player-xr-integration-layer) remain normative dependencies.
7. Begin verification with representative end-to-end production input and tracked posing, using focused automated
   checks and full temporal observation rather than exhaustive infrastructure. Record input-to-response timing and
   inference latency diagnostically, without invented millisecond or inherited motion-quality gates. Apply the
   integration-testing and visual-verification guidance; retain unrelated coverage and report known broad-suite
   failures honestly rather than repairing them in this slice.
8. Before playable handoff, run `dotnet format --verify-no-changes AlleyCat.sln` and
   `dotnet build AlleyCat.sln -warnaserror`. Obtain independent code/spec and selected visual-evidence review, repair
   admitted blockers, and re-review. Deliver launch/setup, mapping, evidence, limitations, review disposition, and
   headset checklist. Escalate material scope changes, tracking/interaction regressions, or model limitations that
   prevent a usable probe rather than adding corrective machinery.
9. **Source-Guided Travel:** [Player Motion](../../../game/src/Control/PLAYER-MOTION.md) documents normal launch,
   mapping and independent diagnostics. The shared source controller follows these placement/chaining rules;
   tuning defaults do not replace variant metadata or prescribe source phase:
   - Use finite, magnitude-bounded, unquantised left input after the existing deadzone. Capture actor yaw at walking
     bout start and retain it across magnitude/direction changes until release. Left input controls travel independently
     of body heading, without extrapolating generated yaw or automatically facing travel velocity. Preserve processed
     right input while walking and accumulate a steering offset over real elapsed time relative to the stable bout
     frame. Apply it coherently to requested travel and heading. Expose a separate tunable walking angular-rate
      mapping, separate from TR10 stationary-turn conditioning. Right release stops accumulation and retains the
      offset until the bout ends; a new bout uses existing reference acquisition. Do not procedurally rotate actor/view.
   - **Backward Requested-Speed Ratio:** expose `PlayerMotionBricks.BackwardSpeedRatio` on the existing normal
     player `MotionBricks` Inspector surface and copy it into the driver at initialisation; restart to apply edits.
     The current default/fallback is 0.75, not a universal tuning requirement or quality bound. Accept
     finite `(0,1]`; clamp finite values above 1 to 1 and use the fallback for zero, negative or nonfinite values.
     There is no hidden positive minimum; the Inspector's 0.01 step is not a runtime limit. Ratio 1 restores the prior
     requested mapping; zero is not a stop command. After existing stick normalisation, multiply only negative left Y
     by the ratio, before bout/course/steering rotation. Leave X and positive Y unchanged and do not renormalise.
      Select the variant's forward-reference speed stage using the original magnitude, not a reduced-magnitude slower
      posture. Use ANIM-002 physical reference metadata, not the velocity of one fixed source window.
     Use this one adjusted desired velocity for near/far prediction, terminal velocity, all four guide roots and
     trajectory compatibility. This ratio governs player requests, not NPC navigation.
   - In common horizontal source coordinates, let `p` be the latest actual ground-root position, `v` its measured
      collision-realised velocity, and `u` stick-requested velocity in the steered frame, scaled by the selected
      variant/stage's physical reference speed. Reference/cycle speed is distinct from sampled phase-local velocity.
     Set `h = nextComparisonTime - lastActualObservationTime`; advance a due deadline before prediction, otherwise
     retain it. Include observation age. Predict curved requested velocity under held steering over both positive `h`
     and the separate generation horizon `H`, conditioning on actual `v`. Endpoint heading, terminal velocity and all
      target placement samples must describe the same trajectory, not a position-only or heading-only update.
     With zero steering, reduce to the existing linear transition: `p_cycle = p + (v + u) * h / 2`.
     This predicts a target, not procedural actor acceleration or realised feedback.
   - Integrate the generation guide from the same actual observation. With zero steering and `H >= h`, use
     `p_guide = p_cycle + u * (H - h)` with terminal velocity `u`. With zero steering and `H < h`, use
     `p_guide = p + v * H + (u - v) * H² / (2 * h)` with terminal velocity `v + (u - v) * H / h`.
      With steering, placement roots/headings follow the same curved trajectory. ANIM-002 dynamically sampled source
      windows supply articulated cues and phase-local root variation around that placement; do not overwrite an uneven
      source cycle with constant-velocity terminal samples. Retain authored heights and ordered poses. Do not stretch
      the velocity transition to `H`, restore radial placement or integrate an unbounded destination. The near-cycle
      prediction need not equal the native generation target, and neither horizon owns intended source phase.
   - Periodically compare current generation-guide position/heading with the **last actual submitted guide**, not the
     near-cycle marker or previous snapshot, so gradual changes accumulate. Retain useful work for compatible changes;
     keep only the latest desired value for the next eligible comparison or normal chaining submission.
   - **Stable-Input Chaining:** suppress snapshot resubmission for unchanged processed movement/turn input since
     the last actual submission when turn is zero. Keep input sampling and virtual-target/marker updates live.
     Latch accepted changes until actual submission, including change-and-return; nonzero steering retains existing
     snapshot eligibility. Preserve the existing early 10-frame chaining path, immediate release/sharp retirement,
     stale-result rejection, actual post-modifier feedback, collision-root ownership, and native shutdown guarantees.
   - Apply sharp-change replacement to materially incompatible trajectories relative to active/submitted work, not
     every right-stick callback. Movement release bypasses sampling and initiates settlement; retained steering must
     not revive released movement. Right release alone retains the course. Reject obsolete results arriving late.
     Retained compatible work is not permission to consume superseded work. Actual realised feedback, collision-root
     ownership, and worker guarantees in TR3–5 remain mandatory; virtual targets are not realised context.
   - Use fixed `H = 49/30 s`, selected from observed native durations, not a
     universal horizon requirement or arrival-time promise. Native duration remains model-selected and variable.
     Tunable defaults remain a 0.2 s comparison interval, 0.08 m position difference, 10-degree heading difference,
     and 45-degree sharp-change boundary. These are tuning settings, not quality bounds or timing gates;
     exact realised speed and comfort remain unproved. Stationary turning follows TR5's same-direction retention
     policy; do not apply walking's 10/45-degree thresholds to stationary magnitude compatibility.
   - Optional diagnostics must distinguish VIRTUAL near-cycle prediction, immutable last SUBMITTED generation guide
     with request identity, and ACTIVE displayed-playback guide with identity/state, including held completion.
     Targets are not achieved motion; remove retired movement markers on release/sharp supersession.
     Stationary-turn recordings must distinguish latest requested magnitude from immutable submitted and actually
     adopted magnitudes with their tickets and live pending/active/held state. Retained work must not be relabelled
     with the latest magnitude; historical receipts are not live ownership.
10. **Conditioning Controller:** implement the normative [ANIM-002
    lifecycle](../../animation/002-locomotive-animation-set/index.md#technical-requirements) on the game side. Own
    variant, state, role, virtual source phase, expected source foot phase and future consecutive-window selection.
    Resolve roles from player movement/turn and interaction-positioning intent; expose how non-crossing sidestepping is
    distinguished from crossing lateral travel without inventing an additional binding in this specification. For
    travel, use variant/stage physical metadata with input magnitude, placement horizon and the backward ratio; never
    treat a crawling/limping source as a walking-speed replacement. Apply ANIM-002 advance, loop, sample-ahead,
    role/stage/variant switch and overlapping-request rules. Snapshot source selection with request identity alongside
    actual realised context; compatible retention must not let a late result own phase progression. Release or
    interruption abandons old intended progression immediately and requests compatible ANIM-003 idle/external action
    targets or the new role without waiting for source-cycle completion. Missing/incompatible sources or targets are
    explicit availability failures under TR5, not permission for procedural fallback or authored-idle reset. Source foot
    phase is intended information only; no inferred displayed-foot matching, leading-foot variants or arc/transition
    clips are prerequisites. Keep existing input responsiveness, steering, tracking, collision and worker authority.
    Apply the stationary-turn allocation and scheduling policy below through the shared ANIM-002 mapping.

### Stationary-Turn Conditioning

The ANIM-002 [stationary-turn angular mapping][turn-mapping] is normative for TR10. CTRL owns the stick-driven
allocation and interruption policy, not a separate angular mapping. Apply the shared [actor-facing rule][turn-facing]:
the game sets actor facing from planned cycle yaw, independently of generated articulation.

[turn-mapping]: ../../animation/002-locomotive-animation-set/index.md#stationary-turn-angular-mapping
[turn-facing]: ../../animation/002-locomotive-animation-set/index.md#actor-facing-during-stationary-turns

- **Allocation:** select the directional source from the stick sign and allocate `abs(stick) × 120 degrees` per
  source cycle using the processed magnitude after the existing deadzone. Anchor a new turn at actual actor-root
  yaw and repeat while held. Apply the latest same-direction magnitude at the next uncommitted cycle, retaining
  active and pending ownership under TR5. Magnitude changes arc size, not phase advance or cycle duration.
- **Scheduling:** use the source-paced cycle and mapped lift cues. Request granularity must not slow the turn below
  source pacing or split a cycle into visibly separate small turns. Do not require a separate request per landmark
  or additional recovery endpoints. Record planned and displayed cycle timings separately from returned model duration.
- **Same-Owner Successor Presentation:** when a follow-on result takes over within the same stationary turn, start
  playback from the last pose supplied in world space before modifiers and from the current actor heading. Blend
  into the incoming motion without changing its endpoint or rotating the hips to match actor heading. This starting
  pose is for presentation only: worker context still comes from actual post-modifier observations under TR4.
  Measure planar movement from the realised root at submission and apply it through collision under TR3. This
  rule changes neither turn gain nor walking splice eligibility.
- **Release-to-Settling Facing:** keep actor facing continuous across the handoff from a released stationary turn
  to settling. Do not snap the actor to generated hip heading. Keep the residual hip-to-actor yaw in the skeleton
  and apply actor yaw only once. This does not prescribe how settling yaw is computed or change walking facing.
- **Ownership:** snapshot the cycle allocation, stick magnitude and mapping with each request. Same-direction
  retention does not permit stale results to regain ownership. Re-centring abandons progression immediately; reversal
  or a movement/role change retires the turn immediately and starts the new intent from fresh realised context.
  Preserve existing positional placement, `CorrectEndpoints`, VRIK/XR, `MoveAndCollide`, worker disposal and the
  tracked-idle entry rule in TR4.

## In Scope

- Existing-route player integration, stick movement and stationary body turning, responsive release and replacement.
- Live tracking, body/view separation, collision/root and XR-origin ownership, realised feedback, safe worker lifetime.
- Focused verification, temporal diagnostics, independent review, and headset playtest handoff.
- Normal source-guided travel with continuous steering, held chaining and truthful diagnostics.
- Game-owned role/phase control, dynamic source sampling and ANIM-003 idle/external action integration under ANIM-002.

## Out Of Scope

- Simultaneous movement plus independent stationary turning, snap turns, a general action/idle framework,
  terrain/contact adaptation and broad rig/hardware support. ANIM-002 role coverage and generic variant/stage support
   are required; normal feminine source adoption does not waive broader stage/default content or validation.
- NPC pauses/small-step quality, physical foot sliding, and wrist refinement remain deferred unless a concrete issue
   blocks this slice's core outcome. Preserve ANIM TR4 finger ownership; do not claim full hand fidelity.
- Multi-character performance, model precision comparisons, exhaustive tests, and broad-suite repair.
- Physics-aware planning, bespoke predictive physical admission, and new stall detection/recovery or rerouting.
  Actual engine collision handling and realised feedback remain required.

## Acceptance Criteria

### User Requirement Acceptance

1. **CTRL-UAC-001 (UR1–3, UR5):** Full temporal evidence in Godot native playback through production input shows
   current tracked idle without held generated posture, observed fingers kept by their owner, movement
   start, differing stick magnitudes, direction changes and release, stationary turns both ways and turn release,
   and movement priority. Inspect the whole window, including inference holds and settling; stale intent must not
   finish after release. Show generated footwork, not only root displacement or isolated favourable frames.
    Show the return to tracked idle after settling with both sticks neutral, and that releasing one stick while the
    other is held or a replacement is pending does not return the body to tracking.
    Observe a settle towards the selected idle stance without generated planar collider travel. Interrupt an active
    settle and inspect stance continuity at the first replacement. Keep manual headset snap and foot-contact
    judgement separate from synthetic boundary continuity.
   After independent review, observe a fresh manual turn in each direction from pre-input to settled idle.
   Check continuous playback across same-owner handoffs and no visible facing snap when release hands off to settling.
   Turning about 180 degrees with full stick then re-centring should read as one large turn plus a smaller
   finishing portion of the next cycle, not several small turns. Verify proportionally smaller cycles at partial
   stick, no cancellation or restart on same-direction strength changes, source-like pace and no sustained
   counter-rotation or hip-driven reversal of actor facing. Inspect ordered footwork where source cues are present.
   Playback continuity and reached landmarks alone do not establish coordinated stepping or acceptable pace.
2. **CTRL-UAC-002 (UR4–5):** Observe ordinary body turning with an unconstrained, reachable tracked view inside the
   divergence limit, and existing constrained-head position/orientation compensation under positional/anatomical
   constraints and beyond the limit on both sides. Distinguish head translation with/without a wall from an exercised
   existing positional constraint; head-to-wall collision is not an acceptance gate. Confirm live head motion and
   preserved unrelated XR behaviour without a new body-stop policy, unrestricted view-locking claim, or universal
   VR correctness claim.
3. **CTRL-UAC-003 (UR6):** Provide the playable candidate, actual mapping, normal launch instructions, limitations,
   and headset checklist for user evaluation. Report synthetic verification separately from pending headset judgement.
4. **CTRL-UAC-004 (UR7):** Compare complete representative windows of partial-stick noise, gradual magnitude change,
   sharp direction change, and release/settling. Observe actual progress along requested forward/lateral/backward travel
   without left-requested heading changes, preserved right-only turning/movement priority, and continued useful walking
   under minor fluctuations. Distinguish cycle predictions from submitted/active guides when diagnostics are enabled.
   For steering, compare the same full production-input sequence before/after the change: straight walking, right
   steering, right release while walking, left steering, movement release and visible settled idle. Show corresponding
   realised course changes, stronger-input faster intended steering, and retained course on right release, separately
   from correct predictor values. Include inference holds and settling; input-state flags alone do not prove steering.
   Inspect moving marker readability, not only recorded target equality.
   Synthetic progress does not close headset usability, settling quality, or marker-presentation follow-ups.
   For held-input chaining, present full matched windows including inference holds,
   release and settled observation. Keep forward awkwardness/sway and headset judgement open: a synthetic speed
   comparison is neither headset symptom resolution, a numerical quality gate, nor evidence of general steering benefit.
   For backward-ratio adjustment, compare matched forward/backward input and full release/settling windows, separating
   requested mapping from realised motion. Present the Inspector setting and ratio-1 restoration for user evaluation;
   backward comfort and separate unresolved sway still require headset judgement, not an exact actual-speed ratio.
5. **CTRL-UAC-005 (UR8):** Demonstrate the distinct roles, including repeatable non-crossing fine positioning versus
   crossing lateral travel, and variant-appropriate speed/posture. Include switching, interruption and externally
   supplied idle without waiting for displayed foot-phase agreement. User judgement of motion remains separate from
   successful source selection or a stationary cue-transfer probe.

### Technical Requirement Acceptance

1. **CTRL-TAC-001 (TR1–2):** The existing startup/player composition launches with the local default model on the
   verified host and accepts both sticks through actual production input ownership. Verify shared runtime/content
   dependencies without a new scene, NPC destination adapter, or required environment override.
2. **CTRL-TAC-002 (TR3–6):** Focused checks and normal-scene traces demonstrate singular collision-root ownership,
   separate XR-origin ownership, submitted context from actual post-modifier observations, stale-result rejection on
   release/replacement, and safe worker shutdown/disposal. Verify the idle player's tree still evaluates hand-pose
   layers beneath the native retarget stage without moving the root. With controller-owned and optically tracked
    fingers, verify the final digits match their owner while the wrist follows generated motion, and that submitted
    context carries the collision-realised root (travel applied once) and final-target poses. Verify that entering
    tracked idle stops native body writes. Trace the evaluated tree destination, realised conditioning history and
    separate outgoing presentation input across release; verify it changes only the endpoint pass's start argument,
    not the inference inputs or destination. Verify no generated planar collider demand during settling and no root
    pinning against external motion. At interruption, verify immediate retirement and stance retention, with
    replacement travel applied once from the realised anchor. Check that the next settle replaces the local reference
    and idle clears it. Verify that after tracked-idle entry no held source sample influences the wrist or spine.
    Demonstrate two successive generated-motion → settling → tracked-idle transitions: the final settling observation
    precedes relinquishment, the native modifier stays inactive through idle and request submission, and only applying
   the next generated pose reactivates it. Verify that a non-neutral stick or a pending replacement prevents entry.
   Verify the 100-degree-per-side default,
   configurability, unconstrained/reachable compensation skip inside the configured limit, and retained compensation
   outside it on both sides and under existing positional/anatomical constraints. Fixture expectations must respect
   RIG-001's head-collision exclusion; a wall-crossing assertion alone does not establish a product regression.
   Review hands and tracking preservation, including the known hand-origin/epoch risk; introduce no arbitrary
   numerical evidence tolerances. Manual diagnostic scheduling or
    predicted context does not satisfy realised-feedback acceptance. Source review confirms actual-root `MoveAndCollide`
    without bespoke predictive physics admission; retain actual collision handling and truthful blocked/collision
    diagnostics without requiring a substitute forecast or new stall detector/recovery.
3. **CTRL-TAC-003 (TR7–8):** Record full representative temporal evidence, timing diagnostics, focused check results,
   format/build outcomes, and independent review disposition. Repair admitted blockers and re-review; disclose
   remaining evidence gaps and unrelated failures. A usable probe is not complete locomotion or a comfort guarantee.
4. **CTRL-TAC-004 (TR9, TR3–5):** Verify normal source-guided installation, bounded targets and stable bout reference,
   independent left travel/heading, measured/requested velocity and deadline arithmetic, both horizon branches,
   and curved target placement with ordered phase-local source motion, without substituting predictions for feedback.
   Use independently derived checks
   for steering signs, accumulated course, elapsed-time consistency and zero-steering reduction to the straight
   predictor. Check right-input propagation, retained offset on right release, and new-bout reference acquisition.
   Preserve the source-bound stationary-turn mapping in TR10.
    Verify distinct cycle/guide markers,
   last-submitted comparisons, gradual changes reaching actual submissions, and retained useful pending work under
   small changes. Exercise immediate movement-release/sharp retirement and actual late-result rejection. Verify
   request/marker identity against displayed playback, actual post-modifier feedback, and collision-realised movement.
   Record full
    temporal behaviour and diagnostic timings without turning tuning defaults or measured progress into gates.
   For stable-input chaining, verify normal production startup without activation variables. Check unchanged-input
   suppression, latched change-and-return, nonzero steering eligibility and preservation of early chaining/release/sharp
   safety. Prior full-window evidence may be reused only with demonstrated executable algorithm equivalence and
   preserved evidence; independent review of that equivalence, startup wiring and evidence is required before
   headset handoff. Wiring checks alone do not establish the parent outcome.
   For backward ratio, verify the normal exported Inspector setting reaches production driver initialisation with
   normal held chaining, including configured values, validation/fallback and ratio-1 restoration. Check
   backward-only scaling after normalisation and before rotation, unchanged forward/lateral components, diagonals
   without renormalisation, and original-magnitude stage selection. Independently verify the adjusted request in both
    predictors, placement velocity, dynamically sampled target windows and compatibility. Retain release/stale
    rejection,
    actual post-modifier feedback, collision and worker ownership. NPC policy isolation remains required;
    measured reduction alone does not establish comfort or authorise quality thresholds.
   For TR5's stationary retention, exercise same-direction magnitude changes across partial magnitudes above
   deadzone, increasing and decreasing in both turn directions, not only near-full values, with genuine active and
   pending owners. Verify epoch/ticket, source-phase and play-time continuity, immutable targets and submitted
   magnitudes, and latest-magnitude use at the next uncommitted cycle without another input event. Check each live
   owner, immediate re-centre/release, reversal and movement/role retirement, non-resurrection and late-return
   rejection. Verify requested/submitted/adopted joins and unchanged-role/zero-movement/no-settling eligibility.
   Synthetic lifecycle checks establish ownership behaviour, not generated footwork or manual symptom resolution.
5. **CTRL-TAC-005 (TR10, TR2–5, TR9):** Apply ANIM-002 lifecycle/metadata acceptance to production player input.
   Trace selected role/stage, virtual phase, sample-ahead window and placement independently of actual post-modifier
   context and generated duration. Check phase advance/wrap, role/variant switches, interrupted idle/action handoff,
   compatible overlap and stale-result rejection without clock rewind. Verify input/horizon placement uses physical
    reference metadata while preserving phase-local motion; preserve all existing release, XR and worker guarantees.
   For stationary turns, verify CTRL uses the same mapping component as NAV under ANIM-002-TAC-001–002. Check
   120 degrees allocated per held full-stick cycle, proportional partial-stick allocation, the signed yaw
   scale, landmark placement from measured source yaw and unchanged non-root articulation and timing. Trace both
   directions across wraps and magnitude changes; only the next uncommitted cycle changes. Compare full displayed
   cycle duration with source duration, including request handoffs and inference holds; verify request granularity
   does not slow or fragment the turn. Retain TR5's same-direction retention, immediate retirement on release,
   reversal or role change, stale rejection and realised feedback. Apply ANIM-002-TAC-002's per-tick planned-yaw
   check, including hip independence, once-applied yaw, unchanged endpoint and actual post-modifier worker context.
   At a same-owner successor handoff, verify the first supplied world pose matches the preceding pre-modifier
   pose, actor heading is retained, and the blend preserves the incoming endpoint without aligning the hips to
   the actor. Check that worker context remains post-modifier and planar motion uses the realised submission
   root and collision handling. On release with differing hip and actor headings, compare per-tick actor-yaw
   changes across the release-to-settling boundary and verify no step attributable to hip heading within a
   declared, tunable angular tolerance. Check residual yaw stays in the skeleton and actor yaw is applied once.
   CTRL-UAC-001's actual-rig evidence remains required.

## Implementation Status

Normal player installation resolves its set through the ANIM-003 locomotion view (default `feminine`) and uses all
eight roles from `reviewed-feminine/set.json`, bound to
`landing-ablation-01.json`. Travel uses a stable input frame, source-rate placement, continuous steering,
backward ratio and held chaining. The [stationary-turn policy](#stationary-turn-conditioning), including planned-yaw
facing and release continuity, is in progress and not yet verified. The player plays no catalogue idle; tracked
idle stays with VRIK.
No source/controller activation flag or prototype movement fallback exists.
Migration to TR2/TR4 native presentation, final-target feedback and tracked-idle ownership is in progress and not
yet verified. TR5 same-direction stationary-turn retention is implemented with focused checks, pending independent
review and manual playtest. See [Player Motion](../../../game/src/Control/PLAYER-MOTION.md) and the
[shared source data](../../../game/data/motionbricks/reviewed-feminine/README.md).

Technical checks cover normal startup/source binding, all-role primary input, release/ownership and native-free
preservation checks, with clean format/build results. These are technical migration evidence; combined independent
review remains pending. No default-entrypoint GPU inference or fresh headset/visual quality acceptance was established
by those checks. The user's source stepping/foot-sliding mitigation is accepted with initial twitch deferred, not a
no-slip guarantee. Raw repetition quality, generated gait and settling toe motion/contact, turning pace and
headset/interaction suitability remain open. Synthetic release-boundary evidence does not resolve the settling pose snap
in the headset. Masculine/default content and previews, native-shutdown and hand-origin risks remain unresolved.
Normal-source code/spec alignment is updated; broader ANIM-002 completion remains deferred-with-risk. Plan steps 5/6
remain open. No new generation or model-call budget follows from this documentation alignment.

## References

- [Project Specifications](../../index.md)
- [Player Character Control](../index.md)
- [Shared Animation Contract](../../animation/index.md)
- [ANIM-002: Locomotive Conditioning Sources](../../animation/002-locomotive-animation-set/index.md)
- [NPC Navigation and Shared Runtime](../../nav/001-npc-navigation/index.md)
- [Runtime Setup and Evidence Limits](../../../game/src/Motion/README.md)
- [Character Root](../../character/002-character-root/index.md)
- [Visual Verification Scope](../../testing/002-visual-verification-scope/index.md)
