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
   stationary turning; during travel, right input steers walking under UR7. The mapping is documented and tunable;
   intensity-to-total-angle mapping, particular deadzones, and requested turn-angle limits are not prescribed.
   The head/body divergence limit in UR4
   is separate from this stick mapping.
3. Releasing movement or stationary-turn input immediately begins settling towards idle and discards remaining intent.
   Releasing walking steering alone instead retains the acquired course under UR7. Do not require the
   full queued motion segment or stale turn to finish, or add recovery-step sequences. This is not instant inference.
4. Tracked head motion stays live. Ordinary body turning must not simply rotate the view with the avatar. Introduce
   body/view separation with a tunable head/body yaw divergence limit defaulting to 100 degrees per side. This relaxed
   allowance accounts for physical hip turning that the avatar does not reproduce, avoiding premature disturbing
   compensation. Beyond the limit use existing XR-origin compensation, not a new body-stop policy. Retain existing
   positional/anatomical compensation within the existing physical scope in TR6; this is not an unconditional
   camera-lock or physical head-to-wall collision guarantee.
5. MotionBricks generates movement, turning footwork, and idle transitions on the calibrated reference Female rig.
   The player is not always animation-driven; current tracked and modified posing remains part of the experience.
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
   using `game/assets/characters/templates/reference_female/reference_female_player.tscn`. Install the shared
   `reviewed-feminine/set.json` driver normally, without a source activation switch or fallback movement catalogue.
   Trace that route rather than
   inferring missing startup from `empty.tscn`. Do not create another scene or adapt NPC destination navigation into
   stick control. Preserve NPC navigation and unrelated tracking, hands, gaze, and interaction functionality.
