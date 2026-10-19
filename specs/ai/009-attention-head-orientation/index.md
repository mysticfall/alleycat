---
id: AI-009
title: Attention-Driven Head Orientation
---

# Attention-Driven Head Orientation

## Requirement

NPCs must present attention-driven gaze with head orientation rather than eyes alone: the head must follow the AI-007
gaze anchor according to how sustained the attention is, while quick glances remain eyes-only and gaze selection
semantics stay wholly with AI-007.

## Goal

Make sustained attention read as facing behaviour — an NPC turns its head towards a conversation partner, cranes its
neck for targets beyond comfortable eye range, and stoops naturally towards low targets without rolling its head —
without changing the AI-007 selector or the IK-001 solver, without affecting the player's XR-owned head path, and
leaving eye presentation to VISION-001, which owns eye–head turn synchronisation.

## User Requirements

1. An NPC engaged with a sustained attention target — a conversation partner — turns its head to face that target,
   even when the target is well within eye rotation range. The NPC faces whoever it talks to; it does not fix its
   face and roll its eyes.
2. Tall or short NPCs looking at targets beyond comfortable eye range — for example a tall NPC looking down at the
   player — crane their neck naturally instead of saturating eye rotation alone.
3. Brief side glances within eye range stay eyes-only: the head retains its pre-glance commanded orientation relative
   to the character and continues easing towards it, rather than freezing abruptly or following a moving previous
   person. Brief glances beyond eye range move the head only as far as needed, preserving the glance's quick character.
   Glance-end reassignment resumes the attention policy; clearing gaze eases the head back to neutral.
4. Head motion is smooth and visibly delayed relative to the eyes — eyes lead, the head follows — without snapping
   or oscillating when a target hovers near a threshold.
5. When gaze clears, the head returns to neutral; when a target lies beyond head range, the head strains towards it
   as far as it can while the eyes keep tracking, reading as trying to see.
6. Player characters remain unaffected; only NPC role templates that compose the controller gain attention-driven
   head orientation.
7. While tracking, the head orients by pitch and yaw only: the NPC must not visibly roll (cock) its head about the
   forward axis, whichever side or height the target moves to, and any transient roll introduced by idle motion must
   decay rather than persist.
8. Looking at a low or crouched target — for example a conversation partner who has crouched — reads as a natural
   stoop: the downward look spreads across the neck and upper spine instead of craning the head alone at the skull
   base.
9. During transit towards a sustained target the head keeps making progress: while genuinely engaged and still far
   from its target, the head must never stop dead — a full freeze lasting 150 ms or longer is a defect. Heads
   holding deliberately short glance dwells are not stalls. Heads parked at the commanded saturation target
    awaiting centring are not stalls. A head that has correctly reached its envelope-limited command may deliberately
    rest with an unreachable target residual; this is distinct from awaiting centring and does not excuse a stalled
    approach, an incorrect command or failure to reach a reachable target.
10. The stoop keeps the head on the stoop line: the head's contribution to the downward look flexes in the same
    direction as the face aim — the chin continues the curve — rather than counter-extending at the skull base.
    The swan-neck counter-bend is an anatomy defect even when the pitch shares alone pass.

## Technical Requirements

### Ownership and Composition

1. `OrientingController` is a concrete `IKTargetIntentProvider` subclass living under `AlleyCat.Mind.Attention`
   alongside the AI-007 selector, named in anticipation of future body-orientation expansion. It is composed as a
   direct child of its owner `Mind` in NPC role templates, following the AI-007 placement precedent. It is not an
   `IComponent` and not a member of `Character.Components`; player and shared player-base templates do not compose
   it. The Mind-side namespace is deliberate: the controller is an attention consumer, Mind already references
   `AlleyCat.Vision` through the AI-007 selector, and Mind referencing `AlleyCat.IK` introduces no cycle.
2. The NPC installer wires the controller into `CharacterIK.HeadTargetIntentProvider`, the existing head provider
   slot that drives Neck-Spine CCDIK (IK-001) per the provider property mapping in the IK implementation notes. The
   controller returns an `IKTargetIntent` — an explicit world-space target transform plus desired influence — and a
   desired influence of 0 deactivates the neck-spine modifier group. The player's head slot remains XR-owned and
   unchanged.
3. The controller consumes only its owner character's `IVision`: it reads `LookTarget` — the assigned anchor's
   world position and how long that same anchor has been continuously assigned. It must never call `SetLookTarget`
   or `ClearLookTarget` and must not read AI-007 selector state. AI-007 remains the sole gaze selector; this
   controller owns only when and how far the head follows — biomechanics, not semantics.
