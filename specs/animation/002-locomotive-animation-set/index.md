---
id: ANIM-002
title: Locomotive Conditioning Sources
---

# Locomotive Conditioning Sources

## Requirement

Provide locomotion sources and semantic metadata for game-owned virtual source playback and dynamic MotionBricks
conditioning. This is the normative source/phase contract for [CTRL-001](../../ctrl/001-locomotion/index.md) and
[NAV-001](../../nav/001-npc-navigation/index.md), consumed by content authors, implementers and reviewers.

## Goal

Let the game select meaningful future motion cues while MotionBricks connects actual realised poses to those targets.
Preserve source motion information without restoring procedural clip stitching or treating generated motion as the
owner of intended state progression.

## User Requirements

1. Locomotion distinguishes forward and backward travel, left/right non-crossing sidesteps, left/right crossing
   lateral travel, and left/right stationary turns. Non-crossing sidesteps support small interaction-position
   adjustments; they are repeatable movement, not idle-to-movement transitions. Crossing lateral travel may also repeat.
2. Variants such as walking, crawling and limping use the same one-or-more-speed-stage structure. Their physical speed
   and posture inform movement; crawling must not inherit walking speed merely because input magnitude is identical.
3. Source sampling follows intended role and phase, including uneven motion, without waiting for displayed feet to
   match a source phase. Input/route changes and release remain responsive; intended source phase is not a promise of
   identical generated foot placement or timing. Stationary turns retain the source's pacing and ordered left/right
   lift cues as the turn arc widens or narrows. Wider turns read as larger turns, not as clusters of small turns.
   Actor facing progresses in the commanded direction without being reversed by generated hip motion.
4. Locomotion enters and leaves compatible idle targets supplied by the
   [ANIM-003](../003-animation-catalogue/index.md) catalogue and compatible externally supplied action targets. Each
   source variant owns a reference posture for authoring, not a matching runtime idle asset or a requirement to pass
   through idle during movement or state changes.

## Technical Requirements

1. **Semantic Source Metadata:** identify variant/posture, movement role, speed stage and source identity/provenance.
   Declare usable time ranges, sample timing/rate, root units and coordinate axes, travel direction separately from body
   facing, loopability and cyclic boundaries, and admissible phase initialisation/transfer information. Describe
   expected source foot phases where applicable, sufficient to interpret the ordered source cycle; these describe source
   intent, not inferred contact in the displayed character. For stationary turns, identify each foot's lift landmarks
   with source phase, associated source angular progression and their order, including both feet where present. Declare
   whether measurements use foot/toe joint origins or a defined clearance convention; joint-origin peaks do not
   establish sole clearance, support or contact. Do not collapse two distinct foot cues into one global maximum or an
   artificial midpoint. No unique highest point or automatic peak-selection algorithm is required. If a required cue is
   absent, declare the source unsuitable for that coverage or record the missing validation follow-up; do not fabricate
   another cue. Declare unsupported roles or missing inputs explicitly rather than silently relabelling crossing travel
   as non-crossing sidestepping. CTRL/NAV load the reviewed set resolved by the character's ANIM-003 locomotion view,
   without controller or source activation flags or a prototype movement fallback. The default `feminine` entry binds
   `assets/animations/packs/default/locomotion/feminine/set.json` under `res://`. Bind all eight roles,
   angular metadata and approved source identity together; unavailable or inconsistent content fails explicitly.
   ANIM-003 entries reference sets in this format without extending it.
2. **Physical Speed Metadata:** each moving variant/stage/role supplies a physical reference velocity or speed with
   direction and the representative interval/cycle used to establish it. Keep this distinct from phase-local root
   velocity and turning information available from ordered samples. Uneven local velocity, such as a limp, must not
   become the sole full-speed reference merely because a selected window is slow. Controllers combine input or route
   speed, variant/stage metadata and target horizon for placement; normalised input alone is not physical speed.
   Stationary-turn metadata must publish measured signed reference angular displacement over an identified usable
   episode/cycle, its source bounds and duration, coordinates and facing/sign convention, and representative angular
   rate. Distinguish net displacement and its representative rate from ordered phase-local angular progression,
   including holds, overshoot and reversals; forward angular variation is not net displacement. Raw source yaw need
   not be invertible as a phase clock. Nominal authoring angle is not runtime physical truth: no fixed 45-, 60- or
   90-degree source span is mandated. Do not invent a positive translational speed for stationary turns.
