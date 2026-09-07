---
id: RIG-002
title: Forearm Twist
---

# Forearm Twist

> **Removal status:** The wrist-swing helper and its bend-correction path are permanently removed. The removal is
> delivered in this change set: runtime, tooling, tests, and generated assets are twist-only. Backward
> compatibility is explicitly not required.

## Requirement

Deliver bilateral deform-only forearm-twist helpers that distribute wrist roll across the lower-arm-to-hand chain
without changing the canonical humanoid skeleton or established hand, animation, retargeting, IK, physical-rig,
and hand-target contracts. Wrist bend is not helper-driven: the normal hand bone chain remains authoritative and
no second helper bone exists.

## Goal

Reduce visible forearm candy-wrapper deformation during wrist roll while preserving the lower-arm-to-hand chain
used by gameplay and pose systems.

Wrist deformation under anatomical bending is an acknowledged unresolved cosmetic issue. This specification
delivers no remedy: the user's recorded expectation is that any future remedy uses corrective blendshapes rather
than another helper bone (see Out Of Scope).

## User Requirements

1. Neither arm may transiently deform at start-up or during an authority change. Valid animation, grab,
   live-tracking, and frozen-tracking wrist poses remain correct.
2. At zero IK influence, ordinary animation remains correct. A frozen last-valid optical wrist remains visibly
   authoritative until its owning source changes.
3. The unfixed `weight_000` twist control visibly collapses at user-confirmed held `135°` and `180°` roll poses.
   These are sampled static RED evidence, not evidence of unobserved between-frame motion. Fix the collapse across
   the same nine sampled twist poses, especially those held extremes, without near-point pinch on either side.
4. The helper never drives wrist bend. Anatomical hand and wrist bending happens only through the normal hand bone
   chain, which remains authoritative under bend and combined roll-with-bend poses. Residual wrist deformation
   under bending is an acknowledged unresolved cosmetic issue, not a delivery gate of this specification.
5. Runtime `TwistWeight = 0.50` is the interim default, not a mandatory release value; the user finds axial twist
   at `0.50` acceptable. Compare weight `0`, the selected runtime weight, and the earlier `0.25` candidate across
   female/male, left/right axial scenarios.
6. Final player-visible acceptance requires independent reviewer inspection of the captured images and explicit
   user visual approval. Neither source provenance nor an automated metric can replace that approval.
7. Ordinary regeneration preserves ambiguous bilateral source skinning on both physical sides. The known female
   right-hand finger correction must not cause other unproven wrong-side weights to move, nor may axial authoring
   move finger or unrelated ownership or sacrifice twist quality.

## Technical Requirements

1. Each `LeftLowerArm` and `RightLowerArm` has exactly one direct twist-helper child, named `LeftForearmTwist`
   and `RightForearmTwist`. The matching hand is the twist helper's direct child:
   `LowerArm → ForearmTwist → Hand`. No second helper bone exists on either side.
2. Twist helpers are deformation-only and remain outside `SkeletonProfileHumanoid`, BoneMap, animation
   retargeting, IK chains and endpoints, physical rigs, and hand targets.
3. Repository-owned generic MPFB source assets reside under `tools/mpfb/` and target Blender 5.2, MPFB 2.0.17, and
   MPFB asset schema `110`. Contributors manually install them before ordinary MPFB regeneration; generated
   `.blend` files are not corrected directly. Blender data, manifests, canonical welded IDs, and imported seam
   mappings are historical or generation provenance only. Schema-4 canonical welded provenance and imported-seam
   mapping are not a delivery prerequisite for runtime deformation quality.
4. Skinning uses smooth authored helper weights. Runtime `TwistWeight` is configurable from `0` to `1` and
   defaults to `0.50`; ownership `helper_fraction` starts at `0.5`. They may change independently within at most
   three axial configurations, with a ledger and user approval per selected candidate. A deformation change
   invalidates prior provisional axial approval, and the selected complete configuration requires final user
   visual approval; neither starting value is a fixed release mandate. Zero-weight controls and unchanged
   recaptures do not consume candidate slots; deformation-changing repairs do. Semantic profile-half and
   diagnostic `0.5` values are not automatically changed.