4. The head tracks the assigned anchor's world position only, never saccade offsets.

### Engagement Policy

5. Engagement is hybrid, with two triggers:
    - Saturation rule (immediate, geometric): evaluate the anchor direction per skeleton evaluation against the
     eye comfort cone — the angular range within which the eyes alone are expected to carry the gaze. When the
      direction exceeds the cone, request the smallest command change that brings predicted gaze inside it, subject
      to the anatomical envelope and roll-free constraints in Technical Requirement 25.
   - Sustained-attention centring (temporal): when the same anchor has been continuously assigned on
     `IVision.LookTarget` for at least the centring delay, the head centres on the anchor — full centring, even when
     the anchor is well within the eye rotation bound. The degree of head orientation encodes engagement, not
     geometry.
6. Glance versus sustained is classified only from continuous same-anchor assignment duration measured on
   `LookTarget`; assignment of a different anchor or a clear resets the timing. There is no conversation or speech
   detection: conversation partners produce sustained centring because they are sustained primary targets under
   AI-007 dwell semantics.
7. Glance head-hold retains the pre-glance commanded orientation in the character-relative anatomical frame; smoothing
   continues towards that command. It neither freezes the realised pose nor follows the previous person's movement.
   During a brief out-of-cone glance, request only the necessary comfort correction under Technical Requirement 25.
   Reassignment resumes policy for the assigned anchor; clear releases towards the upstream pose. Clear/re-engagement
   invalidates unrelated held commands, so a new attention episode cannot inherit a stale Hold reference.
8. Sustained head aim brings the anchor onto the eye-neutral axis. A small residual eccentricity is tunable, with
   full centring as the default.

### Thresholds and Orientation Envelope

9. Comfort-cone and envelope thresholds are per-axis tunable with asymmetric vertical defaults mirroring physiology:
   comfort cone of approximately ±15° horizontal and 10° up / 15° down; orientation envelope of approximately ±75°
   horizontal and 40° up / 55° down. The eye hard clamps (35° horizontal / 25° vertical) remain owned by
    `EyesController` under VISION-001; this spec does not change them.
    Establish the envelope from authored anatomical geometry relative to the character with explicit face-axis
    conventions. Character-root rotation transports it; idle animation and solved output do not move it. Never derive
    it from the first runtime animated or solved pose.
10. A target beyond the envelope strains best-effort to the envelope edge while the eyes keep tracking, reading as
    trying to see. IK-001 joint constraints remain the final authority on the achievable pose, and the envelope is
    the declared seam for a future body-turning AI-family sibling using `INavigation`.

### Motion Feel, Release, and Timing

11. The head must not snap like eyes. Engagement applies a reaction delay (approximately 0.15–0.2 s), rate-limited
    smoothed rotation, and an influence ramp; the head must remain materially slower than eye seek so the eyes lead
    and the head follows. Release applies hysteresis on angle and engagement so a target hovering near a threshold
    does not flap the head.
    Reaction gating applies at engagement onset, not repeatedly during sustained transit. Aim smoothing remains 0.07;
    changing this setting requires approval supported by measurements.
12. When `LookTarget` is cleared, ease towards the current upstream roll-free face pose while influence decays to 0.
    At zero influence native animation owns the pose, including authored roll. IVision's existing fallback gaze
    (1 metre ahead) stays eyes-only. Re-engagement uses explicit current state, not a prior episode's held command.
13. The centring delay (default approximately 0.6 s) must exceed AI-007's configured secondary dwell so brief
    secondary glances never trigger centring. This relationship is a tuning constraint, not an enforced contract:
    both values remain independently tunable, and the defaults must keep a safe margin.

### Reference Frame and Determinism

14. Keep upstream input and solved feedback separate. At the existing `CharacterIK` Begin stage, before head-target
    evaluation, capture native skeleton Head and Neck poses plus authored face/Viewpoint and pivot offsets. Do not
    read solved attachment globals as upstream input, advance/seek animation again, write bones, or reconstruct an
    animation sampler. Validate that preceding stages do not write bones and that hip/head modification is downstream.
    The feature-local immutable snapshot contains binding generation, skeleton identity, evaluation sequence, delta
    and mode, character/skeleton transforms, upstream head/pivot/face geometry and authored offsets. Preserve scale for
    positions and use proper rotation bases for orientation maths.
    Capture a separate final solved-face packet directly from bones at `SkeletonUpdated`, independent of attachment
    subscriber order. Include sequence, binding identity/generation and character-relative face/eye-origin geometry
    with its capture root transform. At Begin evaluation k, feedback is from completed evaluation k−1, transported
    through the current root; it is not the current solve. Bootstrap with an explicitly marked upstream sample until
    solved feedback exists. Pair completion with evaluation sequences rather than counting pose-only signals as ticks.
    Compute comfort from `(anchor - solvedEyeOrigin)` transformed by the inverse actual solved-face basis.
    Never subtract commanded yaw/pitch from measured angles to approximate frame conversion. Commands are orientations
    in the stable anatomical frame; solved feedback cannot redefine upstream geometry or the envelope.
