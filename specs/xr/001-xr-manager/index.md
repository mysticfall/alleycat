---
id: XR-001
---

# XRManager

## Overview

XRManager defines the XR runtime contract used by gameplay and tests. It acts as an orchestrator only: it selects a runtime scene, instantiates its root node, and interacts with that runtime strictly through `IXRRuntime`.

## Requirement

XRManager must provide:

1. XR startup with a reported initialisation result
2. A runtime-agnostic interface surface for origin, camera, and hand controllers
3. Late-subscriber readable XR initialisation state flags
4. Runtime scene selection through exported packed scenes (`OpenXrRuntimeScene`, `MockRuntimeScene`)
5. Runtime-specific behaviour delegated to `IXRRuntime` implementations for real XR and mock XR
6. Mock runtime entry points suitable for deterministic integration tests

## Contract

### IXRRuntime Boundary

XRManager must not depend on concrete runtime-node types. It may only call the `IXRRuntime` contract exposed by the instantiated runtime root.

### Runtime Scene Selection

XRManager selects and instantiates one runtime root scene via exported packed-scene properties:

- `OpenXrRuntimeScene` for normal OpenXR execution
- `MockRuntimeScene` for integration-test execution

### Interface Surface

| Component | Role |
|------|------|
| Origin | Active XR origin abstraction |
| Camera | Active XR camera abstraction |
| RightHandController | Active right-hand controller abstraction |
| LeftHandController | Active left-hand controller abstraction |

Shared interfaces are defined in `@game/src/XR/XRManagerAbstractions.cs`:

- `IXROrigin`
- `IXRCamera`
- `IXRHandController`

`IXRHandController` exposes:

- A runtime-agnostic callback/event API for button and input changes.
- `HandPositionNode` as the runtime-authored marker for authoritative hand pose.

### Initialisation State Contract

XRManager must expose post-startup state for components that subscribe after `_Ready`:

| Property | Description |
|------|------|
| `InitialisationAttempted` | `true` once XR startup was attempted |
| `InitialisationSucceeded` | Result of the latest XR startup attempt |

### Signals

| Signal | Description |
|------|------|
| Initialised(bool succeeded) | Emitted after XR initialisation is attempted |
| PoseRecentered() | Emitted when XR pose recentering occurs |

### Runtime Split

- OpenXR runtime (`AlleyCat.XR.OpenXR`) instantiates `@game/assets/xr/openxr_runtime.tscn`, uses scene-attached component scripts (`OpenXRRuntimeNode`, `OpenXRCameraNode`, `OpenXRHandControllerNode`) on XR node types (`XROrigin3D`, `XRCamera3D`, `XRController3D`), and includes `XRCompositionLayer` (`OpenXRCompositionLayerEquirect`).
- Mock runtime (`AlleyCat.XR.Mock`) instantiates `@game/assets/xr/mock_runtime.tscn`, uses scene-attached component scripts (`MockXRRuntimeNode`, `MockXRCameraNode`, `MockXRHandControllerNode`) on `Node3D`/`Camera3D`, and omits `XRCompositionLayer`.

### Mock Test Hooks

In mock runtime, deterministic tests may interact with runtime nodes directly after runtime instantiation. The mock runtime exposes `MockXRRuntimeNode.TriggerPoseRecentered()` for pose recenter simulation.

## References

### Implementation

- @game/src/XR/XRManager.cs
- @game/src/XR/IXRRuntime.cs
- @game/src/XR/XRManagerAbstractions.cs
- @game/src/XR/OpenXR/OpenXRRuntimeNode.cs
- @game/src/XR/OpenXR/OpenXRCameraNode.cs
- @game/src/XR/OpenXR/OpenXRHandControllerNode.cs
- @game/src/XR/Mock/MockXRRuntimeNode.cs
- @game/src/XR/Mock/MockXRCameraNode.cs
- @game/src/XR/Mock/MockXRHandControllerNode.cs
- @game/assets/xr/openxr_runtime.tscn
- @game/assets/xr/mock_runtime.tscn

### Related Specs

- [CORE-001: Global Scene](../../001-global-scene/index.md)