2. The [shared animation contract](../../animation/index.md) is normative for content, inference helpers, conversion,
   calibrated retargeting, source-proportioned feedback, licensing, and explicit missing-channel approximations.
   Reuse the in-process Godot/C# NVIDIA SOMA FP32 runtime. The
   [ANIM-002 source contract](../../animation/002-locomotive-animation-set/index.md) is normative for role/variant
   sources and dynamic sampling; external idle/action targets follow its compatibility boundary. Apply
   [NAV-001 TR2](../../nav/001-npc-navigation/index.md#runtime-and-content) for shared model provisioning and path
   selection:
   exported default `res://models/motionbricks/onnx/mmm_v6_soma_fp32.onnx`, with neither `MOTIONBRICKS_MODEL` nor
   `LD_PRELOAD` required for ordinary launch on the verified host. This does not import NPC route/arrival policy.
3. Maintain one character-root movement owner, distinct from `PlayerVRIK`'s XR-origin authority. Realise generated root
    motion through Godot's `MoveAndCollide` using the actual root collider, without bespoke predictive physics admission
    such as swept-body/clearance forecasts, support-floor proofs or `TestMove` preflights. External physics may block or
    limit realised movement; input targets do not guarantee physical clearance. Preserve actual collision handling and
    truthful blocked/collision diagnostics, not a physics-aware planner. No procedural turn, teleport, foot-IK rescue,
    authored-idle reset, or corrective locomotion may substitute for generated footwork.
4. Condition generation on actual realised player poses after applicable tracking and skeleton modifiers, following
   ANIM TR5's normal-scheduling observation boundary and timestamped context. Do not use authored idle, pre-modifier
   snapshots, or unplayed predictions as realised feedback. Validate the actual solved-skeleton boundary in production.
   The player `AnimationTree` keeps its physics-callback update beneath the MotionBricks stage. Once a generated pose
   exists, MotionBricks owns every mapped body bone and root motion; tree root motion is unused. Unmapped layers, such
   as [INTR-003](../../interaction/003-hands/index.md) hand poses and eyes, stay live, including while idle.
   Before the first generated pose, the player shows the tree's own posture: the IK-004 standing/crouching continuum
   at standing head height, with no idle or locomotion loop.
5. Current input owns movement/turn requests. Movement/stationary-turn release and incompatible trajectory changes
   invalidate superseded queued/in-flight intent; reject stale results and start the appropriate transition without
   waiting for a whole stale segment, using realised context. TR9 retains compatible walking
   work across small direction changes. Separately, stationary turning may retain live
   active/pending work only with unchanged role, zero old/new movement and no settling. Old/latest requested turn and
   each live submitted/active owner must be finite, same-sign and have exact processed absolute magnitude `0.95` or `1`.
   Preserve ownership epoch, tickets, source phase, play time and immutable targets; latch the latest requested
   magnitude for the next already-eligible submission without requiring another input change. Historical tickets
   cannot confer or revive ownership. Release, reversal, other magnitude changes and movement/role changes still
   retire immediately; `0.95→0.9` retires and `0.9→0.95` cannot resurrect old work. This grants no broader
   input compatibility. Adapt only scheduling needed for responsiveness.
    Under the ANIM-002 stationary-turn contract, intensity changes update prospective phase/facing intent without
    rewinding intended phase or changing immutable owned work. This does not broaden the compatibility exception or
    choose a new gain/replanning policy; release, reversal and material-change retirement remain mandatory.
    Preserve copied requests,
   scene-thread submission/consumption, worker-exclusive native ownership and disposal, and graceful application-exit
   draining under the [shared worker contract](../../nav/001-npc-navigation/index.md#outcome-and-worker-contract).
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
     exact realised speed and comfort remain unproved. Stationary turning retains TR5's bounded compatible-work
     policy; do not apply walking's 10/45-degree thresholds to stationary magnitude compatibility.
   - Optional diagnostics must distinguish VIRTUAL near-cycle prediction, immutable last SUBMITTED generation guide
     with request identity, and ACTIVE displayed-playback guide with identity/state, including held completion.
     Targets are not achieved motion; remove retired movement markers on release/sharp supersession.
     Stationary-turn recordings must distinguish latest requested magnitude from immutable submitted and actually
     adopted magnitudes with their tickets and live pending/active/held state. Retained work must not be relabelled
     with the latest magnitude; historical receipts are not live ownership.
10. **Conditioning Controller:** implement the normative
    [ANIM-002 lifecycle](../../animation/002-locomotive-animation-set/index.md#technical-requirements) on the game side.
    Own variant, state, role, virtual source phase, expected source foot phase and future consecutive-window selection.
    Resolve roles from player movement/turn and interaction-positioning intent; expose how non-crossing sidestepping
    is distinguished from crossing lateral travel without inventing an additional binding in this specification.
    Use variant/stage physical metadata with input magnitude and placement horizon, including the backward ratio;
    never treat a crawling/limping source as a walking-speed replacement. Apply ANIM-002 advance, loop, sample-ahead,
    role/stage/variant switch and overlapping-request rules. Snapshot source selection with request identity alongside
    actual realised context; compatible retention must not let a late result own phase progression. Release or
    interruption abandons old intended progression immediately and requests compatible external idle/action targets
    or the new role without waiting for source-cycle completion. Missing/incompatible sources or targets are explicit
    availability failures under TR5, not permission for procedural fallback or authored-idle reset. Source foot phase
     is intended information only; no inferred displayed-foot matching, leading-foot variants or arc/transition clips
     are prerequisites. Keep existing input responsiveness, steering, tracking, collision and worker authority.
     For stationary turns, use measured signed source angular span and ordered local progression with input intensity
     and chosen rotation to coordinate phase/window selection and target facing. Preserve both ordered foot-lift
     phases where present, including their asymmetric angular positions and cyclic boundaries; do not assume a single
     global lift or a time/angular midpoint. Independent fixed source pace ignoring angular intent is insufficient.
     Declare the intent-dependent phase policy separately from model-selected duration; neither four target poses
     nor metadata alone guarantees displayed mid-turn footwork. No angular-waypoint planner, fixed two-subrequest
      policy, one-episode-per-call rule or new native interface is prescribed by this requirement.
     The source-bound policy below governs normal player stationary turns without redefining NAV's finite turn plan.

### Stationary-Turn Conditioning

Normal player stationary turns use four source-bound lift/recovery landmarks, with no pace compensation.
Content repetition/recovery admission labels describe quality, not activation switches or an alternative controller.

- **Metadata and Clock:** load measured signed span, representative rate, source binding and both ordered primary
  foot cues from `reviewed-feminine/turn-angular-reference.json`. Validate finite values, source hash/bounds/timing,
  facing/measurement conventions, cue identities/order/angular associations and span/rate consistency. Toe annotations
  do not add required endpoints. Keep `primaryFootEvents` authoritative for both feet; additive experimental
  `recoveryTrial` binds four ordered full-pose landmarks to the same source hash, phase, angular association and
  measurement convention. Validate primary agreement, stable unique IDs, typed `primary_lift|recovery`, subject foot
  and four-original-pose recovery-window availability before the raw seam. Do not search for pose minima at runtime.
  The current shared source selects left 947→951→961→1001 and right 1103→1107→1120→1152.
  Read phases, angles and consecutive recovery windows from the source-bound metadata, not hard-coded time fractions.
  Preserve full source geometry, including the late authored negative increment. These are content bindings, not
  generic variant rules or sole/contact/support claims. Start source-relative phase `p=0`. Integrate
  `p += F * abs(input) * dt`, where `F=30 Hz`; intensity changes affect only future advance. Output duration,
  completion, actual yaw and displayed feet never advance or reset this clock. Retain `H=49/30 s` as planning horizon,
  not requested native duration. Use first-guide angular cap
  `B=abs(r) * abs(latest input) * H`, where `r` is the metadata representative angular rate.
- **Selection and Lag:** at a stationary submission opportunity, forecast `q=p+F*abs(latest input)*H`. Select the next
  unserved landmark or an interim phase before it, clipped by `floor(q)` and a nondecreasing submitted-phase floor,
  subject to the overtake exception below. Copy four consecutive original 30 Hz poses with raw transforms at wraps.
  Finish the current four-landmark group even when it lags `p`: the second primary lift does not end the group.
  Complete its final recovery before choosing `kNext=max(kCurrent+1,floor(p/P))`, where `P` is the source period in
  frames; only explicit passed-unplayed handling below may advance an overtaken landmark without completion.
  Drop only intervening whole unstarted cycles and their prospective net yaw; do not move the actor, rewind `p`
  or queue catch-up calls.
- **Target Geometry:** anchor placement at actual actor-root yaw, distinct from anatomical heading and intended phase.
  Use the preceding affine map to obtain remaining source-angle geometry. Uniformly scale only target root-yaw
  variation to meet `B`, preserving ordered uneven angular shape; keep non-root articulation, consecutive timing and
  existing positional placement unchanged. Record the scale and scaled local angular derivatives, distinct from
  authored non-root derivatives and clock rate. No generated-output scaling, time warp or procedural actor yaw is
  allowed. After matching-owner playback completion, rebase prospective geometry at actual root yaw with unit scale,
  without resetting phase or replaying scaled-away rotation. Retain normal stationary completion cadence, not walking's
  early splice. Only a landmark selected as first guide and completed through full playback by its matching owner is
  serviced; interims and appearance in guides two to four do not count. Capped completion still services that source
  event, without queueing unrealised yaw. Service receipts prove neither displayed lift/recovery nor whole gait/contact.
- **Overtaken Landmarks:** distinguish authored holds/micro-reversals from backward catch-up to an already overtaken
  forward lift or recovery. Mark the latter passed-unplayed, not serviced. If the forecast-clipped interim would also
  chase backwards after such a pass, select the next unpassed ordered landmark even beyond forecast, under the same
  cap, clock and immutable-owner rules. Bound the search to remaining landmarks in the current group plus the next
  four-event group selected by the cycle/drop rule (at most eight candidates). Exhaustion is an explicit availability
  failure, not arbitrary-cycle recovery, a new wait policy, silent re-anchoring or permission to rotate the actor.
  Variable model duration guarantees neither strict future-only selection, realised angular pace nor natural footwork.
- **Ownership and Input:** snapshot clock/forecast/selected phase, source indices, stable event ID/kind/subject foot,
  magnitude, cap/map, context and owner identity immutably. Distinguish `IsLandmarkTarget` from `IsPrimaryLift`,
  nullable primary `Foot` from `RecoveringFoot`, and primary/recovery completions from separate passed-unplayed counts.
  Exact TR5 `0.95↔1` compatibility retains work; latest input controls the next eligible forecast/cap.
  Material same-role changes retire the owner immediately but preserve clock, service cursor, submitted
  floor and geometric lineage. A replacement uses fresh actual context; it cannot resurrect a retired owner or count
  a cancelled landmark as serviced. `0.90` remains material; release abandons progression immediately and reversal/role
  change starts the new role at entry with a fresh actual anchor. Recovery must delay neither. Preserve stale-result
  rejection, actual post-modifier context, worker disposal, `CorrectEndpoints`, normal VRIK/XR and generated-root
  `MoveAndCollide` ownership; targets are never realised feedback.
- **Uncompensated Pace:** extra endpoint connections may roughly halve turning pace. The illustrative alternating
  30/50-frame schedule increases group duration from 2.6 to 5.2 seconds; this is not measured native timing or a bound.
  Accept and measure the cost without gain increases, retiming, shortened clips, output repair or procedural yaw.
  Model duration remains variable; lower input does not guarantee linear realised speed. Four landmarks neither
  prescribe exactly four calls nor provide timed interior constraints or guarantee coordinated steps within a clip.

## In Scope

- Existing-route player integration, stick movement and stationary body turning, responsive release and replacement.
- Live tracking, body/view separation, collision/root and XR-origin ownership, realised feedback, safe worker lifetime.
- Focused verification, temporal diagnostics, independent review, and headset playtest handoff.
- Normal source-guided travel with continuous steering, held chaining and truthful diagnostics.
- Game-owned role/phase control, dynamic source sampling and external idle/action integration under ANIM-002.

## Out Of Scope

- Simultaneous movement plus independent stationary turning, snap turns, a general action/idle framework,
  terrain/contact adaptation and broad rig/hardware support. ANIM-002 role coverage and generic variant/stage support
   are required; normal feminine source adoption does not waive broader stage/default content or validation.
- NPC pauses/small-step quality, physical foot sliding, and wrist refinement remain deferred unless a concrete issue
   blocks this slice's core outcome. Preserve current calibrated approximations; do not claim full hand fidelity.
- Multi-character performance, model precision comparisons, exhaustive tests, and broad-suite repair.
- Physics-aware planning, bespoke predictive physical admission, and new stall detection/recovery or rerouting.
  Actual engine collision handling and realised feedback remain required.

## Acceptance Criteria

### User Requirement Acceptance

1. **CTRL-UAC-001 (UR1–3, UR5):** Full temporal evidence through production input shows current tracked idle, movement
   start, differing stick magnitudes, direction changes and release, stationary turns both ways and turn release,
   and movement priority. Inspect the whole window, including inference holds and settling; stale intent must not
   finish after release. Show generated footwork, not only root displacement or isolated favourable frames.
   For compatible stationary-turn retention, obtain a fresh manual full-turn observation after independent review,
   including onset, the full held turn, release and settled tail. Playback continuity alone does not close slow-turn
    or minimal-footwork outcomes; preserve the user's whole-window stepping-footwork judgement.
   Include intensity changes and observe ordered left/right footwork across the full turn where source cues are
    present, not only a terminal lift. Correct metadata, intended timing or four-pose targets do not prove cue transfer.
   For stationary-turn conditioning, retain full pre-input/hold/intensity-change/release/settled observation both ways;
   the user must judge consistent feet matching body turning and acceptable pace, not merely reached landmarks.
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
   layers beneath the MotionBricks stage without moving the root. Verify the 100-degree-per-side default,
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
   For TR5's stationary exception, exercise same-sign exact processed `0.95↔1` changes in both directions with genuine
   active and pending owners. Verify epoch/ticket, source-phase and play-time continuity, immutable targets and
   latest-magnitude use at the next already-eligible submission without another input change. Check each live owner,
   immediate release/reversal/incompatible-magnitude and movement/role retirement, non-resurrection and late-result
   rejection. Verify requested/submitted/adopted joins and unchanged-role/zero-movement/no-settling eligibility.
   Synthetic lifecycle checks establish ownership behaviour, not generated footwork or manual symptom resolution.
5. **CTRL-TAC-005 (TR10, TR2–5, TR9):** Apply ANIM-002 lifecycle/metadata acceptance to production player input.
   Trace selected role/stage, virtual phase, sample-ahead window and placement independently of actual post-modifier
   context and generated duration. Check phase advance/wrap, role/variant switches, interrupted idle/action handoff,
   compatible overlap and stale-result rejection without clock rewind. Verify input/horizon placement uses physical
    reference metadata while preserving phase-local motion; preserve all existing release, XR and worker guarantees.
   For stationary turns, validate the published angular/per-foot metadata against actual ordered sources under
   ANIM-002, and trace intensity, chosen rotation, intended phase, target facing and immutable submitted/adopted owners.
   Exercise intensity changes, both lift phases and wraps without phase rewind or target mutation; preserve TR5's
   immediate release/reversal/material-change retirement and actual realised feedback. Full actual-rig turn/change/
    release/settled evidence under CTRL-UAC-001 remains required independently of these technical checks.
   Focused checks cover entry/clock and metadata-rate caps, both full four-landmark groups and the primary-lift
    subsequence with intervening recoveries, source-derived angles/full poses, typed first-guide-only service versus
    later-guide appearance, and interim/lagging selection. Preserve raw wraps and uniformly scaled root-yaw derivatives
    with unchanged non-root poses. Verify separate primary/recovery completion and pass counters, immutable compatible
    ownership/latest-next input, material lineage and stale completion rejection. Check capped recovery service without
    queued unrealised yaw and final-recovery completion before whole-cycle drops without skipped yaw or clock/root
    movement. Exercise the bounded eight-candidate beyond-forecast exception separately from authored reversal and
    expose search exhaustion explicitly. Recoveries must not delay release/reversal; no pace compensation is permitted.
   Synthetic endpoints establish component mechanics only, not generated footwork, realised pace or parent acceptance.

## Implementation Status

Normal player installation uses all eight roles from `reviewed-feminine/set.json`, bound to
`landing-ablation-01.json`, and the [stationary-turn policy](#stationary-turn-conditioning) above. Travel uses a stable
input frame, source-rate placement, continuous steering, backward ratio and held chaining. Stationary turns use the
current source's four typed landmarks, intensity-dependent phase, immutable owners and bounded overtake/group-drop
policy. Idle remains externally supplied. No source/controller activation flag or prototype movement fallback exists.
See [Player Motion](../../../game/src/Control/PLAYER-MOTION.md) and the
[shared source data](../../../game/data/motionbricks/reviewed-feminine/README.md).

The [adoption report](../../../game/temp/motionbricks/anim002-regenerated-source-adoption-01/REPORT.md) records normal
startup/source binding, all-role primary input, release/ownership, turn groups and native-free preservation checks,
with clean format/build results. These are technical migration evidence; combined independent review remains pending.
No default-entrypoint GPU inference or fresh headset/visual quality acceptance was established by those checks.
The user's source stepping/foot-sliding mitigation is accepted with initial twitch deferred, not a no-slip guarantee.
Raw repetition quality, generated gait/contact, turning pace, settled release and headset/interaction suitability
remain open. Masculine/default content and previews, native-shutdown and hand-origin risks remain unresolved.
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