15. Evaluation is delta-driven and deterministic. The pure engagement and aim logic — per-axis threshold evaluation,
    glance and sustained classification, hysteresis state machine, head-hold, and rate-limited smoothing — must be
    extractable as a Godot-free seam injectable into unit tests, following the `EyesLookMath` precedent in
    `game/src/Vision/`.
    Advance policy exactly once per skeleton evaluation using its supplied delta and one captured anchor identity and
    world position; this path has no duplicate render-process advancement. Zero-delta recomposition refreshes geometry
    without ageing assignment, reaction, Hold, smoothing or influence state.

### Purity, Lifecycle, and Validation

16. The controller introduces no changes to `EyesBehaviour`, `EyesController`, saccades, blinking, the AI-007
    selector, or the IK-001 solver. Eye–head turn synchronisation — keeping the eyes consistent with head motion
    during transit — is specified by [VISION-001: Eyes](../../vision/001-eyes/index.md), not by this controller.
17. The controller resolves its owner character's `IVision` only after the owner Character's component projection
    commits, following the AI-007 Mind lifecycle pattern; projection changes must not leave stale Vision bindings.
    Teardown stops providing intent — influence 0 — so CharacterIK falls back to its existing safe idle.
    Invalidate upstream and feedback snapshots on teardown, projection replacement, skeleton or marker rebinding and
    binding-generation changes. Missing required upstream input makes the provider inactive; it must not reconstruct
    a neutral reference from solved output.
18. Exported settings must validate before activation: finite angles and durations; per-axis envelope angles must
    exceed the corresponding comfort-cone angles; reaction delay, centring delay, and rate limits must be finite and
    positive.

### Roll-Free Intent and Aimed Origin

19. The published face intent basis is roll-free by construction, measured against a character-stable up reference:
    forward is the commanded face forward direction in the anatomical frame; the reference up
    comes from the owning character's global basis, falling back to world up when the character node cannot be
    resolved; right and up are then rebuilt from the commanded forward and the reference up exactly as
    `Basis.LookingAt` constructs a basis — with `zAxis = −forward`: right is the normalised cross product of
    reference up and `zAxis`, and up is the cross product of `zAxis` and right — preserving the commanded forward
    and yielding a proper right-handed basis with zero roll about the forward axis. Rebuilding the basis this way
    strips roll from the command. The construction guards the near-parallel forward/up case —
    which the envelope caps should prevent — by falling back defensively to the authored anatomical up, and
    returns the orthonormalised commanded basis when that fallback is itself unusable.
20. Upstream pose, anatomical envelope and stable-frame command have independent ownership. Do not reconstruct neutral
    from solved face orientation with applied aim removed. Solved idle sway, twist redistribution, partial influence
    and CCDIK roll must not accumulate into these references. Validate envelope and comfort membership after roll-free
    construction; release uses the upstream pose under Technical Requirement 12, not a solved-neutral re-anchor.
