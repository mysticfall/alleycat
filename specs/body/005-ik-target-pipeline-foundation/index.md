---
id: BODY-005
title: IK Target Pipeline Foundation
parent: BODY-002
---

# IK Target Pipeline Foundation

## Requirement

The system must provide a reusable pipeline for IK target computation that supports optional target contributors while
preserving existing head and hand target behaviour, enabling future extension to XR source plus restraint/impact
adjustments without replacing the source.

## Goal

Implement the foundational target pipeline for current physical IK target paths, establishing the data-flow
architecture, contributor model, and actuator abstraction before adding constraint or reaction contributors.

## User Requirements

1. Player hand movement must remain unchanged when no target contributors are active.
2. Head target movement must remain unchanged except for routing through the shared pipeline.
3. XR controller position must remain the authoritative source intent for player hands.
4. Pipeline must expose source target, requested/constrained target, realised target, and
   feedback reason or error for debugging.
5. A no-op contributor must be insertable without changing pipeline output.
6. Hand collision and obstruction behaviour must be preserved as the physical actuation layer.

## Technical Requirements

1. Pipeline shall implement: Source Intent → Target Contributors → Constrained Request →
   Physical Actuation → Feedback → IK Target Output.
2. Source intent shall be sampled by each target pipeline and exposed as `SourceTarget` output.
3. Contributors shall implement a single `IIKTargetContributor` interface initially, supporting
   both modifier and constraint contributions without forced separation.
4. Each contributor shall receive source target and prior contributor output as input,
   producing a modified or constrained target as output.
5. Constrained request shall be the final target after all contributors, exposed as
   `RequestedTarget` output.
6. The pipeline shall own source sampling, contribution, constrained request construction,
   physical actuation, and result publication for each frame.
7. Physical actuation shall be exposed through an `IIKTargetActuator` stage with `Actuate(...)`
   returning `IKTargetActuationResult`.
8. Hand target actuation shall apply the constrained request through the existing `AnimatableBody3D`
   obstruction layer, preserving current movement, collision, and dynamic body interaction behaviour.
9. Head target actuation shall apply the constrained request through the existing `CharacterBody3D`
   body actuator without introducing head contributors or comfort constraints in this slice.
10. Right hand, left hand, and head paths shall each publish target pipeline debug state.
11. Right and left hands shall each expose contributor arrays; head uses an empty contributor provider
    until head-specific contributors are introduced.
12. Production target actuators shall be pure actuators and shall not expose legacy source delegates or
    `Follow(...)` APIs.
13. Realised target shall be the final output after physical actuation, exposed as
   `RealisedTarget` output.
14. Feedback shall expose constraint or limit reasons, errors, and delta between requested
    and realised, exposed as `Feedback` output.
15. Debug output shall include source, requested, realised, and feedback for validation.
16. No-op contributor shall implement `IIKTargetContributor` and return input unchanged.
17. This slice shall not introduce new BODY → IK dependencies: IK may consume Body
     profile/proxies, while `DynamicPhysicalRig` and BODY collision proxy generation remain
     independent of IK.
18. Physical actuation layer shall be composable with future chain/grab/collision modes,
      not exclusive to any single actuation type.

## In Scope

- Target pipeline for right hand, left hand, and head physical IK target paths.
- Source intent → contributors → constrained request → actuation → feedback.
- Single `IIKTargetContributor` interface for modifier and constraint contributions.
- `IIKTargetActuator` abstraction for physical target actuation.
- Debug output for source, requested, realised, and feedback.
- No-op contributor insertion and validation.
- Preservation of existing `AnimatableBody3D` hand obstruction behaviour.
- Preservation of existing `CharacterBody3D` head actuator behaviour through the pipeline.
- Pipeline validation tests proving equivalence to current actuator output when no extra contributors are active.

## Out Of Scope

- Impact reaction contributors and hit response behaviour.
- Chain/grab constraint contributors.
- Stagger and recovery transitions.
- Ragdoll and physical constraint bridge.
- Separate modifier and constraint interfaces (deferred to future implementation).
- Comfort gate implementation for head displacement.
- Head-specific contributors or comfort constraints.
- Haptic feedback or audio/visual pressure cues.
- Network synchronisation for multiplayer.

## Acceptance Criteria

1. User Requirement 1 validated: hand movement matches current behaviour when no
   contributors are active.
2. User Requirement 2 validated: head target path runs through the pipeline and keeps the head solve target
   aligned with the actuated head target.
3. User Requirement 3 validated: XR controller remains source intent, unchanged from current
   implementation.
4. User Requirement 4 validated: pipeline exposes source, requested, realised, and feedback
   through debug interface.
5. User Requirement 5 validated: no-op contributor insertion produces identical output.
6. User Requirement 6 validated: hand collision and obstruction behaviour preserved through
   `AnimatableBody3D` actuation.
7. Technical Requirement 1 validated: pipeline implements documented data-flow stages.
8. Technical Requirement 3 validated: single `IIKTargetContributor` interface supports both
     modifier and constraint contributions.
9. Technical Requirements 6 to 12 validated: the pipeline owns one-frame flow for right hand,
   left hand, and head, using pure `IIKTargetActuator` target actuators without production
   `Follow(...)` usage.
10. Technical Requirement 15 validated: debug output includes all four pipeline stages.
11. Technical Requirement 17 validated: diff/import analysis confirms this slice adds no new
     BODY → IK dependency, and `DynamicPhysicalRig`/BODY collision proxy generation remain
     IK-independent.
12. Technical Requirement 18 validated: physical actuation layer supports composition with
     future modes.
13. Tests validate the new pipelines match current actuator output when no extra contributors
    are active.

## References

- [BODY-002: Character Physical Response System](../002-character-physical-response/index.md)
- [BODY-002 Design Notes](../002-character-physical-response/design-notes.md)
- [DynamicPhysicalRig Implementation](../../../game/src/Rigging/Physics/DynamicPhysicalRig.cs)
- [IK Implementation Notes](../../ik/implementation-notes.md)
- [IK-002: Arm And Shoulder IK System](../../ik/002-arm-shoulder-ik/index.md)