3. **Dynamic Windows:** retain source motion across usable ranges, not only one fixed characteristic window per role.
   Sample consecutive short windows at a future virtual phase, preserving source ordering, local root changes and
   articulated pose cues. The current SOMA interface consumes four consecutive frames at 30 Hz; sampling/conversion
   must respect source timing and this interface. Fixed entry/exit indices alone do not satisfy dynamic sampling.
   Transform source-relative windows into controller-selected target placement/facing without flattening uneven
   local motion into identical poses or constant-speed samples. No automatic keyframe-selection algorithm is mandated.
   Stationary turns use the [shared angular mapping](#stationary-turn-angular-mapping) below.
4. **Game-Owned Lifecycle:** the game controller owns variant, state, role, virtual source clock/phase, expected source
   foot phase and target selection. MotionBricks owns connecting generation, not semantic phase progression.
   The lifecycle must define the following, with selection decisions explicit and inspectable:
   - Initialise phase within an admitted source range on activation. Advance elapsed virtual playback under a declared
     source-rate policy, not inference completion. Select a future phase/window using the controller's target horizon
     and source timing, subject to the stationary-turn contract below. Source-clock time, planned phase, selection,
     target horizon and model-selected generated duration are distinct; do not assume they coincide.
   - For repeated sampling, identify a candidate complete cycle supported by actual source motion, manually; no
     repository cycle-finding tooling exists and no algorithm is required. Freeze source bounds, period,
     endpoint-duplicate convention and accumulated travel/facing transform before the trial. Authoring boundaries alone
     do not establish a complete cycle.
   - First test the raw cycle without boundary processing. Explicit experimental cycle metadata within a human-approved
     role range may authorise a bounded trial without admitting runtime-loop quality or changing the original
     non-cyclic approval. Exact pose/derivative equality or certified recurrence is not a trial prerequisite.
     Measure boundary discrepancies diagnostically; sparse MotionBricks targets/interpolation may tolerate them,
     but repeated generated output on the actual rig must demonstrate suitability before cyclic use is accepted.
   - Keep intended time elapsed across arbitrary cycle counts, independent of inference duration. Preserve four
     consecutive 30 Hz samples across wraps and accumulate each cycle's travel/facing transform before controller
     placement, without origin resets or double placement. Retain ordered local motion, not an exact-seam promise.
     Never silently wrap undeclared non-cyclic content: retain truthful exhaustion and supported next-target handling.
   - On role, stage or variant changes, explicitly retain/transfer compatible phase or initialise the new admitted
     range using declared metadata. Do not require leading-foot variants or infer displayed support feet to switch.
     Missing compatibility must be exposed, not repaired with fabricated source poses or an authored-idle reset.
   - Interruption replaces the intended state/target immediately under CTRL/NAV policy. Stop advancing the abandoned
     state's phase; an idle/action handoff or new locomotion state must not await the old cycle's completion.
   - Overlapping generation/playback may retain compatible work, but each request snapshots its role, stage, phase,
     sampled window and placement with request ownership. Sample-ahead must not itself commit future state. Reject
     obsolete results; late completion must not rewind the clock, revive interrupted state or dictate the next phase.
5. **Controller Boundary:** CTRL-001 maps input/interaction positioning to roles and targets; NAV-001 maps route,
   destination/facing and outcomes. Each controller must expose its role-selection and phase-transfer decisions,
   distinguishing non-crossing positioning from crossing travel without requiring a new control binding here.
   They own target placement and compatibility/replacement, not source authoring. No blend tree or elaborate new
   scheduler is prescribed. No mandatory arc-turn, start/stop, reversal or leading-foot clips are introduced.
6. **Idle/Action Boundary:** [ANIM-003](../003-animation-catalogue/index.md#transitions) supplies idle target
   windows (the start of the effective idle clip, converted to SOMA). External action owners supply action target
   windows with timing, coordinates and posture compatibility. The locomotion controller requests compatible
   targets for arrival, release or interruption and connects from actual realised context. It must not assume an
   idle clip in every locomotion variant. Report missing or incompatible targets explicitly under the CTRL/NAV
   failure policy. This page defines the boundary. ANIM-003 owns idle playback and transitions.
7. **Realisation and Safety:** apply the [shared animation contract](../index.md) for source licensing, native
   presentation, SOMA helpers and actual final-target feedback. Virtual phase and expected source foot phase are
   intended state only; never feed sampled/predicted targets back as observed motion. CTRL/NAV retain collision-root,
   tracking, request ownership and worker-lifetime authority. Source slicing may declare ranges/sample offsets or
    materialise traceable role data without altering motion. The shared SOMA endpoint pass still applies; do not add
    procedural stitching, seam repair, foot-IK rescue or forced endpoint convergence to conceal conditioning
    limitations. Source-boundary processing is only an optional fallback to visible generated-motion harm, with
    explicit changes and human review under TR9; it is not mandatory technical sanitising or generated-output repair.
8. **Validation:** verify semantic coverage, physical metadata, source timing, sampling and lifecycle independently of
   model output, then inspect full actual-rig temporal evidence under both controllers. Candidate-cycle checks establish
   inspectable trial metadata, not seamlessness or artistic approval; do not block the raw trial on unverified loop
   quality. Record intended phase and actual observations separately. Do not invent numerical motion-quality gates or
   claim source cue transfer proves dynamic-sampling suitability. Preserve canonical sources and provenance; the
   [source-production contract](../../tooling/kimodo-pipeline/locomotive-animation-set.md#source-production-contract)
   owns reviewed-action ingestion, technical sanitising, slicing and safe publication.
9. **Local Source Reference Posture:** artists own each variant's explicit compatible source reference posture and
   its provenance. Each authored role begins and ends in that same posture, preserving the whole child-pose
   configuration while placing the root at the role's declared world position and facing. The posture may be
   arms-down standing or another authored configuration, such as arms crossed; it is not limited to a T-pose or
   default standing pose and does not depend on a matching idle asset.
   Source creation is external and artist-owned; retain inspectable Blender source/reference inputs for generic variants
   with one or more stages. Repository delivery covers reviewed-action ingestion, canonical export and source-set
   publication, not a template generator or extension runner.
   Retain feminine and masculine defaults, feminine first, as movement styles rather than claims about every person
   of a given sex. Both complete defaults are required for final content acceptance, not for a representative
   downstream implementation. Produce one continuous multi-role take, not separate per-role takes. Each role contains
   multiple intended cycles, with acquisition and settling outside declared middle and representative-speed ranges.
   Preserve the predeclared extraction policy and full chronology. Do not automatically select favourable windows or
   shift failed ranges; an explicit human range revision creates a new source/review revision, not an old-evidence pass.
   A trial cycle wholly inside an already approved role is new experimental metadata under TR4, not a source/range
   mutation, covert favourable crop or promotion of rejected motion. Retain whole chronology and the approval bounds;
   human review of actual motion remains necessary for content admission and cyclic suitability.
   Mandatory human review binds approval to source hash, action and ranges, covering acquisition, movement interiors,
   transitions and settling on the canonical source and on the reference characters in Godot native playback, or in a
   Blender preview independently shown to match native playback on skinned landmarks. Matching
   boundaries do not establish interior suitability, phase or loopability. Keep finite-data, supported
   skeleton/coordinates/rate, range, identity and claimed-complete coverage checks; human approval does not waive them.
   Preserve declared authoring constraints, source protections and clear processing errors without requiring
   a repository generation runner or extra misplaced-constraint tests. Boundary conditioning is not runtime phase
   control or permission to repair bad motion. The
   [source-production contract](../../tooling/kimodo-pipeline/locomotive-animation-set.md#source-production-contract)
   is normative for acceptable source properties, reviewed-source processing and source-evidence delivery.

### Stationary-Turn Angular Mapping

This TR3–4 contract is normative for both [CTRL stationary turns][ctrl-turn] and [NAV terminal-facing turns][nav-turn].
Both controllers must use the same mapping component; each owns its allocation, request lifecycle and finish policy.

[ctrl-turn]: ../../ctrl/001-locomotion/index.md#stationary-turn-conditioning
[nav-turn]: ../../nav/001-npc-navigation/index.md#finite-terminal-facing-turn-plan

- **Cycle Allocation:** allocate a signed angle to each cycle of the selected directional source, with an initial
  maximum magnitude of 120 degrees per cycle. Use the source-bound angular metadata to validate the selected cycle's
  identity, timing, bounds, facing/sign convention, measured signed span and ordered primary lift landmarks.
- **Root-Yaw Mapping:** anchor at the cycle's planned start facing. At each source sample, add its signed yaw change
  from cycle start multiplied by `allocated signed angle / measured signed source span`. Use actual angular
  progress at each landmark, not elapsed-time fraction; larger allocations place the same cues farther apart on the
  target arc. Preserve both primary lift cues where present, their order, local holds and authored micro-reversals,
  and declared wrap transforms. Do not invert yaw as a phase clock or turn backwards to catch up to past cues.
- **Source Pacing:** leave non-root articulation and source timing unchanged. Use consecutive original samples under
  TR3, without time warp, scaling generated output or adding corrective rotation. The allocation changes angle,
  not cycle duration. Landmarks are source cues, not a shared requirement for one request per cue. Model-selected
  duration and realised pace remain separate evidence; compare full displayed cycles with the source duration.
- **Conditioning and Ownership:** every submission uses actual post-modifier realised context under ANIM TR5, not
  source poses, planned facing or unplayed predictions. Snapshot the cycle, mapping, selected window and ownership
  with each request; retain the controller's interruption and stale-result rules. Mapped targets are not proof of
  displayed footwork or contact.

#### Actor Facing During Stationary Turns

At each tick during a stationary-turn cycle, the game sets actor facing to the mapped yaw at current planned cycle
progress, not to yaw derived from generated hips or any other generated bone. Hip orientation depends on the
animation variant; for example, crawling hips can be oriented very differently from hips in a standing turn.
The generated pose still supplies articulation. Apply the planned yaw once: residual generated hip yaw must not
drive the actor or be applied twice in presentation. The planned cycle endpoint is unchanged.

## In Scope

- Required role vocabulary, generic variants/stages, physical speed and timing metadata, and dynamic window sampling.
- Game-owned phase lifecycle, target placement boundary, ANIM-003 idle and external action integration, and
  safety/validation.
- Variant-local source reference postures, continuous multi-role authoring and whole-window source validation.

## Out Of Scope

- Idle content, playback and transitions, owned by [ANIM-003](../003-animation-catalogue/index.md); an action
  system; mandatory transition, arc or leading-foot clips; inferred displayed-foot matching; procedural stitching;
  terrain/contact adaptation; and general rig support.
- Mandatory automatic source-window discovery or a new cycle-finding algorithm, and general exporter/schema design
  beyond the required reviewed-source pipeline. Cycles and windows are identified manually.
  These exclusions do not waive ingestion, usable metadata, role slicing, coverage, lifecycle or runtime validation.

## Acceptance Criteria

### User Requirement Acceptance

1. **ANIM-002-UAC-001 (UR1):** Source audit and eventual full temporal demonstrations distinguish all required roles,
   including repeatable left/right non-crossing interaction positioning versus crossing lateral travel.
2. **ANIM-002-UAC-002 (UR2–3):** Variant/stage examples include different physical speeds and uneven local motion;
   sampling follows intended progression without displayed-foot synchronisation or delayed input response.
   Review full actual-rig stationary turns with intensity changes, release and settlement, including ordered cues
   from both feet where present. Check that larger arcs read as larger turns at source pace and that neither controller
   sustains rotation against the command or reverses actor facing because of generated hip motion. Distinguish
   authored micro-reversals in the mapped source from hip-driven reversals. Correct facing does not prove footwork.
3. **ANIM-002-UAC-003 (UR4):** Locomotion connects to compatible ANIM-003 idle targets and externally owned action
   targets (TR6) without requiring a runtime ANIM-002 idle asset or passing through the source reference posture;
   unavailable targets are reported truthfully rather than silently reset. Source reference ownership remains local and
   independent of those targets.

### Technical Requirement Acceptance

1. **ANIM-002-TAC-001 (TR1–4):** Validate role coverage and metadata against actual sources, including physical units,
   reference versus local velocity, usable ranges and consecutive windows at multiple phases and cyclic boundaries.
   Verify declared cycle bounds/period/endpoint convention against the source motion and distinguish trial metadata
    from admitted runtime-loop metadata. A catalogue containing only fixed entry/exit windows is insufficient.
   For stationary turns, validate measured signed net span, representative rate and ordered local progression against
   the declared episode, including holds/reversals. Validate each foot's cue phase, order and associated angular
   progression with the declared measurement convention, not one global peak or an assumed angular/time midpoint.
    Missing cues remain explicit coverage/validation gaps; do not infer support or contact from joint-origin peaks.
   For both controllers, verify use of the same mapping component. Independently check allocation, cycle start
   facing, scale and angular landmark placement against measured source yaw, with unchanged non-root articulation
   and four consecutive 30 Hz poses per connection. Check unchanged source timing and compare realised cycle duration
   with source duration in actual-rig captures; distinguish request handoffs and inference holds from source pacing.
   Verify normal CTRL/NAV startup without activation variables loads the default pack's complete set and binds all
   eight roles and angular metadata to the same current source. Quality-admission labels must not be interpreted as
   runtime activation switches.
2. **ANIM-002-TAC-002 (TR4–6):** Independent lifecycle checks cover advance/sample-ahead, loop/end boundaries,
   role/stage/variant switches, interruptions, idle/action compatibility and overlapping/late requests. Traces identify
   controller-owned intended phase independently of generated duration, displayed feet and actual context timestamps.
   Check multiple cycle counts, wrap-straddling four-frame windows and accumulated travel/facing without origin reset
    or double placement; retain finite-range exhaustion and abandoned-phase invalidation checks for non-cyclic content.
   Check stationary-turn phase/facing coherence at cycle boundaries, including next-cycle intensity changes in
   CTRL-TAC-005 without mutation, rewind or loss of realised context.
   For both controllers, compare actual actor yaw at each tick against mapped yaw at the recorded cycle progress
   within a declared, tunable angular tolerance. Vary generated hip yaw at the same planned progress and verify
   actor facing is unaffected, yaw is applied once and the planned endpoint is unchanged. Verify the worker still
   receives actual post-modifier realised context, not the planned pose.
   Under NAV-TAC-004, separately verify fixed-plan ownership, actual-context provenance, every planned primary endpoint
   played before ordinary handoff and finite final-facing/compatible-idle completion. Elapsed lookahead, native duration
   and advance planning must not skip service obligations; retain immediate safety/replacement interruptions.
3. **ANIM-002-TAC-003 (TR7–8):** Verify unchanged collision/tracking/worker ownership and final-target feedback; review
    actual-rig temporal results and source integrity separately from metadata/sampling correctness. Report gaps and
    model limitations without corrective locomotion or arbitrary thresholds. Review repeated actual-rig generated
    motion across wraps from the raw candidate, with diagnostic boundary measurements but no exact-seam gate.
     MotionBricks interpolation is a possibility to test, not evidence of tolerance or cyclic suitability.
   Stationary-turn review includes the full actual-rig turn, intensity changes, release and settled tail. Metadata
   correctness or four future poses alone does not establish displayed mid-turn footwork or resolve slow turning.
4. **ANIM-002-TAC-004 (TR7–9, UR2/4):** Audit externally authored source/reference inputs, local-reference boundary
   placement, multiple intended cycles, declared extraction policy and full chronology, independently of catalogue
   idle assets.
   Verify reviewed-action ingestion without a generation-runner receipt, hash/action/range-bound approval and essential
   data checks. Check documented technical sanitising preserves poses/trajectory and role slices retain ordered windows;
   range revisions require new review. Experimental cycle declarations inside approved roles retain original approval
   bounds and full chronology; they do not admit cyclic quality. Any fallback boundary processing requires visible
   generated-harm evidence, explicit traceable changes and human review, separate from technical sanitising.
   Review full source/preview motion, not endpoints alone, separately from implementation safety review and actual-rig
   runtime proof. No repository source-generation machinery or extra misplaced-constraint test is a prerequisite.
   Report representative versus complete coverage; final default acceptance requires both defaults' actual sources
   and reference-character previews under TR9's native-playback authority. Correct metadata or a one-role
   demonstration does not prove natural all-role flow.

## Implementation Status

Normal CTRL and NAV share `game/assets/animations/packs/default/locomotion/feminine/set.json`, resolved through the
ANIM-003 locomotion view's default `feminine` entry; every role binds to
`landing-ablation-01.json`, the complete 1–1200 at 30 Hz export of `source_feminine_landing_ablation_01`.
The [source manifest](../../../game/assets/animations/packs/default/locomotion/feminine/set.json),
[angular metadata](../../../game/assets/animations/packs/default/locomotion/feminine/turn-angular-reference.json)
and [source-data guide](../../../game/assets/animations/packs/default/locomotion/feminine/README.md) bind current
hashes, measurements, usable ranges and half-open repetition selections. Durable source/authoring, descriptor,
approval and provenance use the `game/assets/animations/source/locomotion/locomotion_feminine_landing_ablation_01`
prefix.

Current inclusive usable ranges and half-open repetitions are:

- Forward: 46–106 / [58,94); backward: 196–256 / [207,246).
- Left/right non-crossing: 346–406 / [346,376) and 496–556 / [496,526).
- Left/right crossing: 646–706 / [646,676) and 796–856 / [796,826).
- Left/right turns: 946–1006 / [946,1006) and 1096–1156 / [1096,1156).

Read primary lift phases, measured span and angular progression from the sidecar, including local reversals;
neither turn is assumed to span 90 degrees. Runtime does not search for a substitute cue.
Source ranges remain non-cyclic; `EXPERIMENTAL_NOT_ADMITTED` labels describe content quality,
not an activation flag. They confer no seamless-loop, sole-contact or generated-motion naturalness guarantee.

Shared normal-source adoption and native NAV path/handoff with finite terminal turns have technical evidence; combined
independent review remains pending. CTRL integration with the shared angular mapping and planned-yaw facing for both
controllers are in progress. Source stepping/foot-sliding mitigation is user accepted; initial twitch is explicitly
deferred. Runtime generated gait, sliding, turn pace, headset/interaction quality and raw repetition admission remain
open. Masculine/default content, reference previews and known native-shutdown/hand-origin risks remain unresolved.
Broader architecture and final acceptance remain deferred-with-risk; plan steps 5/6 are open. Normal-source adoption
does not complete ANIM-002 or authorise new source generation/model calls.

## References

- [Project Specifications](../../index.md)
- [Shared Animation Contract](../index.md)
- [ANIM-003: Animation Catalogue](../003-animation-catalogue/index.md)
- [Player Locomotion](../../ctrl/001-locomotion/index.md)
- [NPC Navigation](../../nav/001-npc-navigation/index.md)
- [Kimodo Source Protection and Current Tooling](../../tooling/kimodo-pipeline/locomotive-animation-set.md)