21. The controller exports a `NeckPivot` marker reference, authored at the neck articulation under the IK-001
    template contract. Upstream head, pivot and face geometry must come from the upstream snapshot, never solved
    attachment globals; solved output cannot advance the target. Record requested and mediated targets separately
    from the actual solved pose. Each evaluation resolves the published origin once, in the authored envelope frame,
    through one exact root transport shared by publication and gaze prediction:
    - Orbit construction (neutral, upward and lateral commands, and every fallback): with h the upstream Head
      origin, p the upstream Neck/pivot position, U the upstream face rotation and D the commanded face rotation in
      the same declared frame, publish `R = D * inverse(U)` and `target = p + R * (h - p)`, converting face
      orientation to Head orientation through the authored face offset. D=U yields target=h and an infinitesimal
      command change approaches zero displacement.
    - Downward fold-aware resolution: the controller re-anchors a bounded prediction frame onto the animation-owned
      upstream chain poses captured this evaluation — animation stays immutable upstream; the frame never advances,
      seeks or writes it, and its zero-fold member reconstructs the upstream pose exactly. The frame enumerates the
      co-forward fold family: each movable chain joint folds about its authored sagittal axis by a fraction of the
      increment its authored IK-001 cone still admits against the authored local rest under the current incoming
      pose (the Head end bone takes no fold), so the authored constraints bound the family and are not retuned by
      this policy. Selection is continuous in the command: the direction-matched family fraction is interpolated on
      the family sweep curve, plus a demand-deepening fraction ramped between the authored comfort-cone and envelope
      downward bounds (a fixed authored strength, currently 14 grid steps at full demand — a tuning value, not a
      public contract or runtime input). The selected family displacement blends continuously from the orbit
      displacement across an authored engagement band and is then yaw-transported about the pivot; neutral, upward
      and lateral commands reproduce the orbit construction exactly.
    The final published request after blending and yaw transport is generally not a fold-family member: family
    membership of the intermediate selection certifies neither reachability nor anatomical safety of the published
    request, and no universal reachability or safety claim is made for any published request. The installed
    demand-window Head budgets and IK-001 joint allowances remain the acceptance of what the chain actually
    achieves. Gaze prediction and publication share this one geometry per evaluation (Technical Requirement 25), so
    a candidate command's predicted eye origin is the eye position of that same resolution. Unsupported geometry —
    upstream poses outside the rest-bounded domain, a non-rigid skeleton basis, degenerate reference directions —
    is reported with its reason, and only the fold resolution fails closed: the fold construction and any
    fold-derived certification, acceptance or exception claim are refused for that evaluation, while the unchanged
    orbit construction continues to be published at the policy's existing influence. This fallback is not a provider
    deactivation and no separate unsupported-domain refusal gate exists; no fold-family reach or safety guarantee
    follows from it, and the installed demand-window Head budgets, IK-001 joint allowances and all other guards
    remain mandatory for what is actually published.
22. When `NeckPivot` is unset or invalid, publish the upstream Head origin, not its last solved origin. Missing required
    Head input instead makes the provider inactive under Technical Requirement 17. This fallback does not guarantee
    zero positional force when other modifiers move ancestors.

### Transit Stall and Stoop Anatomy

23. Stall rule: during the complete transit towards a sustained target, no contiguous
    window of 150 ms or longer may be a full freeze. A full freeze is a contiguous interval within active
    engagement — look target assigned and orienting influence above 0.5 — in which the rendered face yaw, pitch,
    and roll ranges are each at most 0.05° and the face-to-anchor residual stays above 2° throughout. The
    designed-dwell exclusions are the designed glance dwell and the designed saturation park. Designed glance dwell:
    the target was cleared or replaced within 0.6 s of assignment while the head held within the freeze tolerance of its
    pre-assignment orientation. A sustained transit target never qualifies for the glance dwell. Measure dwell
    from actual assignment through clear/replacement against the pre-assignment pose, not the candidate window
    start. Require residual >2° from the first sample throughout. Designed saturation park: while the same anchor stays
    continuously assigned before centring eligibility, the head legitimately parks at the commanded saturation
    target of Technical Requirement 25, holding the geometric saturation residual — the face-to-anchor residual
    that command implies — until centring becomes eligible. A freeze window qualifies as a designed saturation
    park only when all three conditions hold: the head stays within the freeze tolerance of the commanded
    saturation target throughout; the face-to-anchor residual stays equal to the geometric saturation residual
    throughout; and the window ends at centring eligibility — the same anchor having remained continuously
    assigned for the authored centring delay (Technical Requirement 13), after which sustained centring must
    move the head unless the separate rest-at-limit exception below is independently verified. Sustained transit never
    qualifies for saturation park: a freeze during the approach to the commanded
    saturation target, or persisting beyond centring eligibility, is not excluded by that rule.
    A separate rest-at-limit exception permits deliberate stationary strain only when all of the following are
    independently verified throughout the claimed resting window:
    - Current-anchor geometry under Technical Requirement 25 proves an unreachable centring residual within the authored
      anatomical envelope; the measured residual is consistent with that envelope-limited result, not merely outside
      eye comfort. A helper infeasibility flag alone is not independent proof.
    - The recorded command is the correct roll-free envelope-limited centring command under Technical Requirement 25
      for that geometry, not an intermediate smoothed command, a stale command or an arbitrary clamped orientation.
    - The actual head has reached that command within 0.05° on each yaw, pitch and roll axis throughout the window.
      Eligibility is orientation-defined: requested, mediated and solved positions remain separately recorded
      diagnostics under Technical Requirement 21 and the Complete-Window Evidence Contract. Angular agreement does not
      prove positional attainment, but this exception requires neither positional equality nor a positional tolerance.
    Eligibility starts only after attainment, never at incidental mid-approach stillness. Re-evaluate the facts when
    anchor identity/position, upstream geometry, root transport, binding or command changes; any change that invalidates
    them ends eligibility immediately. Missing or stale evidence cannot qualify. A mode name, limit occupancy, yaw value
    or stillness alone is insufficient; IK constraint contact is not a general physical-limit exemption. Reachable
    targets, stalled approaches and incorrect commands remain subject to the unchanged 150 ms rule. Classify only
    independently verified resting intervals; retain and evaluate every non-eligible part of the raw freeze window.
    Detector adoption requires recorded red validation with head execution suppressed and boundary replays covering
    brief dwell, sustained freeze, residual crossing and pose drift.