5. One bilateral runtime writer — the `ForearmTwistModifier` — is the sole runtime writer for both twist helpers.
   It is a direct `Skeleton3D` child, after canonical hand/copy/animation work and before the optical-last
   finger-retargeting modifier. Role templates do not add a duplicate.
6. Per side, the runtime writer derives the `LowerArm`-to-`Hand` rest-relative rotation and extracts the signed
   principal axial twist about the lower-arm rest axis. It applies the weighted axial twist to the twist helper
   and writes complete local transforms, not rotation-only poses. The swing component of the hand rotation is not
   driven anywhere: the writer never originates or alters the hand's authoritative global pose, and re-asserts it
   in the same pass after the helper write.
7. RIG-002 does not depend on IK types. The [IK-005](../../ik/005-target-pipeline/index.md) authority adapter
   submits same-pass side authority immediately before this modifier, after canonical hand/copy/animation work. A
   stale stamp fails closed. For an unready, stale, or invalid side, Technical Requirement 17 governs: a
   representable fallback preflights the complete helper rest and hand compensation before writing, then
   preserves the authoritative hand and unaffected side without advancing failed-side state. If preflight or
   post-write recovery fails, the character becomes unavailable rather than claiming a successful rest. An
   authority-epoch change resets only that side; optical finger retargeting remains last.
8. The approved anatomical wrist-bend fixture is posed in two steps:
   - Palm normal: the imported hand-rest local `+Z` axis, cross-checked against thumb, middle, and little-finger
     geometry. At T-pose rest it points downward, world `−Y`.
   - Subject forward: derived from skeleton geometry—`up = Hips → Head`, `right = Left → RightUpperArm`, and
     `forward = up × right`—then conjugated by the skeleton-node basis and asserted within tolerance of world
     `−Z`.
   - Pronation: rotate about the forearm longitudinal axis (`elbow → wrist`) by the signed nearest angle that
     brings the palm normal onto subject forward. Recorded angle observations are historical calibration, not
     hard-coded requirements.
   - Bend: flexion `+60°` and extension `−60°` about `longitudinal × palm-forward`; total hand rotation is
     `Swing(bend) × Twist(pronation)`. The wrist origin is invariant and the helper remains axial-only; the bend
     is authored on the normal hand bone chain.
   - Bend direction is derived from geometry: flexion moves the wrist-to-middle-finger direction towards the palm
     face and extension moves it away; axis labels alone are insufficient.
9. For a valid, ready, same-pass side, use skeleton-local affine transforms with column vectors and right-to-left
   composition. Let global rests `R_L`, `R_X`, `R_W` denote lower arm, twist helper, and hand; let `P_L` be the
   current upstream lower-arm global and `C` the earlier authoritative achieved hand global. Write
   `w = origin(R_W)` and `c = origin(C)`. All inverses and quaternion extraction must be representable:
   supported bases are positive-conformal (`sO`, `s > 0`, proper orientation `O`), checked before extracting
   rotations. Arbitrary shear and reflections are not implicitly supported. Invalid inputs follow Technical
   Requirement 17.

   In the lower-arm rest frame, use axis `a = normalise(B_RL⁻¹(w − origin(R_L)))`, where `B_RL` is the rest
   lower-arm basis. Let `D = rot(P_L⁻¹ × C × R_W⁻¹ × R_L)`, extracting the proper orientation from the supported
   basis; extract the signed principal axial twist `T` about `a`. At runtime weight `α`, use `Tα = slerp(I, T, α)`.
   These are zero-translation lower-rest-frame rotations; `helper_fraction` is skinning ownership only, not a
   runtime transform factor.

   Transport the axial rotation by `F = P_L × Tα × R_L⁻¹`. The complete global target is `G_X = F × R_X`; write
   the complete local transforms `L_X = P_L⁻¹ × G_X` and re-assert the authoritative hand local
   `L_W = G_X⁻¹ × C` in the same pass, so the hand's achieved global pose is unchanged by the helper write. With
   consistent inverse binds, the complete helper skinning transform is `G_X × R_X⁻¹ = F`.
