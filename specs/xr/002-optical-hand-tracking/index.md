---
id: XR-002
title: Optical Hand Tracking
---

# Optical Hand Tracking

## Purpose

Define optional optical (controller-free) hand tracking for the player avatar, delivered as a global bilateral
hand-pose mode on top of standard Godot 4.7 OpenXR hand-tracker APIs. Covers the optical wrist feeding the existing
VRIK hand-target pipeline and a player-only finger retargeting modifier that maps finger motion through constrained
anatomical models: the hardware-accepted signed hinge and roll-free swing mapping for non-thumb chains, and an
authored-animation reference model for thumb axes. Authored hand poses retain mode-conditional authority.

## Requirement

Deliver a global hand-pose mode that commits to either `Controller` or `Optical` for both hands at once, ingests the
standard OpenXR hand trackers, drives the existing VRIK hand-target pipeline from the calibrated optical wrist, and
retargets tracked finger rotations onto exactly the 30 canonical finger bones through a player-only
`SkeletonModifier3D`, transferring only motion the avatar can represent: signed hinge flexion at the non-thumb
intermediate and distal joints, a roll-free flexion/spread directional swing at the non-thumb proximals, and a thumb
model whose independent destination axes and roll-free metacarpal frame derive from static Blender-authored neutral
and soft-fist references. Optical tracking overrides authored finger poses only while the committed mode is `Optical`;
controller calibration anchors, controller input surfaces, and all existing hand gameplay semantics remain unchanged.
The global mode and per-side sources must be exposed through the runtime-agnostic boundary defined in XR-001 with
deterministic mock support.

## Goal

Let players present their real hands — including individual fingers — without holding controllers, while preserving
the existing ownership boundaries: VRIK exclusively owns wrist/hand/arm solving, INTR-003 owns authored finger poses
outside optical mode, and controller consumers are unaffected. Tracking must degrade safely (freeze affected poses and
retain the committed mode) rather than flip-flopping between sources. Finger retargeting deliberately discards
longitudinal roll and off-hinge tracker components so motion the avatar cannot represent never accumulates into
visible twist or splay. Stage 1 also discards the thumb metacarpal's axial opposition roll.

## User Requirements

1. When the XR runtime provides optical hand tracking and both hands are unambiguously tracked, the player's in-game
   hands and fingers follow their real hands instead of the controllers.
2. The committed mode switches only when both hands agree on the same non-ambiguous source, so hands do not jump
   between sources due to momentary tracking noise or disagreement.
3. While optical mode is active, losing tracking for one hand freezes that hand at its last valid pose; the other hand
   continues working, and held-object behaviour is unchanged.
4. When both hands later unambiguously report controller input, hand control returns to controllers.
5. Outside optical mode, authored hand-pose animations (including grab poses) behave exactly as before; optical
   tracking never mutates authored animations or AnimationTree state.
6. Hand gameplay behaviour — collision, obstruction handling, grabbing, and holding — is identical regardless of the
   active mode; optical mode adds no new interaction semantics.
7. Optical hand tracking enables automatically when the runtime capability/mode is available; no player-facing
   setting or UI is required.
8. Controller-driven gameplay (for example locomotion, grab buttons, and transcription input) is unaffected by optical
   hand presentation.
9. When the tracked non-thumb fingers are straight and together, the avatar avoids an exaggerated directional fan while
   retaining the accepted neutral alignment and slight natural bend authored into the rig.
10. Both hands repeatedly curl into and open from a fist without flexion-dependent cumulative lateral phalange twist or
    fingertip bunching. Opening returns consistently to the accepted straight/together neutral.
11. Deliberate finger spread remains signed, visible, and useful on both hands. Straight, fist, and spread poses do not
    require the player to adopt the avatar rig's authored rest orientation when optical mode starts.
12. Partial curl combined with deliberate spread remains coherent on both hands: fingers keep bending in their natural
     bend planes while spread is retained, and releasing the pose returns to the accepted neutral.
13. The calibrated thumb rests at the avatar's authored natural thumb pose when the player's real thumb is relaxed.
    Stage 1 maps opposition palmward but deliberately discards thumb-metacarpal axial opposition roll. A fist may
    therefore retain an open thumb web/V; this is an accepted Stage 1 limitation. Neutral, spread, and non-thumb
    finger behaviour remain unchanged.

## Technical Requirements

### Global Hand-Pose Mode

1. Exactly one committed global hand-pose mode exists at a time: `Controller` or `Optical`. The initial committed mode
   is `Controller`.
2. Each side publishes a per-side observation derived from the runtime: an unambiguous `Controller` proposal, an
   unambiguous `Optical` proposal, or an ambiguous/no-sample observation.
3. The committed mode switches only when both the left and right per-side observations agree on the same
   non-ambiguous proposal. On disagreement or ambiguity, the prior committed mode is retained.
4. Bilateral mode state table (symmetric left/right cases collapse into one row):

   | Committed Mode | Observations (Left, Right) | Result |
   |----------------|----------------------------|--------|
   | `Controller` | Both sides unambiguously `Optical` | Commit `Optical`. |
   | `Controller` | Both sides unambiguously `Controller` | Retain `Controller` (no change). |
   | `Controller` | Any disagreement, ambiguity, or missing observation | Retain `Controller`. |
   | `Optical` | Both sides unambiguously `Controller` | Commit `Controller`. |
   | `Optical` | Both sides unambiguously `Optical` | Retain `Optical` (no change). |
   | `Optical` | Any disagreement, ambiguity, or optical sample loss | Retain `Optical`; affected poses freeze. |

5. Loss of one or both optical samples while committed `Optical` does not change the committed mode; affected poses
   freeze at their last valid values.

### Standard APIs

6. The implementation must use standard Godot 4.7 OpenXR surfaces only: `XRServer`, `XRHandTracker`, and the
   `/user/hand_tracker/left` and `/user/hand_tracker/right` trackers. Excluded dependencies and features are listed
   under Out Of Scope.

### Optical Wrist To VRIK

7. In optical mode, the selected calibrated wrist enters the existing VRIK hand-target pipeline only by replacing the
   fallback source intent at the `XRHandPoseTargetProvider` seam. It then flows through the existing target
   contributors, physical actuation, collision/obstruction handling, grab override, and VRIK unchanged. No new
   interaction semantics are introduced.

### Calibration

8. The existing controller calibration anchors (`RightController/HandPosition`, `LeftController/HandPosition` in
   `game/assets/xr/openxr_runtime.tscn`) must remain unchanged.
9. Optical mode requires separate authorable per-side full `Transform3D` calibration anchors, because controller-grip,
   OpenXR palm/wrist, IK-target, and imported-skeleton frames differ.
10. Optical output contract: the tracked wrist is converted to a calibrated world-space wrist transform with the
     XROrigin transform, reference-frame adjustment, recentring, and non-unit world scale applied exactly once.

### Finger Retargeting

11. Finger retargeting is one player-only custom `SkeletonModifier3D`. Godot's built-in `XRHandModifier3D` must not be
    used because it writes the hand/wrist bone and lacks the required source-relative filtering.
12. The modifier owns exactly the 30 canonical `SkeletonProfileHumanoid` finger bones — 15 per side:
    - Thumb: `<Side>ThumbMetacarpal`, `<Side>ThumbProximal`, `<Side>ThumbDistal`
    - Index: `<Side>IndexProximal`, `<Side>IndexIntermediate`, `<Side>IndexDistal`
    - Middle: `<Side>MiddleProximal`, `<Side>MiddleIntermediate`, `<Side>MiddleDistal`
    - Ring: `<Side>RingProximal`, `<Side>RingIntermediate`, `<Side>RingDistal`
    - Little: `<Side>LittleProximal`, `<Side>LittleIntermediate`, `<Side>LittleDistal`

    `<Side>` is `Left` or `Right`.
