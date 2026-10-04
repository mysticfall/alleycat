---
id: ANIM-002
title: Locomotive Animation Set
---

# Locomotive Animation Set

## Requirement

Define the variant-independent locomotive clip set, its motion metadata, and package delivery consumed by player and
NPC locomotion.

## Goal

Any compatible variant supplies the real clips, root-motion facts, and per-foot planted intervals that consumers need
to plan natural moves and keep planted feet still, including moves shorter than a full start plus stop, without
inheriting another variant's values.

## User Requirements

1. Every supported variant supplies the full role set for travel, arcs, side-steps, pivots, starts, and stops.
2. Each variant's movement uses its own metadata. No variant inherits another variant's identities, durations,
   distances, timing, or trajectories.
3. Authored root motion is planning input, not immutable authority. Consumers may slice clips and apply small
   swing-phase corrections while keeping the motion recognisable.
4. Source assets are preserved and not regenerated merely to hide an implementation defect.
5. Technical artists can publish complete, coherent packages transactionally and diagnose invalid metadata.
6. Every locomotion clip states when each foot is planted, and intervals are derived correctly even where retargeting
   leaves feet slightly floating or sinking. Authored ground slides and low shuffles are known pipeline artefacts: they
   count as swing, are not pinned, and the user judges them in-game.
7. Naturalness is a manual user judgement; structural and numerical evidence does not prove it alone.

## Technical Requirements

### Runtime Capability Contract

1. A compatible variant declares one unique clip for each capability:
   - primary cyclic forward and backward travel;
   - cyclic lateral-left and lateral-right travel for each left/right phase lane;
   - cyclic left and right arcs for each left/right phase lane;
   - confined left and right side-steps;
   - confined left and right pivots;
   - finite forward and backward entries for each left/right phase lane; and
   - finite forward and backward exits for each left/right phase lane.
2. The canonical capability identifiers are:
   - `cyclic_forward_primary`, `cyclic_backward_primary`;
   - `cyclic_lateral_left_left_lane`, `cyclic_lateral_left_right_lane`,
     `cyclic_lateral_right_left_lane`, `cyclic_lateral_right_right_lane`;
   - `cyclic_arc_left_left_lane`, `cyclic_arc_left_right_lane`, `cyclic_arc_right_left_lane`,
     `cyclic_arc_right_right_lane`;
   - `confined_side_step_left`, `confined_side_step_right`, `confined_pivot_left`,
     `confined_pivot_right`;
   - `entry_forward_left`, `entry_forward_right`, `entry_backward_left`, `entry_backward_right`; and
   - `exit_forward_left`, `exit_forward_right`, `exit_backward_left`, `exit_backward_right`.
3. Capability, role, boundary kind (the catalogue's per-clip `boundary_kind`), loop intent (the per-clip
   `loop_intent`), and phase lane are explicit package data. Consumers do not infer them from names, suffixes, file
   names, declaration order, counts, or `sad` conventions.
   - **Leading foot:** for entries (`i2f_*`, `i2b_*`) and exits (`f2i_*`, `b2i_*`), the clip-name suffix names the
     leading foot. This is the foot lifted in swing at the clip's locomotion-side boundary, not the first foot to step.
     The locomotion side is the end of an entry, where it hands off to the cycle, and the start of an exit. In
     `i2f_l`, the right foot takes the first step and the left foot is lifting at the end; in `f2i_l`, the left foot
     is in swing at the start. The left/right in entry and exit capability identifiers follows the same convention,
     for example `entry_forward_left` for `i2f_l`.
4. Cyclic capabilities loop and declare `loop_intent` true. Entries and exits are finite transitions and declare
   `loop_intent` false. Confined manoeuvres are finite in use: navigation plays a bounded number of their steps.
   Finite and looping are not exclusive: side-step and pivot clips are designed to loop seamlessly and may declare
   `loop_intent` true, which does not contradict their `confined_manoeuvre` boundary kind. Missing, duplicate,
   unknown, or contradictory capabilities fail package validation.

### Motion Metadata