10. Development-time skeleton tests prove transform and authority correctness, not per-vertex deformation
    quality. They verify axial-only twist, complete local writes, same-pass hand-global-pose re-assertion,
    per-side isolation on representable fallback, and declared authority ordering. Under anatomical bend and
    combined roll-with-bend poses they additionally prove the helper stays axial-only while the hand bends
    through its authoritative chain. They exercise the installation and per-pass failure boundaries in Technical
    Requirement 17: preflight before writes, complete rest and hand compensation when recoverable, and character
    unavailability when recovery fails. They observe production results before any test-authored pose overwrite.
11. Before mirrored cleanup, protect each source-present ambiguous bilateral row's complete physical group
    assignments on both sides. A wrong-side suffix, low weight, or vertex position alone does not establish
    exporter contamination. Cleanup may remove an independently identified exporter-introduced counterpart
    assignment, but must not transfer a positive source-present bilateral assignment without independent,
    row-specific justification. If no exporter-added assignment is evidenced, cleanup makes no speculative
    positive-weight transfer. The direct correction of female custom-source vertex 1945 is separately approved
    and bounded; it does not authorise corrections to other rows.

    After safe cleanup, recensus eligible rows on the resulting input and construct axial
    lower-arm/twist-helper/hand ownership only in the eligible same-side domain, including eligible rows with no
    original helper weight. Exclude protected bilateral rows from axial authoring unless an independently
    justified, approved narrow attribution resolves their ownership. Preserve their full source physical channels
    through output; protect finger, unrelated, opposite-side, and out-of-domain influences at each applicable
    stage. Snapshot the completed axial three-anchor distribution as the immutable ownership and import-validation
    reference. See the normative [Character Generator](../../tooling/character-generator/index.md) stage order.
12. Every `BoneAttachment3D` in an emitted or updated character template satisfies stored `bone_idx ↔ bone_name`
    agreement at load. When bone insertion shifts bone order, regeneration refreshes stored indices.
13. Preserve existing photobooth scenarios' cameras, environment, rest stance, pose convention, framing, and
    quality guards; keep their earlier captures and records at their original paths. Add separately identified
    twist diagnostic scenarios, each first probing the existing rest-arm pose without changing its cameras. If a
    static case does not expose a symptom, make at most one reasoned representative posed-arm or trajectory probe
    per defect. Compare axial candidates across the same nine sampled twist poses (neutral and signed `45°`,
    `90°`, `135°`, `180°`), keeping posed arm, camera, lighting, pose and timing matched across weights `0`, the
    selected runtime weight, and `0.25` when different, for female/male and left/right. For temporal symptoms,
    record the full neutral → movement/intermediate → held → return and settling window, not endpoints alone.
    Show the transverse wrist/forearm surface in roll; introduce another view only if the existing
    top-down/profile pair occludes the symptom, after focused review. Keep the imported mesh, hand authority,
    pose/trajectory and observation timing, camera projection/framing, and environment matched across weights.
    At weight zero the helper rests while the hand still moves; at selected weights the helper responds axially.
    Preserve capture-time skeleton assertions for helper rotation, hand-origin and global-pose preservation,
    canonical lower-arm stability, authority ordering, and non-blank images. When the anatomical bend fixture is
    captured, check bent fingertip direction independently of the commanded transform; such captures verify
    helper axial-only behaviour and hand authority, not cosmetic bend quality. These guards do not prove a
    visible defect.
