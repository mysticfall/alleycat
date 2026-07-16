# NAV-001 NPC Motion Matching Navigation Memo

## Status

This is a temporary planning memo for NAV-001. It is not an authoritative specification, implementation contract, or
branch baseline. Use it to preserve approved direction before NAV-001 starts, then replace its guidance with the
authoritative NAV-001 spec once that spec exists.

Consumers: planner, coder, reviewer.

## Purpose

NAV-001 should reframe motion matching as NPC-only navigation execution rather than shared player/NPC locomotion.
NPC navigation should replace the old locomotion-layer concept instead of being layered on top of it.

Player movement in VR should remain tracker-driven. Uncovered player posture or motion cases should be handled by the
IK-004 pose state machine and/or simple animation blending where needed, not by a shared motion-matching locomotion
baseline.

## Branch Awareness

NAV-001 work should begin from `main`, not from the current ANIM-002 branch. Create a new branch named `NAV-001` and
inspect the actual branch baseline before drafting implementation phases.

Do not assume ANIM-002 branch code, specs, scene structure, or tests exist on `NAV-001` unless they are present after the
branch is created from `main`.

## ANIM-002 Reference Scope

ANIM-002 is useful context for lessons learned about motion matching, performance, animation data needs, and validation
risks. It should be mentioned and reviewed as a reference only.

ANIM-002 is not a dependency, prerequisite, or assumed future baseline for NAV-001. NAV-001 may reuse lessons or
infrastructure only after confirming they fit the new NPC-navigation-first direction and the actual `NAV-001` branch
state.

## Directional Design Notes

- Motion matching should execute NPC navigation intent, not own a shared player/NPC locomotion abstraction.
- The navigation contract should start from long-horizon goals rather than only short-lived velocity commands.
- Likely goal inputs include destination and facing targets represented by `Transform3D`, plus speed and orientation
  preferences.
- Motion matching should be able to query richer, stable trajectory information, including:
  - expected future positions,
  - facing direction,
  - desired speed,
  - stopping target,
  - curvature,
  - near-future path samples.
- The final API should not be decided in this memo. Resolve only enough of the character/locomotion hierarchy question to
  unblock NAV-001 design.

## Immediate Work Sequence

1. Switch back to `main`.
2. Create a new branch named `NAV-001`.
3. Inspect the actual `NAV-001` branch baseline, including available specs, code, scenes, and tests.
4. Resolve the character/locomotion hierarchy question enough to unblock NAV-001 design.
5. Draft the authoritative NAV-001 spec with explicit user requirements, technical requirements, validation contracts,
   and out-of-scope boundaries.
6. Derive implementation phases from the actual `NAV-001` branch baseline, not from assumptions about ANIM-002.

## Validation Expectations

Future NAV-001 work should include:

- code/spec synchronisation checks so implementation remains aligned with the authoritative NAV-001 spec,
- unit tests where logic can be tested without Godot runtime dependencies,
- integration tests where Godot nodes, navigation, animation, or scene wiring are involved,
- visual and playtest verification for natural NPC starts, turns, stops, and path following,
- standard repository checks, including format and build verification.

## Risks And Open Questions

- Root motion versus navigation correction, warping, or reconciliation remains unresolved.
- Obstacle avoidance, unreachable targets, off-mesh links, stopping behaviour, and final facing behaviour need explicit
  design coverage.
- ANIM-002 lessons and infrastructure may be reused, replaced, or discarded; decide only after inspecting the `NAV-001`
  baseline and drafting the spec.
- Player/NPC separation must remain explicit so VR tracker-driven player movement does not inherit NPC navigation
  constraints.
- The character/locomotion hierarchy question needs a minimal near-term decision before detailed NAV-001 API design.