13. The modifier must never write hand, wrist, or arm bones, bone positions, bone scale, or global poses. VRIK
    exclusively owns wrist/hand.
14. Retargeting writes complete parent-local Godot 4 pose rotations through `SetBonePoseRotation` while preserving the
    tracked hand's anatomical-parent topology. Non-thumb mapping is a constrained anatomical transfer per
    Requirements 17–23 and thumb mapping is the authored-animation reference model per Requirements 24–29; a general
    full-quaternion fit must not be applied, and no finger may be written as a direct source-relation copy.
    - Intermediate (PIP) and distal (DIP) destinations receive signed hinge flexion about the shared per-hand hinge
      axis `H` only. Off-hinge swing and longitudinal roll from the source are deliberately discarded.
    - Proximal destinations receive flexion plus deliberate spread as one roll-free directional swing. Source roll
      about the tracked longitudinal direction is discarded.
    - For each non-thumb finger, Proximal uses Metacarpal→Proximal (never Wrist→Proximal), Intermediate uses
       Proximal→Intermediate, and Distal uses Intermediate→Distal. The non-thumb metacarpals are source-only parents
       and are never written. An invalid metacarpal therefore freezes only its corresponding proximal destination;
       there is no wrist fallback. The thumb uses Wrist→Metacarpal, Metacarpal→Proximal, and
       Proximal→Distal source relations, mapped through the constrained thumb model of Requirements 24–29 —
       never a direct source-relation write.
    - Every destination accepts a sample only when its source joint and required anatomical parent pass the provider
      validity gate.
15. Modifier ordering: the finger retargeting modifier runs after VRIK hand-copy modifiers in skeleton modifier
    execution order (child order; see [IK Implementation Notes](../../ik/implementation-notes.md)).
16. The modifier topology is player-template only (see
    [CHAR-001: Character Skeleton Profile](../../character/001-character-skeleton/index.md)); base and NPC templates do
    not include it.

### Anatomical Frames

17. The non-thumb source frame is the delivered hand-joint convention evaluated in the neutral child frame produced by
    `Delta = S0_j⁻¹ × S_j`: longitudinal `l_s = +Y`, palmward `b_s = +Z`, and flexion hinge `h_s = +X`, satisfying
    `h_s × l_s = b_s`. These axes apply unchanged to both hands; no side-specific source inversion is introduced.
    The accepted implementation model is
    `reference-female-quest3-wivrn-authored-thumb-axes-v1`: the delivered local `+Y` points towards the fingertip,
    local `+Z` points palmward for curl, and flexion is a positive twist about local `+X`. The WiVRn/Godot delivery
    layer therefore differs algebraically from the raw-OpenXR `(-Z, -Y, -X)` convention; applying that raw convention
    directly inverts the extracted hinge sign. This is semantic profile provenance only, not a raw capture, trace,
    replay, screenshot, mesh, or hardware-validation claim.
