---
id: IK-005
title: Full-Body Collision Decision Memo
---

# Full-Body Collision Decision Memo

## Status

Temporary — may be superseded by a formal specification.

## Decision

Option B: Hybrid root body + selective region proxies.

## Problem

Player character requires accurate per-major-bone collision for movement:

- Locomotion-driven body positioning must respect collision geometry.
- Player hands and held objects must not pass through their own body.
- External dynamic objects (chains, ropes, carried items) must interact naturally with body parts.

Current `PlayerVRIK` lacks explicit self-collision handling beyond temporary exceptions.

## Constraints

- **Physics-driven head/hand targets**: `PlayerVRIK` drives head/hand targets via physics; collision
  positioning complicated.
- **Animation-driven feet**: Animation-driven; upper body follows physics targets, lower body follows animation.
- **Collision/locomotion coupling unspecified**: Collision responses and locomotion state interaction
  is undefined.
- **Existing self-collision bypasses**: `PlayerVRIK` has temporary self-collision bypasses requiring replacement.

## Options

| Option | Description | Verdict |
|--------|-------------|---------|
| A | Full per-bone proxy colliders. Maximum fidelity, high cost and instability risk. | Rejected |
| B | Hybrid root body + selective region proxies. Balanced fidelity and performance. | **Selected** |
| C | Collision-free body with interactive push-out. Simple but no external body interaction. | Rejected |

## Phases

**Phase 1** — Upper-body proxy colliders (hands, forearms, torso). Validate no
hand-through-body clipping in play.

**Phase 2** — External object interaction. Verify natural contact without excessive bouncing.

**Phase 3** — Locomotion integration. Define collision state → locomotion contract before proceeding.

**Phase 4** — Expand to full body if Phases 1-3 prove the approach viable.

## Requirements

### User Requirements

| ID | Requirement |
|----|-------------|
| UR-1 | Hands and held objects must not pass through the player's own body during normal interaction. |
| UR-2 | External dynamic objects (chains, carried items) must interact naturally with body parts. |
| UR-3 | Collision handling must not cause visible jitter or instability in VR tracking. |
| UR-4 | Locomotion must remain responsive and not be unduly blocked by collision responses. |

### Technical Requirements

| ID | Requirement |
|----|-------------|
| TR-1 | Proxy colliders on selected bones rather than per-bone colliders on every skeletal node. |
| TR-2 | Collision layers separate self-collision prevention from external object interaction. |
| TR-3 | Proxy colliders attached to bone-attached nodes and follow the skeleton correctly. |
| TR-4 | Collision responses do not override or conflict with physics-based head/hand target driving. |
| TR-5 | Validated in-game with real locomotion scenarios, not just isolated collision tests. |
| TR-6 | Collision/locomotion coupling contract defined before Phase 3 begins. |

## Acceptance

| Criteria | Coverage |
|----------|----------|
| UR-1: No hand-through-torso clipping in play | UR |
| UR-2: Natural external object contact | UR |
| UR-3: No VR jitter from collision | UR |
| UR-4: Locomotion remains responsive | UR |
| TR-1: Hands, forearms, torso proxies | TR |
| TR-2: Layer config prevents self-intersection | TR |
| TR-3: Bone-attached nodes follow skeleton | TR |
| TR-4: No override of physics targets | TR |
| TR-5: Validated in locomotion scenarios | TR |
| TR-6: Locomotion coupling contract before Phase 3 | TR |

## References

- [IK-004: VRIK Pose State Machine And Hip Reconciliation](004-vrik-pose-state-machine/index.md)
- [IK Implementation Notes](implementation-notes.md)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)