14. New captures use `game/temp/RIG-002/runs/<run-id>/forearm_twist/<scenario>/<sex>/<side>/<view>_<weight>.jpg`.
    The caller supplies a safe, unique `<run-id>` (a single path component without separators or `..`) and an
    explicit create or join mode. Create initialises a new run with that identity and rejects a pre-existing run
    directory; join accepts only that already-initialised run after verifying its recorded identity matches the
    supplied ID. Reject an absent or unrelated run on join and any existing target image on either mode.
    Separate zero- and selected-weight invocations join the same run for a compared scenario, keeping files
    adjacent within its `forearm_twist/` subtree (for example `top_down_000.jpg` and `top_down_025.jpg`; likewise
    `profile_000.jpg` and `profile_025.jpg`). For temporal sequences, include a shared phase/frame identifier in
    `<view>` before the weight suffix. Use `000` for zero and `025` for `0.25`; for other strengths concatenate
    the canonical decimal integer and fractional digits, padding the fractional part to at least three places
    (for example `0.2501` → `02501`, `1` → `1000`). Reject ambiguous or colliding labels rather than rounding. Do
    not archive, migrate, relabel, or overwrite earlier images, records, or SHA manifests; preserve each
    historical manifest's mapping to its original image paths.
15. No production runtime path performs per-vertex or per-triangle deformation validation. Offline metrics may be
    retained only as optional diagnostic evidence. They are not primary acceptance, cannot silently relax a
    retained threshold, and cannot prove or reject current player-visible deformation quality.
16. The formerly mandatory imported-vertex audit, schema-4 canonical welded provenance/imported-seam mapping, and
    Blender/manifest studies are no longer delivery gates. Their recorded thresholds, tolerances, and outcomes
    remain historical or generation provenance; they neither prove nor reject current visual quality. This is an
    explicit replacement of those delivery gates, not a threshold adjustment.
17. Production templates with invalid helper/hand topology, rests, or bases refuse character installation before
    runtime. For an installed side with an invalid same-pass sample, candidate, or runtime bind, preflight
    whether the twist helper's complete local rest and the compensating hand local pose are representable before
    either write. If so, restore the helper to complete local rest in the same pass, preserve the earlier
    authoritative hand global pose, leave the unaffected side running, and do not advance the failed side's
    state. Report a structured warning for a recoverable runtime bind failure. If preflight fails or a
    write/readback cannot recover the required rest and hand pose, fail hard and make the character unavailable;
    do not count a no-write or stale visible helper as a successful rest. This fail-hard outcome requires Godot
    engine evidence before delivery, not an assumed atomic write operation.
18. The single twist helper is the only approved helper mechanism; the removed wrist-swing helper approach must
    not be reintroduced without a further specification change. Corrective blendshapes — the user's expected
    direction for the acknowledged residual wrist-bend deformation defect — dual-quaternion skinning, and other
    corrective-deformation or preserve-volume behaviour remain deferred future directions requiring a further
    specification change; no such work is promised. A previous bounded study stopped under its executed rules;
    that historical stop does not make its imported-vertex audit or metric gates current delivery requirements.

## In Scope

- Bilateral deform-only twist helper topology, generic MPFB authoring sources, smooth weights, and required
  regeneration safeguards.
- Authority-safe helper consumption, the anatomical bend fixture as a hand-authority check, skeleton-level
  development tests, and template bone-binding serialisation.
- Existing photobooth matrix, confirmed static twist RED, matched comparisons, independent image inspection, and
  user visual approval as the quality gate.

## Out Of Scope

- Additional helper segments or deformation systems beyond one twist helper per side, including any helper-driven
  wrist bend correction.
- The corrective-blendshape remedy for the acknowledged residual wrist-bend deformation defect: a deferred future
  direction only, requiring a further specification change, with no blendshape work promised.
- Changes to hand gameplay, arm-IK solving, animation retargeting, or physical-rig behaviour, except the required
  authority bridge and validation contracts linked above.
- Production runtime per-vertex or per-triangle deformation validation.
- The structural alternatives named in Technical Requirement 18 without a further specification change.

## Acceptance Criteria

1. **User Requirements 1–2:** Neither arm transiently deforms during start-up or authority changes. Valid
   animation, grab, live, and frozen wrist poses remain correct; zero IK influence preserves ordinary animation
   and the frozen last-valid optical wrist remains visibly authoritative.
2. **User Requirement 3:** Held `135°`/`180°` twist collapse is static RED, not proof of unsampled motion. On
   female/male × left/right, the final complete configuration resolves the collapse across all nine twist poses
   at the selected runtime weight without near-point pinch.
