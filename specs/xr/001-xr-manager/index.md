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
3. Runtime scene selection through exported packed scenes (`OpenXrRuntimeScene`, `MockRuntimeScene`)
4. Runtime-specific behaviour delegated to `IXRRuntime` implementations for real XR and mock XR
5. Mock-only manual-fire hooks for deterministic integration tests

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

- `IXrOrigin`
- `IXrCamera`
- `IXrHandController`

`IXrHandController` exposes a runtime-agnostic callback/event API for button and input changes.

### Signals

| Signal | Description |
|------|------|
| Initialised(bool succeeded) | Emitted after XR initialisation is attempted |
| PoseRecentered() | Emitted when XR pose recentering occurs |

### Runtime Split

- OpenXR runtime (`AlleyCat.XR.OpenXR`) instantiates `@game/assets/xr/openxr_runtime.tscn`, uses scene-attached component scripts (`OpenXrRuntimeNode`, `OpenXrCameraNode`, `OpenXrHandControllerNode`) on XR node types (`XROrigin3D`, `XRCamera3D`, `XRController3D`), and includes `XRCompositionLayer` (`OpenXRCompositionLayerEquirect`).
- Mock runtime (`AlleyCat.XR.Mock`) instantiates `@game/assets/xr/mock_runtime.tscn`, uses scene-attached component scripts (`MockXrRuntimeNode`, `MockXrCameraNode`, `MockXRManagerHandController`) on `Node3D`/`Camera3D`, and omits `XRCompositionLayer`.

### Mock Test Hooks

In mock runtime, tests can manually fire XR signals through:

- `XRManager.FirePoseRecenteredForMock()`
- `MockXRManagerHandController.FireButtonPressed(string signalName)`
- `MockXRManagerHandController.FireButtonReleased(string signalName)`
- `MockXRManagerHandController.FireInputFloatChanged(string signalName, float value)`
- `MockXRManagerHandController.FireInputVector2Changed(string signalName, Vector2 value)`

## References

### Implementation

- @game/src/XR/XRManager.cs
- @game/src/XR/IXRRuntime.cs
- @game/src/XR/XRManagerAbstractions.cs
- @game/src/XR/OpenXR/OpenXrRuntimeNode.cs
- @game/src/XR/OpenXR/OpenXrCameraNode.cs
- @game/src/XR/OpenXR/OpenXrHandControllerNode.cs
- @game/src/XR/Mock/MockXrRuntimeNode.cs
- @game/src/XR/Mock/MockXrCameraNode.cs
- @game/src/XR/Mock/MockXrManagerHandController.cs
- @game/assets/xr/openxr_runtime.tscn
- @game/assets/xr/mock_runtime.tscn

### Related Specs

- [CORE-001: Global Scene](../../001-global-scene/index.md)
