---
name: godot-3d
description: General guidelines for working with Godot 3D APIs. Use this skill whenever working with 3D nodes, transforms, skeleton IK, physics, or any code that manipulates objects in 3D space. Trigger on mentions of Vector3, Transform3D, Basis, Node3D, Skeleton3D, 3D math, or when converting between coordinate frames.
---

# Godot 3D Guidelines

This skill provides general guidance for working with Godot's 3D system, focusing on common pitfalls and conventions that are easy to get wrong.

## Axis Conventions

This section captures critical axis convention knowledge that has caused bugs when not properly considered.

### The Core Principle: Always Ask "Which Frame Is This In?"

When working with 3D coordinates, always identify which coordinate frame you're operating in:

- **World/Character space** — Global scene coordinates. Godot's world forward is **-Z**.
- **Rig/container space** — The character model's root transform, which may include rotation offsets from import.
- **Skeleton-local space** — Bone positions and transforms relative to the skeleton root.

Failing to distinguish these frames is a common source of bugs.

### The Skeleton Orientation Problem

**Context for this project:**

The reference character rig uses a 180° yaw-rotated container. This means:

| Semantic direction | Skeleton-local axis |
|--------------------|--------------------|
| Avatar forward     | **+Z**             |
| Avatar back        | **-Z**             |
| Avatar right       | **-X**             |
| Avatar left        | **+X**             |
| Avatar up          | **+Y** (unchanged) |

This differs from Godot's native world-space convention where forward is -Z.

**Why it matters:**

If you hard-code `Vector3.Forward` (which is -Z) in code that operates on skeleton-local positions, you'll get the wrong direction on this rig. The same applies to left/right — avatar-right resolves to -X, not +X.

**How to check:**

Look at the container transform in the scene file. For example, in `.tscn`:

```gdscript
[node name="Female_export" parent="." index="0"]
transform = Transform3D(-1, 0, -8.742278e-08, 0, 1, 0, 8.742278e-08, 0, -1, 0, 0, 0)
```

The rotation component (the upper-left 3x3 of the Transform3D) reveals the orientation. A 180° Y rotation flips forward/back and left/right in skeleton-local terms.

**Safe pattern:**

Rather than hard-coding raw vectors, use an explicit semantic-frame helper:

```csharp
// Instead of this:
Vector3 forward = Vector3.Forward;  // -Z — wrong for skeleton-local on this rig

// Do this:
HipLimitSemanticFrame frame = HipLimitSemanticFrame.ReferenceRig;
Vector3 avatarForwardResolved = frame.AvatarForwardLocal;  // +Z — correct for this rig
```

The `HipLimitSemanticFrame` in this project demonstrates the pattern — it encodes the avatar-relative-to-skeleton-local mapping once, so callers don't need to know the rotation.

**Testing for this bug:**

When writing tests for skeleton-local calculations, use a rig with the actual orientation, not an identity-basis skeleton. Tests on identity skeletons will pass even when the axis mapping is wrong.

### General 3D Guidelines

#### Transform Precedences

- **Global transforms** (`GlobalTransform`) are authoritative for world-space position and are rarely directly manipulated — use local transforms and let Godot's scene tree handle propagation.
- **Local transforms** (`Transform3D`) are the working level for most game logic.
- **Basis** (`Basis`) represents rotation and scale. For rotation-only operations, prefer `Transform3D.Basis` or `Quaternion` over Euler angles to avoid gimbal lock.

#### Vector Directions

- Godot uses a **right-handed** coordinate system.
- **+Y is up**, **-Z is forward** in world space.
- Be cautious with `Vector3.Up`, `Vector3.Forward`, etc. — these are world-space constants and may not match your object's local orientation.

#### Skeleton Considerations

- Bone positions are in **skeleton-local space** after `Skeleton3D.GetBoneGlobalRest()` is called — the returned `Transform3D.Origin` is relative to the skeleton root, not world space.
- When doing IK or pose math, always verify whether you're working in world, rig-container, or skeleton-local space.
- AnimationPlayer/AnimationTree outputs are sampled into skeleton-local bone transforms — any custom IK logic must consume and produce in that same space.

#### Raycasting and Collision

- `PhysicsDirectSpaceState3D.IntersectRay()` returns collision points in **world space**.
- Convert to local space before applying to character-specific logic: `to_local(point)`.

#### Common Mistakes to Avoid

1. **Hard-coding world-space directions** in local-space calculations — always verify the intended frame.
2. **Confusing Transform3D multiplication order** — in Godot, `a * b` means "apply a after b", i.e., `b`'s transform is expressed in `a`'s space. This is right-to-left composition.
3. **Ignoring scale in Basis** — `Transform3D.Basis` includes scale. If you need rotation only, extract it explicitly or normalise.
4. **Forgetting to normalise vectors** — direction vectors from math operations may not be unit length, which causes unexpected behaviour in distance-based calculations.

## References

- This project's skeleton axis mapping is encoded in `HipLimitSemanticFrame` (`game/src/IK/Pose/HipLimitFrame.cs`).
- For IK-specific conventions, see the IK contract specs in `specs/characters/ik/`.