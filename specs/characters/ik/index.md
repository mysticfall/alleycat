---
id: IK
title: Player VRIK System
---

# Player VRIK System

## Purpose

Define the parent specification for the player VR humanoid IK spec family.

This page is the source of truth for system-level scope, capability levels, and phased delivery intent across IK
sub-specifications.

## Requirement

Define an umbrella specification for a player-focused VR humanoid IK system that can be delivered incrementally and
expanded with dedicated child specifications.

## Goal

Provide a clear parent/child structure so IK component contracts align under one source of truth without duplicating
child-level implementation detail.

## User Requirements

1. Players must experience stable full-body VR character posing from baseline headset/controller input.
2. Baseline posture behaviour must support standing and crouching semantics, with room for extended pose coverage over
   time.
3. Optional tracking hardware should refine fidelity without breaking baseline support.
4. Players must experience diverse full-body poses (including non-upright poses such as crawling and lying) from
   baseline headset + two-controller input, not hands-only VR.

## Technical Requirements

1. This parent spec must define system-level capability boundaries and normative links to child IK contracts.
2. Runtime XR-to-IK integration boundaries must remain explicit via [XR-001: XRManager](../../xr/001-xr-manager/index.md)
   and [IK Implementation Notes](implementation-notes.md).
3. Child specifications must carry feature-level implementation contracts and validation criteria.
4. Incremental delivery phases must preserve backwards-compatible baseline semantics.
5. IK-001, IK-002, and IK-003 remain prerequisite contracts for upper-body, spine, and lower-limb solving; pose-state
   and hip-reconciliation orchestration must build on those contracts rather than redefining them.

## Specification Structure

- Use this page for system intent, scope, capability expectations, and delivery phases.
- Keep practical solver setup guidance in [IK Implementation Notes](implementation-notes.md).
- Keep feature-level behaviour and acceptance detail in child specifications.

## Child Specifications

- [IK Implementation Notes](implementation-notes.md)
- [IK-001: Reusable Neck-Spine CCDIK Setup](001-neck-spine-ik/index.md)
- [IK-002: Arm And Shoulder IK System](002-arm-shoulder-ik/index.md)
- [IK-003: Leg And Feet IK System](003-leg-feet-ik/index.md)
- [IK-004: VRIK Pose State Machine And Hip Reconciliation](004-vrik-pose-state-machine/index.md)

## In Scope

- A humanoid IK system for the player character in VR.
- Runtime XR-to-IK bridging for player head and hand target driving.
- Movement-driven interaction intent based on headset and controller motion.
- Baseline support using headset and two controllers.
- Optional refinement paths for additional tracking input when available.
- Wider pose coverage than basic upright-only interaction (for example kneeling, stooping, sitting, and crawling).
- High-level behaviour intent for constrained movement scenarios.

## Out Of Scope

- Feature-level solver-node configuration and rig-level implementation details that are normatively defined by child IK
  specifications and implementation notes.
- Full contract-level detail for IK-001, IK-002, and IK-003.
- XR runtime implementation details beyond the XRManager contract.
- Full contract-level detail for pose-state and hip-reconciliation architecture, which is normatively defined in
  [IK-004: VRIK Pose State Machine And Hip Reconciliation](004-vrik-pose-state-machine/index.md).
- Network replication and backend concerns.
- Platform certification and optimisation planning.

## Runtime Integration Boundary

- XR runtime contracts and startup state are defined in [XR-001: XRManager](../../xr/001-xr-manager/index.md).
- Player XR-to-IK runtime bridge behaviour is defined in [IK Implementation Notes](implementation-notes.md).
- IK component behaviour remains defined by child IK specifications.

## Input And Capability Levels

### Level 1: Baseline VR Input (Required)

- Inputs: headset + left/right hand controllers.
- Expected outcome: robust upper-body control with stable full-body pose approximation.

### Level 2: Optional Hand Tracking Refinement

- Inputs: Level 1 plus hand tracking where supported.
- Expected outcome: improved hand/arm intent interpretation.
- Behaviour must degrade gracefully to Level 1.

### Level 3: Optional Body Tracking Refinement

- Inputs: Level 1 plus body tracking where supported.
- Expected outcome: improved whole-body pose fidelity.
- Behaviour must degrade gracefully to lower levels.

## Incremental Delivery Plan

1. **Phase 1: Umbrella Alignment**
   - Establish IK as the parent source of truth for IK specifications.
   - Align existing IK-001 and IK-002 under this umbrella.
2. **Phase 2: Baseline Full-Body Capability Definition**
   - Define baseline component boundaries and acceptance outcomes.
3. **Phase 3: Extended Pose Range Coverage**
   - Expand coverage for standing, crouching, kneeling, stooping, sitting, crawling, and transitions.
   - Deliver framework-first pose-state and hip-reconciliation contracts under IK-004.
4. **Phase 4: Constraint Behaviour Definition**
   - Add focused specifications for constrained-motion scenarios.
5. **Phase 5: Optional Tracking Refinement Layers**
   - Add opt-in specifications for additional tracking modalities.

## Acceptance Criteria

1. The specification defines both user-visible IK outcomes and technical implementation-governance contracts.
2. System-level runtime boundaries explicitly reference XR and IK runtime-bridge contracts.
3. Baseline capability levels and phased delivery plan are defined for implementation planning.
4. Child IK specifications are identified as normative sources for feature-level implementation contracts.
5. Pose-state and hip-reconciliation behaviour is delegated to IK-004 while preserving IK-001/002/003 as
   prerequisites.

## Code-Spec Sync Note

This revision is specification-only and introduces no implementation, scene, or runtime code changes.

## References

- [Project Specifications Index](../../index.md)
- [IK Implementation Notes](implementation-notes.md)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)
- [IK-001: Reusable Neck-Spine CCDIK Setup](001-neck-spine-ik/index.md)
- [IK-002: Arm And Shoulder IK System](002-arm-shoulder-ik/index.md)
- [IK-003: Leg And Feet IK System](003-leg-feet-ik/index.md)
- [IK-004: VRIK Pose State Machine And Hip Reconciliation](004-vrik-pose-state-machine/index.md)