24. Head counter-bend guard: for the stoop, the head contribution must be same-signed
    with the face aim — the head flexes into the downward look instead of counter-extending at the skull base.
    Skull-base counter-extension is an anatomy defect. The guard is an outcome requirement owned by this spec and
    delivered jointly with the IK-001 constraint set; assert it against actual face pitch with correctly composed
    joint contributions, never a sum of world Euler angles.

### Geometric Saturation and Validation Gates

25. Use a small bounded geometric helper, not a new skeletal solver. For saturation, choose the smallest change from
    the held/reference command that places predicted gaze inside the asymmetric comfort cone while respecting the
    anatomical envelope and roll-free orientation. Sustained centring seeks to minimise residual within that envelope
    using bounded best-effort geometry, not a guaranteed global angular-residual optimum. For supported unreachable
    yaw-limit geometry, the correct bounded reference is the yaw-cap command with pitch analytically chosen to zero
    predicted face-local vertical direction on a valid admissible branch. Independently justify the yaw cap from
    first-principles upstream/requested geometry, derive that pitch and prove the remaining residual unreachable.
    This reference is specific to geometry satisfying those proofs, not arbitrary target configurations or merely
    clamped commands. Unsupported geometry or missing proofs cannot qualify for the rest-at-limit exception.
    Specify and independently test feasible cases, deterministic tie-breaking and explicit infeasible/best-effort
    residual results. Predict the near-target eye origin from the requested Head pose and authored Viewpoint offset,
    consuming this evaluation's fold-aware frame under Technical Requirement 21 so prediction and publication
    resolve one shared construction with no command gate; this predicts requested geometry, not exact constrained
    reach, and family membership of the intermediate selection does not certify the blended, yaw-transported
    published request. An unchecked one-pass Euler correction followed by roll
    stripping is not an exact solution. The chosen algorithm requires independent review before integration; escalate
    if the contract cannot be met without wider redesign.
26. Before production integration, independently validate repaired measurement oracles, then prove upstream/solved
    separation, evaluation/delta ownership, zero influence and lifecycle in the installed engine for both production
    and isolated-fixture scheduling. Gate the geometric helper with source-independent coupled-axis, partial-influence,
    comfort-boundary, envelope-clipping and near-target tests, including 14° actual local yaw at −40° head pitch.
    Keep CCDIK, modifier ordering and the IK-001 authored cones unchanged; cone and ordering changes follow IK-001 and
    require an approved user decision. Failure of anatomy or reach requires a decision supported by measurements, not
    weaker acceptance or unapproved retuning.

## In Scope

- A direct Mind-child `OrientingController : IKTargetIntentProvider` under `AlleyCat.Mind.Attention`, composed in NPC
  role templates and wired by the NPC installer into `CharacterIK.HeadTargetIntentProvider`.
- Hybrid engagement policy: immediate saturation residual, sustained-attention centring from continuous same-anchor
  `LookTarget` assignment timing, glance classification, and glance head-hold semantics.
- Per-axis asymmetric comfort cone and orientation envelope with physiological defaults, best-effort edge strain,
  and release to neutral.
- Reaction delay, rate-limited smoothed rotation, influence ramp, release hysteresis, and the centring-delay versus
  AI-007 secondary-dwell tuning constraint.
- Independent upstream geometry, authored anatomical envelope, stable-frame command and delayed solved-face feedback.
- Roll-free intent and non-recursive upstream origin geometry: the pivot-orbit construction with unset-pivot
  fallback, extended on the downward regime by the animation-anchored, rest-bounded fold family with continuous
  selection, authored demand deepening and coupled gaze prediction.