5. Every clip declares its duration, endpoint root displacement, endpoint yaw, and a root trajectory table: cumulative
   planar root position and yaw sampled over source time at the clip's native key rate, in source units.
   - **Declared properties:** the table declares its key rate and an interpolation error bound against the embedded
     root track, so the displacement and yaw between any two slice points are predictable within that bound.
   - **Use:** consumers choose clips and slices by direction and distance, and predict endpoints without evaluating
     the AnimationTree.
   - **Interim runtime derivation:** until the pipeline publishes the table, a consumer may derive it once at bind
     time from the embedded clip, never per frame. The derived table meets the same contract: key rate, coordinates,
     source units, and error bound.
   - **Ball-joint trajectories** are rig-specific runtime data, not package metadata; see
     [NAV-001 Technical Requirement 7](../../nav/001-npc-navigation/index.md#arrival-planner).
6. Coordinates are Godot root-local: `+X` right, `+Y` up, `-Z` forward; actor-local planar is `[right, forward]`
   = `[root.x, -root.z]`; yaw is signed local-Y in radians; package distances are in source-unit metres, as authored
   in the embedded clip.
   - **Scale rule:** Godot scales root motion by the bound `Skeleton3D`'s `motion_scale`, and one package may be bound
     to rigs with different scales. Every consumer of catalogue distances or root trajectory positions multiplies them
     by the bound skeleton's `motion_scale` before use. Yaw is not scaled. The rule does not apply to ball-joint
     trajectories, which are already in the bound rig's runtime units.
7. Clips declare per-foot phase events (`Acquire` or `Release` per anatomical foot) at their authored boundaries, and
   stitch mappings between roles at matching events, so whole clips can be chained.
8. Every locomotion clip declares per-foot planted and swing intervals over source time for **both** anatomical feet,
   covering the whole clip.
   - **Planted means weight-bearing contact:** the foot's ball joint (the toe-bone rest-pose origin) is down and
     effectively stationary on the ground. A planted interval runs from ball contact to ball release, where release
     is the ball lifting or starting to move along the ground. Heel-strike before ball contact counts as swing, and
     heel lift before release is roll within the interval.
   - **Ground slides are swing:** a ball moving along the ground faster than a policy tolerance is swing, not planted.
     This covers authored slides and low shuffles, which are known pipeline artefacts. Such content is accepted as-is,
     is not pinned, and its visual result is judged in-game.
   - **Derivation is heuristic and role-aware:** each clip's role (entry, cycle, exit, side-step, pivot, arc, idle)
     supplies an expected step structure that is fitted to the ball's height, relative to that foot's own low
     ball-height baseline within the clip, and to its planar ground speed. A constant float or sink therefore does not
     affect derivation. Role expectations, signal tolerances, and heuristics are tunable policy, not spec constants;
     accuracy must be reasonable, not perfect. The [Kimodo pipeline][kimodo-pipeline] Technical Requirement 15 is the
     normative derivation and verification contract.
   - **Authority:** intervals are authoritative for when a foot is planted, both for [IK-003 foot planting][ik-003]
     and for slice joins and correction gating in NAV-001. A clip missing coverage for either foot fails package
     validation. Idle clips declare both feet planted throughout; the idle package stores these intervals in its
     catalogue's `per_foot_planted_intervals` field.
9. Where a planted foot is pinned is not package data. It is the ball joint defined by [IK-003 Foot
   Planting][ik-003-planting]. Planted intervals describe motion only. They grant
   nothing: they do not authorise actor movement or any other runtime decision.
10. Authored root motion is planning input; consumers may slice clips and apply swing-phase corrections. Metadata does
    not impose exact distance, rate, or clip completion.
11. Package metadata is precomputed so consumers do not parse it or search the animation library per frame.
12. Package metadata does not embed CTRL input policy, NAV route/arrival policy, or destination tolerances.

#### Planted Combination Rule

When several weighted clips or slices contribute to the pose on a tick, as in crossfades, blends, and slice joins, a
foot is planted on that tick only if every contributing clip whose normalised blend weight exceeds `0.1` marks it
planted at that clip's playback position. Otherwise the foot is not planted. When one clip drives the pose, its own
intervals apply directly. The `0.1` threshold is a fixed spec constant, not an implementation tuning value; changing
it requires a spec change.

Each clip's effective weight is derived from the AnimationTree's blend parameters and transition progress, for example
`BlendSpace2D` blend weights and state-machine crossfade progress, normalised over the contributing clips. Every
consumer of planted state (IK-003 foot planting, NAV-001 correction gating, and planted-foot slip measurement) cites
this rule rather than restating it.

### Reference Variant Clip Facts

These `sad` values are informative for planning discussions, not defaults. Consumers read each variant's metadata.
Distances are in source units, before the bound skeleton's `motion_scale` (Technical Requirement 6).

| Role | Capability | Root Motion (Source Units) | Duration |
| ---- | ---------- | -------------------------- | -------- |
| `i2f_l`, `i2f_r` | Forward entry | `0.70 m` forward | `1.27 s` |
| `f2i_l`, `f2i_r` | Forward exit | `0.63 m` forward | `1.00 s` |
| `i2b_l`, `i2b_r` | Backward entry | `0.63 m` backward | `1.00 s` |
| `b2i_l`, `b2i_r` | Backward exit | `0.70 m` backward | `1.27 s` |
| `wf` | Forward cycle | `1.33 m` forward | `1.27 s` |
| `wb` | Backward cycle | `1.55 m` backward | `1.50 s` |
| `xl_*`, `xr_*` | Lateral cycles | `0.70–0.74 m` sideways | `1.40–1.50 s` |
| `al_*`, `ar_*` | Arc cycles | `0.88–0.90 m` forward, `0.33–0.71 m` sideways, `37–39°` turn | `1.17–1.27 s` |
| `sl`, `sr` | Side-steps | `0.43 m` sideways | `1.30 s` |
| `pl45`, `pr45` | Pivots | `45°` turn in place | `1.63 s` |

A full forward entry plus exit covers about `1.33 m` in source units, so shorter forward moves need a slice pair.
Entries and exits carry no yaw; heading changes come from pivots, arcs, or swing-phase correction. Entries and exits
declare phase events only at their travel boundary, and side-steps and pivots declare none, so slice joins rely on
the planted intervals of Technical Requirement 8.

### Package Delivery and Variants

13. Consumers select one character/variant package as a coherent unit: embedded `AnimationLibrary`, catalogue,
    planted intervals (for idle, the catalogue's `per_foot_planted_intervals` field), and integrity metadata. Mixed
    package, character, variant, or generation bindings fail closed.
14. Runtime packages use `[character asset root]/animations/runtime/<set>/<variant>`. Locomotion contains
    `locomotion_<variant>.tres`, `locomotion_<variant>_catalogue.json`, and per-foot planted-interval data
    (`per_foot_planted_intervals_v1.json`); idle contains `idle_<variant>.tres` and `idle_<variant>_catalogue.json`,
    whose `per_foot_planted_intervals` field holds the idle planted intervals.
15. Animations are embedded in their package library and resolved by `(library resource, animation identity)`.
    Standalone clip paths and serialised subresource identifiers are not runtime contracts.
16. Publication is variant-scoped. Publishing another variant must not overwrite or alias an existing variant's
    package. Live runtime switching is not required.
17. Cross-character sharing is explicit and validated. Authorship and retarget provenance remain truthful.
18. Publication stages and validates complete locomotion and idle packages before a journalled all-or-rollback update.
    A clean clone loads published packages without regeneration or local cache state.
19. Generated catalogues, planted intervals, descriptors, and provenance are pipeline-owned and not hand-edited.

### Validation and Ownership

20. [The Kimodo pipeline specification][kimodo-pipeline] owns source selection, repair, materialisation, publication,
    and package validation. It does not own player or NPC movement policy.
21. Package validation checks variant integrity, complete capabilities, declared frames and units, finite data,
    root-trajectory correspondence with the embedded clips, phase-event and stitch-mapping consistency, and both-foot
    planted-interval coverage.
22. Mechanism fixtures may be synthetic, but acceptance also exercises real selected content. Tests do not treat
    production names, timings, distances, or `sad` values as universal authority.
23. Validation prefers structural and numerical evidence. Screenshots are optional diagnostics; user review decides
    naturalness.

## In Scope

- The locomotive capability set and variant-owned motion metadata.
- Coherent package selection, embedded runtime delivery, and transactional publication.
- Package validation and variant compatibility.

## Out Of Scope

- Player input policy, NPC route/arrival policy, and the runtime foot-planting mechanism (IK-003).
- Live variant switching, generic motion matching, and terrain adaptation.
- New animation content, including short-step clips, unless NAV-001 measurement shows a need and the user approves.

## Current Data Gaps

This section is non-normative status. The current `sad` packages do not yet meet Technical Requirement 5: each clip's
catalogue holds only start, middle, and end root samples.

Until pipeline and catalogue data work closes this gap, NAV-001 consumers use the interim runtime derivation of
Technical Requirement 5. Ball-joint trajectories are bind-time runtime data in every case and are not a data gap.

The existing `_l` start/stop lane (`i2f_l`, `f2i_l`) has a lateral planted-foot mismatch of about 70–125 mm against
`wf`, even at phase-aligned seams. The content fix is deferred by user decision; NAV-001 must operate with the
existing clips.

## Acceptance Criteria

### User Requirement Acceptance

1. **ANIM-002-UAC-001:** Real selected-content NPC runs in the NAV-001 scenarios move with real clips and slices of
   this package, and focused user review judges them natural.
2. **ANIM-002-UAC-002:** A different compatible full-role variant can be selected without source-code changes or
   inherited `sad` assumptions.
3. **ANIM-002-UAC-003:** Publication preserves source assets and reports invalid metadata without fabricating success.

### Technical Requirement Acceptance

1. **ANIM-002-TAC-001:** Package tests verify all 22 capabilities, unique explicit dispatch, variant-owned metadata,
   rejection of missing, duplicate, inferred, mixed-package, or contradictory declarations, and acceptance of
   `loop_intent` true on `confined_manoeuvre` clips.
2. **ANIM-002-TAC-002:** Metadata tests verify that:
   - declared endpoint displacement and yaw match the embedded clips;
   - the root trajectory table uses the clip's native key rate and source units;
   - at and between samples, its root position and yaw match the embedded root track within the declared error
     bound; and
   - phase events and stitch mappings are consistent.
3. **ANIM-002-TAC-003:** Package tests verify both-foot planted-interval coverage for every locomotion clip and, via
   the idle catalogue's `per_foot_planted_intervals` field, every idle clip. They reject a clip missing either foot,
   verify each interval against its clip under the weight-bearing definition of Technical Requirement 8 (a ground
   slide above the policy tolerance is swing), confirm that derivation fits each clip's role-expected step structure,
   and verify the [Planted Combination Rule](#planted-combination-rule) on crossfade and slice fixtures, including
   weights just above and below `0.1`.
4. **ANIM-002-TAC-004:** Publication tests verify embedded package layout, variant isolation, transaction rollback,
   clean-clone loading, and preserved source assets.
5. **ANIM-002-TAC-005:** Runtime tests consume another compatible package's own metadata without `sad` authority.
6. **ANIM-002-TAC-006:** Runtime tests bind one package to rigs with different `motion_scale` values and verify, on
   each rig:
   - each consumer of catalogue distances or root trajectory positions applies the bound skeleton's scale: a scaled
     root prediction for a slice matches the AnimationTree's evaluated root motion for that slice within the declared
     error bound × `motion_scale`, and its yaw, unscaled, matches within the declared error bound; and
   - where the interim root derivation is used, the derived table matches the declared key rate, coordinates, source
     units, and error bound, and is built once at bind time, never per frame.

### Acceptance Traceability

- User Requirements 1–3 → ANIM-002-UAC-001–002, ANIM-002-TAC-001–002, ANIM-002-TAC-005.
- User Requirements 4–5 → ANIM-002-UAC-003, ANIM-002-TAC-004.
- User Requirement 6 → ANIM-002-TAC-003.
- User Requirement 7 → ANIM-002-UAC-001.
- Technical Requirements 1–4 → ANIM-002-TAC-001.
- Technical Requirements 5–7, 10–12 → ANIM-002-TAC-002, ANIM-002-TAC-005–006.
- Technical Requirements 8–9 → ANIM-002-TAC-003.
- Technical Requirements 13–19 → ANIM-002-TAC-004–005.
- Technical Requirements 20–23 → ANIM-002-TAC-001–002, ANIM-002-TAC-004.

## References

- [Project Specifications Index](../../index.md)
- [Kimodo Locomotive Animation-Set Pipeline][kimodo-pipeline]
- [NAV-001: NPC Navigation](../../nav/001-npc-navigation/index.md)
- [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md)
- [IK-003: Leg and Feet IK System][ik-003]

[ik-003]: ../../ik/003-leg-feet-ik/index.md
[ik-003-planting]: ../../ik/003-leg-feet-ik/index.md#foot-planting
[kimodo-pipeline]: ../../tooling/kimodo-pipeline/locomotive-animation-set.md