3. **User Requirements 5–6:** Female/male × left/right captures cover axial neutral and signed `45°`, `90°`,
   `135°`, `180°` plus combined roll-with-bend authority poses, comparing weight `0`, the selected runtime weight,
   and `0.25` when different. The provisional twist result at `0.50` is not joint approval; a deformation change
   invalidates it until the same nine poses are rechecked. An independent reviewer inspects the matched
   comparisons, then the user explicitly approves the complete configuration.
4. **User Requirement 4:** Under anatomical bend and combined roll-with-bend poses, the helper remains axial-only,
   no helper-driven bend exists, and the hand bends through its authoritative chain with its achieved global pose
   preserved. Cosmetic wrist deformation under bending is the acknowledged out-of-scope defect and is not judged
   by these gates.
5. **User Requirement 7 and Technical Requirements 4 and 11:** Focused tests prove pre-cleanup bilateral
   protection → justified scoped cleanup → recensus → axial authoring (including eligible zero-helper rows) →
   immutable axial snapshot. Source-to-export comparison checks retained source-present bilateral physical
   channels through output where correspondence is established; where correspondence is unavailable, do not claim
   row-level proof from suffix or position, and keep that row protected. No unproven positive wrong-side
   assignment is transferred. Tests distinguish independently evidenced exporter-added counterparts and the
   bounded female source-vertex-1945 correction from ambiguous source rows. They verify finger, unrelated, and
   out-of-domain protection, positive eligible zero-helper support, and axial construction conservation. The
   candidate ledger respects the three-axial-configuration budget; neither starting parameter is treated as a
   fixed release requirement.
6. **Technical Requirements 1–7 and 9–10:** Skeleton-level development tests prove the single-helper chain and
   exclusions, sole writer and ordering, axial-only twist, complete global and local writes, and same-pass hand
   re-assertion, using independent expected geometry rather than duplicating the implementation formula. Rest,
   actual hand global after writes, unaffected-side authority, and stale/unready/invalid fail-closed handling are
   independent invariants; observe results before test-authored overwrites. Invalid template topology, rests, or
   bases refuse installation.
7. **Technical Requirement 8:** Fixture tests prove the geometric palm-forward, pronation, flexion, extension,
   composition, wrist-origin, and bend-direction contracts without hard-coding historical calibration angles.
8. **Technical Requirement 12:** Generated or updated templates pass the `BoneAttachment3D` stored-index/name
   binding guard after helper-bone insertion.
9. **Technical Requirements 13–14:** Existing scenarios retain their cameras, environment, stance, poses,
   framing, capture-time skeleton assertions, and non-blank and mode-appropriate differing-image guards. Twist
   comparisons keep mesh, authority, pose, timing, and presentation equal across weights; any temporal claim
   additionally requires transition, hold, return, and settling evidence. Bend-role captures verify helper
   axial-only behaviour and hand authority, not cosmetic bend quality. New paired paths have collision-free
   weight labels; explicit create rejects an existing run, while explicit join verifies its identity across
   weight-specific invocations, with no target overwritten. Earlier image paths, records, and SHA manifests with
   their original image-path mappings remain intact. Pixel differences alone are not symptom evidence.
10. **Technical Requirements 15–16:** No production runtime performs vertex-level validation. Any retained
    offline metric is reported as optional diagnostic evidence only; historical Blender, manifest, and
    imported-vertex audit results are not represented as proof for or against current visual quality.
11. **Technical Requirement 17:** Representable bad samples, candidates, and runtime binds preflight both
    fallback poses before writing, restore the helper's complete rest, preserve the authoritative hand global
    pose and unaffected side, and leave failed-side state unchanged; recoverable bind failures emit a structured
    warning. Unrepresentable fallback or unrecoverable write/readback fails hard and makes the character
    unavailable, not rested. Godot engine evidence must demonstrate this last outcome before delivery.
12. **Technical Requirement 18:** The removed swing-helper approach is not reintroduced, and corrective
    alternatives, including blendshapes, remain deferred pending a specification change. Historical study
    evidence remains accurately labelled as historical, without imposing superseded metric or provenance gates.