- Bounded geometric saturation, scheduling/lifecycle probes and repaired independent measurement oracles.
- The approved transit stall rule: full-freeze detection bounds, the designed glance-dwell and saturation-park
  exclusions, the distinct verified rest-at-limit exception, and the detector's red-validation evidence requirement.
- The head counter-bend guard for the stoop: the head contribution stays same-signed with the face aim, with
  skull-base counter-extension treated as an anatomy defect.
- Deterministic delta-driven evaluation with a Godot-free pure-logic seam, settings validation, and
  component-projection lifecycle integration with teardown to IK safe idle.

## Out Of Scope

- Body or locomotion turning via `INavigation` for behind-the-character targets; the orientation envelope is the
  declared seam for this future AI-family sibling.
- Conversation, speech, or any semantic target classification; sustained-ness is inferred from gaze assignment
  timing only.
- Changes to AI-007 selection policy, saccades, blinking, or Vision fallback.
- Eye–head turn synchronisation: `EyesBehaviour`/`EyesController` changes that keep the eyes consistent with head
  motion during transit are owned by [VISION-001: Eyes](../../vision/001-eyes/index.md), which specifies the
  requirement; this spec does not duplicate that contract.
- Changes to the IK-001 solver or the player's XR-owned head path.
- Unapproved tuning changes: retain aim smoothing 0.07, eye smoothing 0.08, eye clamps and IK-001 cones.
  Further tuning needs a user decision supported by measurements; frozen acceptance is not a tuning parameter.
  The user-approved provisional playtest acceptance profile is an authorised acceptance revision, not an
  unapproved tune.

## Acceptance Criteria

New behavioural coverage uses the reference female in a minimal real-pipeline fixture; existing dual-character
authoring/regression coverage and production-startup photobooths remain required. Frozen bounds and complete windows
below require explicit user approval supported by measurements to change. Valid current-fixture results establish only
conditional acceptance: historical original-build reconstruction is waived, not proof of historical symptom resolution
or universal animated-idle correctness.