18. At skeleton binding, the modifier derives one shared destination hand frame per hand from
    `Skeleton3D.GetBoneGlobalRest()` plus the accepted chain-neutral swings of Requirement 43
    (`FingerRestNeutralMath`'s middle-proximal target contract):
    - `L` is the desired middle proximal direction.
    - `R` is the desired index proximal root minus the desired little proximal root; `R_perp = R − L·(L·R)`;
      `H0 = normalise(R_perp)`.
    - For each swung chain, take the component of its intermediate segment direction perpendicular to `L`, normalise
      it, and form the deterministic equal-weight resultant `B_curv` of the four valid curvature directions.
    - Choose `H = ±H0` so that `(H × L)·B_curv > 0`, then define `B = H × L`. The frame is right-handed:
      `L × B = H`, `B × H = L`, `H × L = B`. Positive rotation about `H` moves `L` palmward towards `B`.
    - The desired neutral global bone orientations `Q'_j` are the swung global rests produced by Requirement 43.

    The effective `N_j`, the desired globals, and both per-hand frames are staged into the single bilateral skeleton
    binding transaction of Requirement 26 and become usable only when that transaction publishes; otherwise binding
    fails closed with no runtime writes.
19. Frame binding fails closed unless all inputs are finite and:
    - the projected root span (magnitude of `R_perp`) divided by the mean proximal segment length is at least `0.5`;
    - every contributing natural bend is at least `2°`;
    - all four curvature directions are available;
    - the resultant concentration (magnitude of the equal-weight mean of the unit curvature directions) is at
      least `0.8`;
    - every curvature direction lies within `35°` of the resultant; and
    - the final frame is unit, orthogonal, and right-handed within the existing numerical tolerances.

    The natural bend of a chain is the angle between its swung proximal and swung intermediate segment directions.
    A rejected hand disables finger retargeting for that skeleton without partial publication and without arbitrary
    basis, world-axis, previous-frame, animation-derived, or synthetic-tip fallback. Reference-female evidence passes
    these gates with a projected span of `62.241 mm`, natural bends of `7.228–12.577°`, concentration `0.951`, and
    maximum curvature disagreement of `29.064°`.

### Runtime Anatomical Mapping

20. For each mapped source relation, derive `S = inverse(Q_parent_world) × Q_joint_world` from the tracked world
    orientations and `Delta = inverse(S0_j) × S`, where `S0_j` is the immutable, hardware/source-neutral profile
    neutral from a validated optical capture. Normalise and hemisphere-align `Delta` to identity before extracting
    anatomical motion.
21. PIP and DIP destinations receive signed hinge flexion only. For unit `Delta = (v, w)` and source hinge `h_s`:

    ```text
    p = dot(v, h_s)
    m = sqrt(w² + p²)
    q_twist = (h_s · p/m, w/m)
    theta = 2 · atan2(p/m, w/m)   // in (−π, π]
    ```

    When `m` is degenerate, reject only that destination (freeze per Requirement 35). Express the shared destination
    hinge locally per bone as `h_d,j = inverse(Q'_j) × H` and write `D_j = N_j × rotation(h_d,j, theta)`, where `N_j`
    is the rest-derived effective neutral from Requirement 43. Off-hinge swing and longitudinal roll are deliberately
    discarded. The distal inherits the same global `H`; no destination tip is fabricated.
22. Proximal destinations receive one roll-free directional swing. With the tracked longitudinal direction
    `d_s = Delta × l_s` (source roll discarded) and components `x = dot(d_s, l_s)`, `y = dot(d_s, b_s)`,
    `z = dot(d_s, h_s)`:

    ```text
    l_d = inverse(Q'_p) × L
    b_d = inverse(Q'_p) × B
    h_d = inverse(Q'_p) × H
    d_d = normalise(x·l_d + y·b_d + z·h_d)
    R_p = shortest_arc(l_d, d_d)
    D_p = N_p × R_p
    ```

    Parallel handling is deterministic; an antiparallel or degenerate direction fails and freezes that proximal only
    (Requirement 35) rather than inventing a roll axis.
23. `Delta = identity` must reproduce the effective `N_j` exactly for every non-thumb destination, so the
    hardware-accepted natural neutral bend is preserved and straight/together tracked fingers hold the accepted
     neutral. Requirements 17–23, their source axes and equations, and every non-thumb effective `N_j` remain
     unchanged.
    Non-thumb binding and runtime mapping must not read or derive state from the authored thumb references.

### Thumb Anatomical Model (Stage 1)

24. The thumb source frame uses the same delivered hand-joint convention as Requirement 17: local `+Y` is the
    longitudinal towards-tip direction, local `+X` is the flexion hinge, and the axes apply unchanged to both hands
    with no side-specific source inversion. Stage 1 transfers the swing and hinge components and deliberately discards
    the metacarpal axial roll.
25. Thumb binding samples two immutable Blender-authored single-frame pose `Animation` resources directly: the
    `Reset` neutral reference and the `Grab-pipe-10` flexion reference. The consumer exports two general,
    deliberately non-thumb-prefixed properties:

    ```csharp
    [Export]
    public string AuthoredNeutralReferenceAnimationPath { get; set; }
        = "res://assets/characters/reference/female/animations/Reset.tres";

    [Export]
    public string AuthoredFlexionReferenceAnimationPath { get; set; }
        = "res://assets/characters/reference/female/animations/Grab-pipe-10.tres";
    ```

    The binding contract is direct, immutable, and strict:
    1. Both configured paths must be non-empty project-relative paths that resolve to `Animation` resources, loaded
       directly with `ResourceLoader`. A resource is never registered on a live animation node: there is no
       `AnimationPlayer` resolution, no `AnimationLibrary` registration, and no playback dependency.
    2. Sampling never calls `Play`, `Seek`, or `Advance`; never mutates tracks, keys, an `Animation`,
       `AnimationPlayer`, `AnimationTree`, mixer parameter, skeleton pose, or any other resource; and never reads
       live pose state.
    3. Exactly one enabled `Rotation3D` track must resolve for each required canonical bone path
       `%GeneralSkeleton:<CanonicalBoneName>` (for example `%GeneralSkeleton:LeftThumbMetacarpal`), matched exactly
       with no fuzzy, suffix, basename, or case-insensitive matching. The flexion reference requires the six thumb
       bones; the neutral reference additionally requires the Reset forward-kinematics set of Requirement 28 — wrist,
       hand, the four non-thumb proximal roots, and any animated root-to-hand ancestor, per side.
    4. Every required track must hold exactly one key — key `0` at `t=0` — read directly through the resource track
       API with no interpolation.
    5. Binding fails closed with the side, bone, resource, and violated contract identified on: a
       missing resource; a missing, duplicate, wrong-type, or disabled required track; a wrong key count or key
       time; or an invalid quaternion — non-finite, near-zero, or failing `|length² - 1| ≤ 0.001`.
    6. No silent "final key" selection or `Animation` length semantics apply to multi-key clips.

    Pinned asset facts: both current references are 57-track resources of length
    `0.041666668 s` with exactly one key per track at `t=0`, and imported left/right values are exact bilateral
    mirrors — right is `(x, -y, -z, w)` of left, modulo the quaternion double-cover storage sign on the thumb
    metacarpal.
    - Each side must own exactly the three canonical thumb bones, parented in a chain beneath the hand bone, matching
      Requirement 14. Imported thumb rest rotations must be finite; they do not supply `N_j` (Requirement 29
      sources it from the Reset key), but `ThumbRestBasisMath` retains its scale-tolerant polar decomposition
      as a binding qualification: column-scale ratio at most `1.5`, per-axis deviation from mean scale at most
      `25%`, no unsupported shear, and positive determinant.
26. For every thumb joint `j`, binding reads the absolute parent-local Reset key `R_j` and flexion key `F_j` and
    derives, with `hemisphere_align(q, r)` returning `q` or `-q`, whichever has non-negative dot product with `r`:

    ```text
    F'_j = hemisphere_align(F_j, R_j)
    A_j  = normalise(inverse(R_j) × F'_j)
    alpha_j = 2 · atan2(length(A_j.xyz), A_j.w)      // must be at least 2°
    a_j = normalise(A_j.xyz)
    ```

    `a_j` is the independent authored destination axis. `alpha_j` is reference metadata only, never gain; proximal
    and distal hinge gain stays exactly `1` with no scalar cap or response clamp (Requirement 27). The
    reference-female asset oracle is pinned:

    | Joint | Left Axis | Right Axis | Angle |
    |-------|-----------|------------|------:|
    | Metacarpal | `(-0.1770, +0.5991, +0.7809)` | `(-0.1770, -0.5991, -0.7809)` | `20.86°` |
    | Proximal | `(+0.7825, +0.5460, +0.2994)` | `(+0.7825, -0.5460, -0.2994)` | `23.62°` |
    | Distal | `(+0.7651, +0.4835, +0.4254)` | `(+0.7651, -0.4835, -0.4254)` | `35.07°` |

    Local authored axes mirror bilaterally as `a_left_expected = J · a_right` with `J = diag(+1, -1, -1)`, within an
    angular residual of `0.1°`, a component norm of `1e-4`, and a reference-angle difference of `0.1°`, all measured
    after hemisphere alignment.

    Skeleton binding is exactly one all-or-nothing bilateral transaction — the single publication boundary for all
    binding-derived state:
    1. Previously published binding state is cleared or invalidated when the transaction opens.
    2. Profile resolution (Requirements 44–45); the non-thumb neutrals, desired globals, and per-hand frames
       (Requirements 18–19 and 43); the Reset-sourced thumb neutrals (Requirement 29) and the `ThumbRestBasisMath`
       qualification (Requirement 25); both loaded reference resources and every sampled key; all six authored axes
       and reference angles; both metacarpal frames (Requirement 28); and every bilateral validation are staged in
       temporary buffers.
    3. Publication happens once, only after every mandatory gate passes.
    4. On any failure nothing publishes and no finger writes occur: non-thumb state never becomes usable through a
       separate earlier publication, and a thumb failure voids the whole binding rather than a thumb-only subset.
    5. Granular per-destination freeze (Requirements 29 and 35) applies only after a successful binding.
27. Thumb proximal and distal mapping retain the existing Requirement 20 `S`/`Delta` machinery, source relations from
    Requirement 14, and `theta_j` as the signed physical hinge angle extracted from source `Delta` about the
    delivered source `+X`. For each joint independently:

    ```text
    D_j = N_j × rotation(a_j, theta_j)
    ```

    Proximal and distal keep independent animation-derived axes; they never share an authored axis or hinge, and no
    destination thumb tip may be synthesised. A degenerate extraction freezes only the affected destination. Positive
    and negative `theta_j` remain unclamped with gain exactly `1`; `Delta = identity` must reproduce `N_j` exactly.
28. Thumb metacarpal mapping constructs a complete roll-free bend/splay frame from the authored references, entirely
    in deterministically derived state and never from the live skeleton pose:
    1. **Named frames.** `S` is the skeleton-local global space (matching `Skeleton3D.GetBoneGlobalRest()`); `P_b` is
       the absolute parent-local pose frame of bone `b`; `R_b` is the Reset bone frame; `H` is the Reset hand-local
       frame. Every authored metacarpal frame vector used at runtime is expressed in the Reset metacarpal right-local
       factor — the coordinate system on the right of `N_meta` in `D_meta = N_meta × R'` (clause 7's gained
       swing).
    2. **Reset forward kinematics.** Reset keys are complete absolute parent-local rotations and are never multiplied
       by imported rest rotations:

       ```text
       Q_b^R = Q_parent(b)^R × q_b^R
       p_b^R = p_parent(b)^R + Q_parent(b)^R × o_b        (o_b = GetBoneRest(b).origin)
       ```

       Per side, the root-to-hand ancestors, the wrist, the hand, the four non-thumb proximal roots, and the three
       thumb bones must have exact Reset rotation tracks; a non-animated ancestor may contribute its imported
       rest-local polar rotation. Binding fails if relevant position or scale channels would affect any queried
       origin unless that case is explicitly supported. The current references qualify: finger and hand origins are
       not position-animated, the proximal roots are direct children of the hand bone, and the thumb-proximal scale
       track is downstream of every queried point.
    3. **Reset hand-local palm plane.** With all points mapped into `H` by the kinematics above (`P_index` to
       `P_little` are the four non-thumb proximal root origins; the hand origin is `0`):

       ```text
       C = (P_index + P_middle + P_ring + P_little) / 4
       P = midpoint(Wrist_H, 0)
       u = normalise(C - P)
       t = normalise((P_index - P_little) - u·dot(u, P_index - P_little))
        n_palm,H = sigma_side × normalise(t × u)     sigma_Left = -1, sigma_Right = +1
        n_palm,meta = inverse(q_meta^R) × n_palm,H
        ```

        The cross-product order `t × u` is normative. The palmward sign is never selected from the flexion
        reference's geometry, thumb-to-index geometry, world axes, or previous frames. The constants are
        calibrated on the reference female — capture-era rest geometry plus Reset keys — so the normal points
        palmward: `n_palm` agrees with the hardware-accepted non-thumb per-hand frame palmward
        axis `B` (Requirement 18) within ≈ `19.5°`, with exact bilateral mirror residuals of `0.0°`. On
        destination skeletons without a dedicated wrist bone, the "wrist" anchor is the FK origin of the hand's
        parent bone — on the reference female, `LeftLowerArm`, an elbow-class origin — with a measured effect
        on the palm plane of ≤ `0.01` on the alignment dot.
    4. **Metacarpal frame.** In Reset metacarpal local space, with `a_meta` the authored metacarpal axis of
       Requirement 26:

       ```text
       l = normalise(o_thumb_proximal)
       h_raw = a_meta - l·dot(a_meta, l)
       rho = length(h_raw)                              // must be ≥ sin 35° = 0.573576436
       h = h_raw / rho
       b = h × l
       ```

       `l` is equivalently `normalise(inverse(Q_meta^R) × (p_prox^R - p_meta^R))`. There is no per-side flip of
        `b`; the palm and soft-fist gates validate its sign. This frame is not a thumb-to-index frame, not a
        segment-centre/curvature frame, not a geometry hinge shared across thumb bones, not canonical `+X`,
        and not a synthetic-tip frame. Only the metacarpal uses this projection; proximal and
       distal keep their independent animation-derived axes (Requirement 27).
     5. **Validation gates** (inclusive thresholds). Binding fails closed unless every consumed direction is finite
        with squared length `> 1e-10`; parallel handling is deterministic for dot `≥ 1 - 1e-6` and antiparallel
        dot `≤ -1 + 1e-6` is rejected; every consumed quaternion satisfies `|length² - 1| ≤ 0.001`; every authored
        angle is `≥ 2°`; the palm projected span divided by mean proximal length is `≥ 0.5`; with
        `n_perp = n_palm,meta - l·dot(n_palm,meta, l)`, the degeneracy floor `|n_perp| ≥ sin 20°` holds — a thumb
        longitudinal lying nearly in the palm plane fails closed because the projected normal is ill-defined —
        and the normalised palmward gate is `dot(b, normalise(n_perp)) ≥ 0.8`; the soft-fist swing `beta` is
        `≥ 2°`, where `d_flex = normalise(A_meta × l)` and `beta = acos(clamp(dot(l, d_flex), -1, 1))`; the
        movement alignment `dot(normalise(d_flex - l·dot(l, d_flex)), b) ≥ cos 35° = 0.819152044` and
        `dot(d_flex, b) > 0`; and the `(l, b, h)` basis has unit, pairwise-orthogonality, and determinant
        errors `≤ 1e-4`.

        The normalised gate measures the authored bend direction's palmward alignment within the plane
        perpendicular to the thumb longitudinal axis and is invariant to the thumb's out-of-plane opposition
        angle; the raw dot `dot(b, n_palm,meta)` is anatomy-capped by `sin theta(l, n_palm,meta)` because `b` is
        perpendicular to `l` by construction. Reference-female evidence: the normalised dot is ≈ `0.99958` on
        both hands (`theta(l, n_palm,meta) ≈ 47.8°`, raw ceiling `0.7407`).
    6. **Bilateral mirror contract.** In skeleton space the polar mirror is `M = diag(-1, +1, +1)` for positions
       and the polar vectors `l`, `b`, and `n`; axial vectors mirror as `h_left_expected = -M · h_right`; local
       authored axes mirror as `a_left_expected = J · a_right` with `J = diag(+1, -1, -1)`. Measured after
       hemisphere alignment, mirror residuals must be angular `≤ 0.1°`, component norm `≤ 1e-4`, and
       reference-angle difference `≤ 0.1°`.
    7. **Runtime source transfer — anchored hand-frame correspondence with a calibrated metacarpal response
       envelope.**
       The source swing direction is evaluated in the wrist frame as `d_w = S_meta × (+Y)` — the live
       wrist→metacarpal relation `S_meta = inverse(Q_wrist) × Q_metacarpal` (Requirement 20), normalised,
       applied to the source metacarpal `+Y` tip axis — then carried into the destination by measured
       hand-frame correspondence, anchored on the calibrated neutral, and scaled by the instantaneous anatomical
       response envelope:

       ```text
       d_w      = S_meta × (+Y)
       C_side   = B_dest × B_source⁻¹
       d_h      = C_side × d_w
       d_0      = Q0 × (N_meta⁻¹ × d_h)
       d        = normalise(d_0)                  // the anchored aim, unprojected
       axis     = normalise(l × d)                // the perpendicular component selects the live swing axis
       angle    = arccos(dot(l, d))               // full dot(l, d), never a renormalised projection
       b_axis   = dot(axis, b)                    // signed live swing-axis component in destination (l,b,h)
       h        = dot(axis, h_frame)              // h_frame is the destination frame's h vector
       c_b      = -b_axis                         // Left
       c_b      = +b_axis                         // Right

       S(x; a, b) = 0                             when x <= a
       S(x; a, b) = 3u² - 2u³                     when a < x < b, u = (x-a)/(b-a)
       S(x; a, b) = 1                             when x >= b

       g        = S(h; 0.400, 0.625) × S(c_b; 0.050, 0.150)
       k_eff    = 1 + (K_meta - 1) × g
       R        = rotation(axis, angle)           // the shortest arc from l onto d
       R'       = rotation(axis, k_eff × angle)
       D_meta   = N_meta × R'
       ```

       In the response equations, `h_frame` aliases the destination frame's `h` vector so scalar `h` is unambiguous;
       `b_axis` and `h` are the signed components of the normalised live swing axis in the destination `(l,b,h)` frame.
       The smoothsteps and their product match value and first derivative at every boundary, so the response is
        C1-continuous. It is instantaneous anatomical response only; no runtime diagnostic state or pose
        classification affects it. No scalar angle cap, gain clamp, or runtime fitting is permitted.

       - `B_source` is the source wrist anatomical basis (`h_s`, `l_s`, `b_s`) = (`+X`, `+Y`, `+Z`)
         (Requirement 24). `B_dest` pairs the destination binding hand frame of clause 3 with the source
         wrist axes: source `+Y` (distal) ↔ finger-distal `u_d = u`; source `+Z` (palmward) ↔ palmward
         normal `n_d = n_palm,H`; source `+X` ↔ thumb-side axis `t_d = sigma_t · t`. The pairing signs are
         measured and side-dependent — Left (`t: −1`, `n: +1`), Right (`t: +1`, `n: +1`), applied to `t`
         and `n_palm,H` — giving `det(B_dest) = +1` on both sides; improper pairings are measured broken
         (58–123° neutral residuals) and must not appear.
       - `Q0` is the pinned per-side rotation about `l` that lands the calibrated neutral direction — the
         neutral-window mean of `N_meta⁻¹ × d_h` — on `l` (measured Left `33.17°`, Right `22.34°`). It
         absorbs the rig-versus-hardware neutral orientation offset; because `d_w` reads the live relation
         directly and `Q0` absorbs the constant, the mapping is S0-drift-invariant (verified to `4.7e-06°`).
        - `K_meta` is the positive-direction ceiling of the metacarpal effective gain: Left `2.00`, Right `2.25`.
          The live effective gain is `k_eff`, bounded by the response law between `1` and `K_meta`. `Q0`, `K_meta`,
          and the four response thresholds are per-side profile records pinned under Requirement 45; both sides
          use the common thresholds shown above. `K_meta` is not a `K_j` record value. Every
         non-metacarpal hinge and swing gain stays exactly `1`, with no scalar cap or response clamp.

       The `N × R'` composition order is normative; `R' × N`, `N_meta × Delta`, and conjugation through
       `N_meta` remain incorrect and must not appear. At the calibrated neutral `d = l`, so `R = identity`,
       `R' = identity`, and `D_meta = N_meta` exactly — with `S0` the calibrated source neutral,
       `Delta = identity` reproduces the cached `N_j` for all six thumb joints (Requirement 27 contributes
       zero hinge angles), preserving the Requirement 29 identity-Delta→Reset-key contract unchanged.
       Source longitudinal roll is still discarded: a right-composed source `+Y` roll leaves `d_w` and the
       metacarpal output unchanged; the parallel branch is the neutral (`R' = identity`, `D_meta = N_meta`
       verbatim); an antiparallel or degenerate `d` fails that destination only and freezes per the
       Requirement 29 ladder.

29. Thumb freeze and sample semantics remain unchanged. For every thumb joint — metacarpal, proximal, and distal on
    both hands — the destination neutral `N_j` is the local rotation key sampled from the immutable Reset animation
    (the Requirement 25 neutral reference; `N_j` equals the sampled Reset key `R_j` of Requirement 26), never the
    bound skeleton's imported rest, never Requirement 43's chain-neutral swing, and never the flexion key. Reset is
    the authored definition of the desired visual thumb neutral — natural splay with slight curl — so identity Delta
    must land on it by contract rather than incidentally; non-thumb neutrals are unaffected and keep their
    Requirement 43 derivation. With `Delta = identity` at all three thumb joints of a hand, the written thumb
    rotations must equal the sampled Reset keys exactly, per joint and on both hands, independent of what the
     imported rest contains. On the reference female the Reset thumb keys are numerically equal to the imported
     local rest rotations, so sourcing `N_j` from the Reset key has no behavioural delta on the current rig;
     it discriminates only on rigs where the two differ. Whole-hand loss freezes the
    hand; an invalid wrist freezes metacarpal and proximal; an invalid metacarpal freezes metacarpal and proximal; an
    invalid proximal freezes proximal and distal; and an invalid distal freezes distal only. Thumb and non-thumb remain
    independently live. A thumb joint and required source parent pass the rotation-only gate when either
    `OrientationTracked` or `OrientationValid` is set and transforms are finite. Thumb rest origins, bases, and scales
    remain import-forensic evidence only; they do not define thumb axes. No general full `N_j × Delta` transfer may
    drive production.

### Mode-Conditional Authority

30. While optical mode is active, optical tracking fully overrides authored INTR-003 finger poses.
31. Outside optical mode, the modifier performs no writes, so authored AnimationTree hand poses remain authoritative.
32. Authored animations and AnimationTree state must never be mutated. A clear arbitration seam for future INTR-003
    integration must be preserved.

### Validity And Freeze Semantics

33. Sample acceptance is two-tier, using `XRHandTracker.HasTrackingData` and per-joint valid/tracked flags with
    finite transforms required in both tiers. The optical wrist capture (wrist and palm joints) keeps the strict
    actively-tracked tier — both joints must report `PositionTracked` and `OrientationTracked` — because wrist
    capture consumes positions. Rotation-only finger retargeting accepts a finger joint and its required source
    parent when either `OrientationTracked` or `OrientationValid` is set, because orientation-valid-but-inferred
    samples remain usable for rotation-only consumers whose positions are never consumed.
34. On optical-mode entry, the current authored local finger rotations are snapshotted only as the optical-session
    freeze cache. No mapping term consumes this snapshot. Each valid non-thumb sample uses the immutable profile
    `S0_j` (Requirement 44), the cached rest-derived effective `N_j` and per-hand frame (Requirements 18 and 43), and
    the current anatomical parent-relative source relation (Requirement 20). `S0_j` and `N_j` must never be captured
    from mode entry, the current animation, or any other live session pose. If a destination's calibration record is
    absent or invalid, that destination retains its authored entry snapshot for the session instead of using
    unnormalised direct mapping. The same fallback applies to an invalid source joint or required source parent. The
    cache and pinned profile values are cleared on mode exit/re-entry and skeleton rebind; the effective destination
    neutrals are re-derived only on skeleton rebind.
35. Valid joints update independently, with direct source-dependency freeze per chain: an invalid metacarpal freezes
    only its proximal destination; an invalid proximal freezes its proximal and intermediate destinations; an invalid
    intermediate freezes its intermediate and distal destinations; an invalid distal freezes its distal destination.
    Unrelated chains are unaffected. Joints that are invalid, or whose required source parent is invalid, reapply
    their cached rotation.
36. Whole-hand tracking loss freezes that hand; the other hand continues independently.
37. On mode exit, the optical-session cache is cleared so the current authored pose immediately regains authority.
38. The frozen wrist pose is cached in world space.

### Enablement And Runtime Abstraction

39. Optical hand tracking enables automatically when the runtime capability/mode is available. No player-facing
    setting or UI is in scope.
40. The global hand-pose mode and per-side hand-pose sources must be exposed through the runtime-agnostic boundary
    (`IXRRuntime`/`XRManager` per XR-001).
41. The mock runtime must provide deterministic hooks for committed-mode transitions and per-side sample injection
    (valid/invalid joints, finite/non-finite orientations, sample loss) so all mode and freeze behaviour is testable
    without hardware.
42. Controller input surfaces (`IXRHandController`) are unchanged; controller consumers (PlayerController,
    Transcriber) are unaffected.

### Resource Profile

43. At skeleton binding, the modifier must derive and cache the 12 effective non-thumb destination neutrals per hand
    from the actual destination `Skeleton3D` global rest geometry, expressed in skeleton-local global-rest space:
     1. Require four unique chains per hand with the exact topology Hand→Proximal→Intermediate→Distal, finite
        global-rest origins, and non-degenerate, orthogonal, uniform-scale, positive-determinant global-rest bases.
    2. For each chain `f`, derive its proximal direction from the proximal global-rest origin to the intermediate
       global-rest origin. Use that hand's middle proximal direction as the target.
    3. Derive a deterministic shortest-arc swing `A_f` from each proximal direction to the target. Parallel directions
       use identity. For antiparallel directions, project the proximal global-rest basis axes in fixed X, Y, Z order
       onto the plane perpendicular to the source direction, select the longest projection with first-axis tie-breaking,
       and use a π rotation around its normalised axis. The middle chain therefore receives identity swing.
    4. Apply the same `A_f` to the proximal, intermediate, and distal global-rest orientations. Do not independently
       align intermediate or distal directions, and do not invent a distal tip direction.
    5. Recursively convert the desired globals to complete parent-local Godot 4 pose rotations:
       `N_proximal = Q_hand_rest⁻¹ × (A_f × Q_proximal_rest)`,
       `N_intermediate = (A_f × Q_proximal_rest)⁻¹ × (A_f × Q_intermediate_rest)`, and
       `N_distal = (A_f × Q_intermediate_rest)⁻¹ × (A_f × Q_distal_rest)`.

    Applying one global swing per chain must preserve its internal relative rotations, curvature, bend plane, roll, and
    distal-to-intermediate relation. Derivation and runtime writes must not mutate roots or origins, lengths, positions,
    scales, skinning, imported rest, animations, or live pose channels. `N_j` must not be read from animations, live
    pose channels, OpenXR samples, `SkeletonProfileHumanoid`, BoneMap directions, `.blend` source geometry, or any
    session pose. Invalid topology, duplicate or missing bones, zero-length/non-finite proximal directions, or
    unsupported global rest bases must reject the binding and disable finger retargeting for that skeleton without any
    runtime writes.
44. The project owns an authorable, replaceable neutral-normalisation resource profile with explicit schema and
    profile version identifiers; schema `2` remains required. A valid profile is a single resource
    containing exactly 15 records per side (30 total): proximal, intermediate, and distal records for the index,
    middle, ring, and little fingers plus metacarpal, proximal, and distal records for the thumb. Every record
    supplies finite, normalised serialised values for `S0_j`, compatibility/provenance `DestinationNeutral`, and
    `K_j`. `K_j` is identity-only deprecated compatibility metadata for non-thumb and thumb records alike: the
    resolver must reject any record whose `K_j` is not identity — both quaternion hemispheres of identity are
    accepted — making the record invalid so its destination uses Requirement 34's freeze fallback, rather than
    silently ignoring the value. Thumb `DestinationNeutral` values mirror the Reset-sampled thumb neutrals of
    Requirement 29 as provenance; the runtime thumb `N_j` is the Reset key sampled at binding, never the imported
    rest. Non-thumb `DestinationNeutral` remains compatibility/provenance metadata only, with production using the
    rest-derived effective `N_j` from Requirement 43 and never applying both values. Loading validates the
    version, bilateral completeness, joint identity, uniqueness, quaternion finiteness, and quaternion
    normalisation before a record may drive a destination. Invalid or absent records use Requirement 34's
    per-destination freeze fallback; they must not fall back to unnormalised direct mapping. Resolved `S0_j`
    values are pinned and immutable for the optical session.
45. The default Quest 3 with WiVRn 26.6.2/reference-rig profile resource is
    `game/assets/xr/calibration/fingers_calibration_quest3.tres`; its filename is a locator, not provenance. It pins
    immutable `S0_j` values, `Q0`, `K_meta`, and response thresholds. Each resource preserves `SourceCaptureID` as
    the stable semantic identifier
    `reference-female-quest3-wivrn-authored-thumb-axes-v1`; `Provenance` describes that semantic profile only and
    must not name raw captures, trace files, campaigns, or replay runs. `K_j` remains identity-only deprecated
    compatibility metadata (Requirement 44); every hinge and non-metacarpal swing gain is exactly `1`.
    Thumb metacarpal effective gain follows Requirement 28's response envelope between `1` and per-side `K_meta`
    (Left `2.00`, Right `2.25`). The profile pins `Q0` (Left `33.17°`, Right `22.34°`) and common thresholds
    `h0 = 0.400`, `h1 = 0.625`, `b0 = 0.050`, and `b1 = 0.150`. No scalar cap, response clamp, runtime fitting, or
    promotion path to non-identity `K_j` is permitted.
## In Scope

- Global bilateral `Controller`/`Optical` hand-pose mode with the commit rule and state table above.
- Standard OpenXR hand-tracker ingestion (wrist and finger joints) via `XRServer`/`XRHandTracker`.
- Optical wrist source intent into the existing VRIK hand-target pipeline, limited to the fallback-source seam.
- Player-only finger retargeting modifier owning exactly the 30 canonical finger bones, rotation only.
- Separate authorable per-side optical calibration anchors and the calibrated world-space wrist output contract.
- Mode-conditional authority over authored INTR-003 finger poses, including freeze and cache semantics.
- Automatic enablement on runtime availability.
- `IXRRuntime`/`XRManager` exposure of the global mode and per-side sources with deterministic mock hooks.
- Project-owned, versioned neutral-normalisation profile data, validation, and safe per-destination fallback.
- Binding-time derivation, validation, and caching of effective non-thumb destination neutrals from the bound skeleton's
  global rest geometry.
- Binding-time per-hand destination anatomical frame derivation with fail-closed consensus gates and transactional
  publication.
- Constrained anatomical non-thumb runtime mapping — signed PIP/DIP hinge flexion and roll-free proximal swing —
  with granular per-chain freeze.
- Authored-animation thumb model: immutable one-frame reference sampling, six independent authored axes, one
  all-or-nothing bilateral binding transaction, a roll-free metacarpal bend/splay frame, the anchored hand-frame
  metacarpal correspondence with neutral anchor and calibrated directional response envelope, independent signed
  proximal/distal flexion, Reset-sourced thumb neutrals, and the thumb freeze ladder.

## Out Of Scope

- Godot Meta Toolkit integration.
- OpenXR Vendors plugin.
- Meta hand meshes.
- Runtime-generated hand meshes.
- Full-body tracking.
- Gesture or grab input mapping.
- Pose recognition.
- Tracked-finger collision.
- Smoothing requirements.
- Additional gain laws, scalar angle caps, or response clamps beyond Requirement 28's mandatory metacarpal envelope.
- Modification of the authored `Reset` or `Grab-pipe-10` reference animation assets.
- Applying authored-animation axis derivation to non-thumb mapping in this increment; Requirements 17–23 remain
  unchanged and hardware-accepted.
- Player-facing per-user calibration UI or saved user profiles.
- Position-driven finger reconstruction.
- Thumb-metacarpal axial opposition-roll transfer and correction of the accepted Stage 1 open thumb web/V.
- Full production `N_j × Delta` thumb transfer.
- Thumb tip synthesis: no thumb tip joint exists and none may be fabricated.
- General non-thumb basis-correspondence fitting, circumduction capture, or any promotion path to non-identity `K_j`;
  `K_j` stays identity-only deprecated compatibility metadata.
- Artistic changes to finger-root spacing or imported rest geometry.
- Player-facing settings or UI for hand tracking.
- Later calibration, new hardware-evidence collection, and thumb-closure changes. Each requires a separately approved
  increment and does not defer the retained mapping, profile, fallback, authored-reference, controller, or non-thumb
  contracts in this specification.

## Acceptance Criteria

### Existing Runtime And Static-Neutral Acceptance

| ID | Requirement Layer | Criterion |
|----|-------------------|-----------|
| 1 | User | Optical hand tracking enables automatically when the runtime supports it, |
|   |                   | without player action. |
| 2 | User | With both hands unambiguously tracked optically, in-game hands and fingers |
|   |                   | follow the player's real hands. |
| 3 | User | Momentary disagreement, ambiguity, or single-hand optical loss does not |
|   |                   | switch the committed mode; hands do not jump between sources. |
| 4 | User | Optical loss of one hand freezes that hand at its last valid pose while |
|   |                   | the other hand continues. |
| 5 | User | When both hands later unambiguously report controller input, hand control |
|   |                   | returns to controllers. |
| 6 | User | Outside optical mode, authored hand-pose animations behave exactly as |
|   |                   | before, with no authored animation or AnimationTree state mutated. |
| 7 | User | Grabbing, holding, collision, and obstruction behaviour is identical in |
|   |                   | both modes. |
| 8 | User | Controller-driven gameplay is unaffected while optical mode is active. |
| 9 | Technical | Implementation uses only `XRServer`, `XRHandTracker`, and |
|   |                   | `/user/hand_tracker/left\|right`; no excluded dependency or API is |
|   |                   | introduced. |
| 10 | Technical | Mock-driven mode transitions follow the bilateral mode state table |
|   |                   | exactly, including retain on disagreement or ambiguity and retain on |
|   |                   | optical-sample loss. |
| 11 | Technical | Controller calibration anchors in `openxr_runtime.tscn` are unchanged; |
|   |                   | optical poses come from separate authorable per-side full `Transform3D` |
|   |                   | calibration anchors. |
| 12 | Technical | The optical wrist output applies the XROrigin transform, reference-frame |
|    |                   | adjustment, recentring, and non-unit world scale exactly once. |
| 13 | Technical | The selected calibrated wrist replaces only the fallback source intent |
|   |                   | at the `XRHandPoseTargetProvider` seam and flows through the otherwise |
|   |                   | unchanged target pipeline. |
| 14 | Technical | The finger modifier is a custom `SkeletonModifier3D`, not |
|   |                   | `XRHandModifier3D`, and writes exactly the 30 canonical |
|   |                   | `SkeletonProfileHumanoid` finger bones. |
| 15 | Technical | The modifier never writes hand, wrist, or arm bones, bone positions, |
|   |                   | bone scale, or global poses. |
| 16 | Technical | For every non-thumb destination, deterministic tests verify `S = Q_parent⁻¹ × Q_joint`, |
|    |                   | `Delta = S0_j⁻¹ × S`, and the anatomical outputs of Requirements 21–23 within `0.1` |
|    |                   | degrees, using the cached rest-derived effective `N_j` and per-hand frame. Tests |
|    |                   | preserve source-only metacarpals and verify that an invalid metacarpal freezes only |
|    |                   | its proximal destination, with no wrist fallback. |
| 17 | Technical | Outside optical mode the modifier performs no writes; authored |
|   |                   | AnimationTree hand poses remain authoritative. |
| 18 | Technical | Mode entry snapshots authored finger rotations only for freeze fallback; |
|   |                   | `S0_j` and all other profile values remain immutable and are not captured |
|   |                   | from entry or animation. Finger joints use the orientation-tracked-or-valid |
|   |                   | acceptance tier while wrist capture stays actively tracked. An invalid |
|   |                   | source parent, source joint, or profile record freezes only the affected |
|   |                   | destination at its authored entry snapshot; whole-hand loss freezes only |
|   |                   | that hand. |
| 19 | Technical | Mode exit clears the optical-session cache so the current authored pose |
|   |                   | immediately regains authority. |
| 20 | Technical | The finger modifier runs after VRIK hand-copy modifiers in skeleton |
|   |                   | modifier execution order. |
| 21 | Technical | The modifier topology exists only in the player template (CHAR-001); |
|   |                   | base and NPC templates do not include it. |
| 22 | Technical | Mock runtime hooks deterministically drive committed-mode transitions |
|   |                   | and per-side sample validity for tests without hardware. |
| 23 | Technical | `IXRHandController` surfaces and controller consumers (PlayerController, |
|   |                   | Transcriber) are unchanged. |
| 24 | User | On both hands, tracked straight and together fingers avoid exaggerated |
|   |                   | directional fan while retaining the accepted slight rig-authored natural |
|   |                   | bend. Deliberate tracked spread remains visible and signed. |
| 25 | Technical | The versioned resource profile validates exactly 15 unique records per side (12 non-thumb |
|    |                   | plus 3 thumb) in one schema `2` resource, and finite, normalised |
|    |                   | `S0_j`, compatibility `DestinationNeutral`, and `K_j` quaternions. Any non-identity |
|    |                   | `K_j` — non-thumb or thumb — is rejected as an invalid record rather than silently |
|    |                   | ignored. Effective non-thumb runtime `N_j` is derived from the bound rig and thumb |
|    |                   | `N_j` from the Reset-sampled neutral (Requirement 29); serialised `DestinationNeutral` |
|    |                   | is not applied. |
|    |                   | Missing or invalid records use the per-destination freeze fallback and never direct |
|    |                   | unnormalised mapping. |
| 26 | Technical | Profile fixtures resolve `game/assets/xr/calibration/fingers_calibration_quest3.tres` and |
|    |                   | verify schema `2`, pinned immutable `S0_j`, semantic `SourceCaptureID` and `Provenance`, |
|    |                   | identity-only `K_j`, `Q0`, `K_meta`, and response thresholds. Every |
|    |                   | hinge and non-metacarpal swing gain is `1`; metacarpal gain remains within its |
|    |                   | Requirement 28 envelope. |
| 27 | Technical | Bilateral straight, fist, and spread fixtures verify all 24 non-thumb destinations |
|   |                   | independently, and thumb fixtures verify all six thumb destinations through the |
|   |                   | authored-animation model (Requirements 24–29) rather than direct source-relation |
|   |                   | writes. |
| 29 | Technical | Per-hand rest fixtures verify that every proximal direction aligns to the |
|   |                   | middle proximal direction; middle is unchanged; and each chain preserves its |
|   |                   | intermediate/distal relative rotations, curvature, bend plane, roll, and |
|   |                   | distal relation. Parallel and antiparallel cases are deterministic. |
| 30 | Technical | Binding derives and caches complete parent-local `N_j` values once from the |
|   |                   | destination skeleton's global rest geometry. It performs no independent PIP |
|   |                   | or DIP straightening and does not mutate rest, origins, positions, lengths, |
|   |                   | scales, skinning, animations, or pose channels; runtime writes remain |
|   |                   | rotation-only. Invalid rest geometry or chain topology rejects the binding |
|   |                   | and fails closed without partial retargeting. |
| 31 | Technical | The hardware-accepted static-neutral contract remains reduced together-pose |
|    |                   | fan with natural bend and deliberate spread preserved. The calibrated thumb |
|    |                   | neutral reconstructs the authored Reset neutral pose at `Delta = identity` (criterion A9). |

### Anatomical Mapping Implementation Acceptance

| ID | Requirement Layer | Criterion |
|----|-------------------|-----------|
| A1 | Technical | `Delta = identity` reconstructs the exact effective `N_j` for all 24 non-thumb destinations |
|    |                   | on both hands, preserving the natural neutral bend. |
| A2 | Technical | Deterministic tests verify positive and negative PIP/DIP hinge angles about the shared |
|    |                   | `H`, with positive rotation about `H` moving `L` palmward towards `B`, and correctly |
|    |                   | signed mirrored frames on both hands with no side-specific source inversion. |
| A3 | Technical | Source longitudinal roll and off-hinge swing at PIP/DIP produce no destination |
|    |                   | change; the discarded components are fully rejected, not partially transferred. |
| A4 | Technical | Proximal tests verify flexion-only, spread-only, and combined swings, and |
|    |                   | invariance of `D_p` under pure source roll about `l_s`. |
| A5 | Technical | Shared-frame derivation tests verify that every Requirement 19 consensus gate fails |
|    |                   | closed on its listed degeneracy (span ratio, natural bend, missing curvature |
|    |                   | direction, concentration, disagreement angle, finiteness, frame unit/orthogonal/ |
|    |                   | right-handed) and that binding publishes transactionally with no partial state. |
| A6 | Technical | The tipless distal inherits the same global `H` expressed in its own desired local |
|    |                   | frame; no synthetic tip direction is used anywhere. |
| A7 | Technical | The direct-dependency freeze ladder of Requirement 35 is verified joint-by-joint, |
|    |                   | with unrelated chains remaining live. |
| A9 | Technical | `Delta = identity` reconstructs the exact Reset-sampled thumb neutral `N_j` (Requirement 29) |
|    |                   | for all six thumb destinations on both hands — on the reference female, metacarpal |
|    |                   | non-identity (≈95.26°), proximal/distal identity, numerically equal to the imported |
|    |                   | rest — with no Requirement 43 fan correction applied to the thumb. |
| A10 | Technical | Authored-axis fixtures verify all six Reset→flexion axes and reference angles against |
|     |                   | the pinned reference-female oracle of Requirement 26, quaternion hemisphere alignment, and |
|     |                   | bilateral mirror residuals under `a_left_expected = J · a_right`, `J = diag(+1, -1, -1)`. |
|     |                   | Positive and negative source `+X` hinge angles map independently around each |
|     |                   | proximal/distal `a_j`, with no shared axis or synthetic tip. |
| A11 | Technical | The thumb freeze ladder of Requirement 29 is verified joint-by-joint (wrist, thumb |
|     |                   | metacarpal, proximal, distal), with non-thumb chains remaining live and vice |
|     |                   | versa. |
| A12 | Technical | Binding accepts only the two configured exported resource paths, resolved directly |
|     |                   | with no `AnimationPlayer`, no library registration, and no fuzzy path matching, and |
|     |                   | exactly one enabled `Rotation3D` track with one `t=0` key for each required canonical |
|     |                   | bone path in each immutable one-frame resource. |
| A13 | Technical | Direct resource sampling registers nothing on any live animation node and leaves playback, |
|     |                   | mixer and AnimationTree state, live skeleton pose, and both animation resources unchanged. |
|     |                   | Source guards prohibit `Play`, `Seek`, `Advance`, registration, live-pose sampling, and |
|     |                   | resource mutation. |
| A14 | Technical | Every missing or malformed resource path, resource, track, key value or key time, bone, |
|     |                   | topology input, or quaternion produces the precise Requirement 25 failure reason. Any gate |
|     |                   | failure on either hand — profile, non-thumb frame, thumb asset, axis, mirror, or |
|     |                   | metacarpal — publishes no binding state for the skeleton through the single |
|     |                   | Requirement 26 transaction and performs no finger writes. |
| A15 | Technical | Fixtures verify that each configured authored reference path is resolved directly and |
|     |                   | satisfies the Requirement 25 resource and track contract. |
| A16 | Technical | Metacarpal fixtures verify flexion-only, splay-only, combined motion, invariance under |
|     |                   | right-composed source `+Y` roll, shortest-arc output, right-handed frame construction, and |
|     |                   | mirrored `c_b` signs. Smoothstep tests pin both boundaries and the midpoint, monotonicity, |
|     |                   | C1 continuity, product `g`, and `k_eff = 1 + (K_meta - 1) × g`. Tests discriminate the |
|     |                   | normative |
|     |                   | `D_meta = N_meta × R'` from the wrong-order `R' × N_meta`, `N_meta × Delta`, |
|     |                   | and conjugation-through-`N_meta` alternatives. The normalised palmward gate |
|     |                   | `dot(b, normalise(n_perp)) ≥ 0.8`, its `sin 20°` degeneracy floor, and every other |
|     |                   | degeneracy and gate in Requirement 28 fail closed. |
| A17 | Technical | Non-thumb equations, destination frame `N_j`, profile values, fixtures, and outputs are |
|     |                   | unchanged, and no non-thumb binding or sample reads either authored reference. |
| A18 | Technical | Guards verify the prohibitions: no thumb-to-index or segment-centre/curvature frame, no |
|     |                   | hinge shared across thumb bones, no synthetic tip, and no production full |
|     |                   | `N_j × Delta` transfer. Every hinge and non-metacarpal swing gain is `1`; metacarpal |
|     |                   | effective gain stays between `1` and `K_meta`. There is no scalar cap, response clamp, |
|     |                   | marker or pose-classification dependency, runtime fitting, or non-thumb regression. |
| A19 | Technical | On a synthetic rig whose Reset thumb keys differ from its imported rest rotations, |
|     |                   | `Delta = identity` reproduces the exact Reset keys, not the imported rest, for all |
|     |                   | six thumb joints on both hands (Requirement 29). |
### Accepted Behaviour And Automated Evidence

| ID | Requirement Layer | Criterion |
|----|-------------------|-----------|
| H1 | User | On both hands, at least four fist/open cycles accumulate no lateral phalange |
|    |                   | twist, fingertip bunching, or fingertip crossing. |
| H2 | User | Opening returns consistently to the accepted straight/together neutral, which |
|    |                   | stays free of static splay and retains the natural authored bend. |
| H3 | User | Deliberate spread retains its sign and a useful visible magnitude on both hands. |
| H4 | User | Combined partial curl and spread stay coherent on both hands: bend planes remain |
|    |                   | stable while spread is retained. |
| H6 | User | Wrist, middle-finger, and non-thumb behaviour do not regress. Thumb mapping remains |
|    |                   | bounded by the Stage 1 limitation recorded in H10. |
| H7 | Technical | Profile fixtures verify the pinned `S0_j`, schema `2`, semantic `SourceCaptureID` and |
|    |                   | `Provenance`, `Q0`, `K_meta`, response thresholds, identity-only `K_j`, and gain contracts. |
| H8 | Technical | Automated fixture and replay coverage verifies that thumb opposition maps palmward and |
|    |                   | returns the calibrated neutral. It is not live mesh-clearance or visual-acceptance proof. |
| H9 | Technical | Production permits normal per-joint sampling and validation, bounded ordinary |
|    |                   | operational `ILogger` warnings, and static test replay fixtures. It has no runtime |
|    |                   | JSONL traces, native probe/coherence/event instrumentation, pose markers, diagnostic |
|    |                   | capture, schedule, or digest, or runtime fitting. |
| H10 | User | A fist may retain an open thumb web/V. This is an accepted Stage 1 limitation and requires |
|     |                   | no corrective work in this increment. |

## References

- [XR-001: XRManager](../001-xr-manager/index.md)
- [IK: VRIK System](../../ik/index.md)
- [IK Implementation Notes](../../ik/implementation-notes.md)
- [IK-002: Arm And Shoulder IK System](../../ik/002-arm-shoulder-ik/index.md)
- [IK-005: Target Pipeline](../../ik/005-target-pipeline/index.md)
- [INTR-002: Hand Grab Execution](../../interaction/002-hand-grab-execution/index.md)
- [INTR-003: Hands](../../interaction/003-hands/index.md)
- [CTRL-002: Hand Grab Input](../../ctrl/002-hand-grab-input/index.md)
- [CHAR-001: Character Skeleton Profile](../../character/001-character-skeleton/index.md)
- @game/src/XR/HandTracking/
- @game/src/XR/OpenXR/OpenXRRuntimeNode.cs
- @game/assets/xr/openxr_runtime.tscn