### Requirement Traceability

- User Requirements 1–2: criterion 1.
- User Requirement 3: criterion 2.
- User Requirements 4–6: criteria 3–4.
- User Requirement 7: criteria 3 and 5.
- Technical Requirements 1–7 and 9–10: criterion 6.
- Technical Requirement 8: criterion 7.
- Technical Requirements 4 and 11: criterion 5.
- Technical Requirement 12: criterion 8.
- Technical Requirements 13–14: criterion 9.
- Technical Requirements 15–16: criterion 10.
- Technical Requirement 17: criterion 11.
- Technical Requirement 18: criterion 12.

## Verification And Evidence

IK-005 Technical Requirement 26 still says only a genuinely unready side rests its helper, whereas RIG-002
Technical Requirement 7 includes stale and invalid sides. This B1/S2 conflict requires scoped cross-spec
resolution and Godot engine proof before affected runtime work; the failure boundaries in Technical Requirement
17 do not resolve it. The helper in both texts is now the single twist helper; the contract changes with this
removal and validation follows in code.

The approved non-headless photobooth fixture is `game/tests/rigging/forearm_twist/forearm_twist_photobooth.gd`.
Existing images and records retain their original scenario/sex/side/`weight_{000,025}/top_down.jpg` or
`profile.jpg` paths; existing SHA manifests retain their original image-path mappings. New evidence follows
Technical Requirement 14:
`game/temp/RIG-002/runs/<run-id>/forearm_twist/<scenario>/<sex>/<side>/<view>_<weight>.jpg`. Supply a safe,
unique run ID: create it once, then explicitly join that same identified run for later weights. Reject a
pre-existing ID on create, an absent or mismatched identity on join, and any existing target image. Do not
overwrite, archive, or migrate earlier evidence. Existing contrast-guard failures are guard failures, not proof
of either symptom.

The user confirmed twist collapse at held `135°` and `180°` in
`game/temp/RIG-002/runs/twist_pose_diagnostic_20260923_direction_gl2/forearm_twist/posed_arm_twist_trajectory/`.
Its nine sampled poses are the static twist comparison window, not a continuous-motion observation. The
provisional weight-`0.50` axial result in
`game/temp/RIG-002/runs/axial_slot1_production_20260924_gl3/` covers the same geometry at nine twist poses, but
is not joint approval. These observations do not prove helper/bind causality for rendered imported vertices: a
bounded native Godot mesh-bake probe failed with `source mesh must have its skin registered with a valid
skeleton`. Do not resume real-vertex bake troubleshooting as a prerequisite or invent a shape threshold. Coder
and independent reviewer inspect images with `read`; the user judges the complete configuration.

Wrist deformation under anatomical bending is an acknowledged unresolved cosmetic issue: with helper skinning
ownership active, the user observes residual wrist collapse under bending, and this specification delivers no
remedy. The user's recorded expectation is that any future remedy uses corrective blendshapes rather than another
helper bone; that direction is deferred (Out Of Scope, Technical Requirement 18) and no blendshape work is
promised. Visual ledgers produced under the removed swing-helper bend-correction approach are superseded by this
twist-only contract; git history retains them.

Stage 4 and Stage 4B reports, declarations, and evidence under `game/temp/RIG-002/phase-2/` and
`game/temp/RIG-002/stage-4/` are immutable historical evidence of a prior experiment under its executed rules.
Blender provenance, manifests, and imported-vertex audit results remain historical or generation provenance only.
They do not prove or reject present visual quality and do not block capture, independent review, or user approval
of the frozen photobooth matrix.

## References

- [CHAR-001: Character Skeleton Profile](../../character/001-character-skeleton/index.md)
- [Character Generator](../../tooling/character-generator/index.md)
- [IK Implementation Notes](../../ik/implementation-notes.md)
- [IK-002: Arm And Shoulder IK System](../../ik/002-arm-shoulder-ik/index.md)
- [IK-005: Target Pipeline](../../ik/005-target-pipeline/index.md)
- [XR-002: Optical Hand Tracking](../../xr/002-optical-hand-tracking/index.md)