Demand-window Head acceptance applies the user-approved provisional playtest acceptance profile: Head position
error ≤10 mm and Head rotation error ≤0.5°, inclusive, on every recorded demand sample. Joint swing and off-X hinge
allowances are defined in [IK-001 Technical Requirement 6](../../ik/001-neck-spine-ik/index.md#technical-requirements)
and are a normative dependency of the demand checks. These bounds accommodate the visually accepted current
implementation for playtesting; they are measurement allowances, not numerical-precision proof, anatomical claims or
symptom-resolution evidence. Authored caps, the authored-rest measurement reference, fixation, roll, complete
observation windows and fail-closed invalid-input checks remain unchanged; sealed historical records keep their
original criteria, with any reclassification identified separately.

### User Requirements

1. Visual verification and integration coverage show a tall NPC looking down at a shorter character — the player —
   craning its neck naturally, with the eyes no longer carrying the downward angle alone; the short-NPC looking-up
   case behaves symmetrically.
2. Coverage shows a same-height sustained target well within eye rotation bounds receiving full head centring after
   the centring delay: the NPC faces its conversation partner rather than fixing its face and rolling its eyes.
3. Coverage shows brief in-cone glances retaining the pre-glance character-relative command with continued easing,
   without following a moving previous person; out-of-cone glances request minimal comfort assistance. Reassignment,
   clear and re-engagement resume policy/release without unrelated stale commands, including under root rotation.
4. Coverage shows head engagement visibly delayed and smoother than eye movement, hysteresis preventing flapping for
   a target hovering near a threshold, and release to neutral when gaze clears.
5. Coverage shows a beyond-envelope target producing best-effort head strain to the envelope edge with the eyes
   still tracking, followed by deliberate stationary rest only after correctly reaching the envelope-limited command
   under Technical Requirement 23. Arrival and rest must read as trying to see, not a frozen approach.
   (TRACES UR-5, UR-9)
6. Coverage confirms player templates remain free of the controller and the player's XR head path is unaffected.
7. Photobooth coverage on the reference female shows no visible head roll while tracking: sustained lateral and
   diagonal holds keep the mean roll magnitude within 2.0° over any 0.5 s window and transient peaks within 3.5°
   across the original alternating ±35° yaw/∓25° pitch sequence and 55° lateral hold for 3.2 s.
8. Photobooth and isolated-fixture coverage on the reference female shows a low target (the −40° stoop target)
   producing a distributed stoop: the Head bone carries at most 55% of the face pitch, and the Neck and UpperChest
   bones each carry at least 10% of the face pitch with the same sign — no counter-rotation. The head contribution
   itself stays same-signed with the face aim. Retain original warm-up, settling and hold windows and accurate aim
   under the existing applicable <2–3° oracle; skull-base counter-extension is a defect.

### Technical Requirements

9. Contract and composition tests verify the `IKTargetIntentProvider` subclass under `AlleyCat.Mind.Attention`,
   direct Mind-child placement, no `IComponent` or `Character.Components` membership, NPC-only template composition,
   and installer wiring into `CharacterIK.HeadTargetIntentProvider`.
10. State-machine tests verify saturation engagement, sustained centring within the comfort cone, glance
    classification from continuous assignment timing with reset on anchor change or clear, in-cone head-hold, minimal
    residual for brief out-of-cone glances, glance-end return, release hysteresis, envelope edge clamping, and
    clear-to-neutral with zero influence.
11. Timing tests verify that the default centring delay exceeds the configured AI-007 secondary dwell and pin the
    centring-delay boundary behaviour.
12. Reference tests verify authored envelope independence from idle animation/solved output, root transport and actual
    solved-face-local comfort geometry, including coupled axes, constrained solves and partial influence. Stationary
    targets reached through different histories must not absorb solved pose into upstream references.
    (TRACES TR-9, TR-14, TR-20)
13. Purity tests verify the controller introduces no changes to `EyesBehaviour`, `EyesController`, saccades, blinking,
    the AI-007 selector, or the IK-001 solver (eye–head synchronisation changes are owned by VISION-001); that the
    controller never calls `SetLookTarget` or `ClearLookTarget`; and that the head tracks the anchor world position,
    never saccade offsets.
14. Determinism and lifecycle tests verify delta-driven evaluation through the Godot-free seam, component-projection
    before Vision resolution, projection-safe rebinding, teardown to safe idle with influence 0, and settings
    validation including envelope-exceeds-comfort-cone per axis.
    Prove snapshot generation/sequence invalidation, inactive missing-input handling, marked bootstrap, prior-evaluation
    feedback transport, once-only advancement and zero-delta non-ageing in both scheduling paths. (TR-14, TR-15, TR-17)
15. Integration tests verify the roll-free intent construction: the published intent basis carries zero roll across
    frames even when the solved head is externally rolled. Release follows upstream roll-free pose, then native
    animation including roll at zero influence; re-engagement does not reuse stale held state. (TR-12, TR-19, TR-20)
16. Integration tests verify the fold-aware aimed intent origin: a settled neutral hold and a materially yawed
    lateral hold reproduce the independent rest/orbit construction exactly (D=U yields the upstream Head origin and
    the small-angle limit approaches zero displacement); a settled, fully engaged zero-yaw pure-downward command
    publishes exactly the selected fold-family member — separated from the geometrically excluded orbit target and
    matching the independently composed family member within the FK reconstruction bound, a witness specific to that
    fully engaged yaw-free case rather than a universal claim about blended or yawed output; and the published origin
    varies continuously across the engagement blend and yaw transport. At an equilibrium command the coupled gaze
    prediction's predicted eye origin equals the published origin's eye position under the authored Viewpoint offset,
    and a non-zero reachable stationary demand is published and tracked. Varying solved Head and Neck with constant
    upstream geometry and command leaves the target invariant, an unset or invalid pivot preserves the fallback
    semantics of Technical Requirement 22, and out-of-domain upstream poses or degenerate geometry fall back to the
    orbit construction with the reason reported. (TR-21, TR-25)
17. Isolated-fixture coverage on the reference female verifies the stall rule over the complete transit towards a
    sustained target: the longest full-freeze window outside the Technical Requirement 23 designed-dwell exclusions
    (glance dwell and saturation park) and distinct verified rest-at-limit exception measures under 150 ms, with
    recorded suppression red validation and dwell/residual boundary replays. Include one real-pipeline positive
    rest-at-limit case beyond centring eligibility with independently derived unreachable residual, correct bounded
    best-effort command under Technical Requirement 25 and actual angular attainment under the orientation-defined rule.
    Focused negative replays must each remain detected:
    a stalled approach to that limit, a reachable target and a wrong/stale command despite limit occupancy. Replay a
    geometry/target/command change invalidating eligibility and verify resumed detection. Keep requested, mediated and
    solved positions as separate diagnostics, not eligibility gates or presumed positional proof.
    Keep complete raw freeze windows, per-sample eligibility evidence and excluded/non-excluded durations; neither
    dropping strain assertions nor trimming approach/invalidation samples establishes acceptance. (TRACES UR-9, TR-23)
18. Isolated-fixture stoop coverage asserts the head counter-bend guard technically: the head contribution is
    checked same-signed against the actual face pitch denominator — never a world Euler sum — alongside the
    pitch-share bounds of Criterion 8. (TRACES UR-10, TR-24)
19. Independent review accepts the bounded helper's minimal-change, comfort, envelope and roll-free constraints,
    deterministic feasible/infeasible results and near-target prediction before integration. Engine seam probes pass
    before shared API expansion; source inspection alone is insufficient. (TRACES TR-25, TR-26)

### Complete-Window Evidence Contract

- Preserve the original 40° eye reassignment, 135-frame 90 fps series with measured actual cadence, pre-assignment
  reference, exact assignment timestamps, first-presented-frame numbering and all baseline, acquisition, transit,
  centring and settled windows. Eye acquisition onset ≤2 presented frames, overshoot ≤4° and bilateral direct fixation
  error ≤0.5° against independently derived constrained directions, with saccades disabled, remain mandatory under
  [VISION-001 TR50–52 and Acceptance Criteria 66–70](../../vision/001-eyes/index.md#acceptance-criteria).
  Both eyes must complete acquisition within 0.4 s of actual assignment and maintain fixation thereafter under the
  [eye-acquisition decision](../../vision/001-eyes/index.md#acquisition-completion-decision). Target/head motion must
  not reset acquisition. Head centring ≤0.8 s remains mandatory and is not an eye-acquisition completion deadline.
  Apply VISION-001's normative
  [assignment/presentation ordering](../../vision/001-eyes/index.md#assignment-and-presentation-ordering)
  for frame 1 and boundary replays: eligibility follows rendered-state sampling after assignment, not callback time.
- Phase-align only bounded initial setup if needed. Glance return/clear occurs on its predefined schedule within 0.6 s
  of assignment, independently of centring. Record actual assignment/clear times and setup/transition excursions; no
  success-dependent waits may extend a glance. Observe complete post-draw windows through return and settling.
- Use immutable authored optical references for both eyes and independent direct fixation oracles under VISION-001.
  Test bounded, finite saccades and return to fixation separately, without requiring an exact random trajectory.
  Freeze/dwell measurement uses actual assignment events and pre-assignment pose under Technical Requirement 23.
- Validate a fixed matrix spanning shallow-to−40° stoop, frozen/animated upstream poses, mixed axes, near/far targets,
  approach histories, partial influence, clear/re-engagement and root rotation. Record actual face, composed joint
  contributions, aim, upstream/requested/mediated/solved positions and limit occupancy. Apply existing bounds where
  defined — including the authorised provisional playtest acceptance profile — and independent contract invariants
  elsewhere, not thresholds chosen after observing results. Strain fixtures must demonstrate genuine residual and eye
  contribution; a large authored angle alone is insufficient.
- At least three complete runs of the final meaningful candidate must show no degraded-state recurrence. Store immutable
  per-run evidence under `game/temp/`: commands/environment, candidate source/diff and assembly identity, event times,
  cadence, complete terminal results and captures. Never overwrite final artefacts or infer revision from timestamps.
- Retain affected/full required suites, shared-consumer checks, build/format and independent final review. Rendered
  visual evidence must show valid face/body framing and directions and cover arrival, transition and settling;
  endpoints alone cannot establish temporal correctness. Reviewers independently inspect key images and reconcile raw
  PASS/FAIL results with claims. Changed behaviour requires fresh candidate evidence.

## References

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-007: Attention-Driven Gaze Target Selection](../007-attention-gaze-target-selection/index.md)
- [VISION-001: Eyes](../../vision/001-eyes/index.md)
- [IK-001: Neck-Spine CCDIK Configuration](../../ik/001-neck-spine-ik/index.md)
- [IK Implementation Notes — IKTargetIntentProvider Contract](../../ik/implementation-notes.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [NAV-001: NPC Navigation](../../nav/001-npc-navigation/index.md)
- `game/src/IK/IKTargetIntentProvider.cs`
- `game/src/IK/CharacterIK.cs`
- `game/src/Mind/Attention/DownwardFoldOrigin.cs`
- `game/src/Mind/Attention/GeometricGazeSaturation.cs`
- `game/src/Vision/EyesLookMath.cs`
- `game/src/Vision/EyesController.cs`
- `integration-tests/src/Mind/Attention/IsolatedFemaleTrackingIntegrationTests.cs`
- `integration-tests/src/Mind/Attention/DownwardFoldAngularAcceptance.cs`
