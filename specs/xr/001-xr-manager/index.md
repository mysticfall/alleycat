---
id: XR-001
---

# XRManager

## Requirement

XRManager provides an orchestration layer between gameplay systems and XR
runtimes. It selects a runtime scene, instantiates its root node, and
communicates only through the `IXRRuntime` contract. This keeps consumers
independent of runtime-specific node types while enabling deterministic
testing.

## Goal

Deliver a stable XR orchestration boundary that decouples gameplay from
runtime implementation, supports both real and mock XR backends, and remains
testable without hardware.

## User Requirements

1. XR startup success or failure must be surfaced reliably so player flows
   react predictably.
2. XR-dependent gameplay must receive consistent origin, camera, and
   hand-controller surfaces regardless of runtime backend.
3. Test environments must support deterministic XR behaviour without OpenXR
   hardware.

## Technical Requirements

1. `XRManager` must orchestrate runtime startup and communicate only through
   `IXRRuntime`.
2. Runtime selection must use exported packed scenes and instantiate exactly
   one runtime root.
3. Runtime-agnostic abstractions must remain the integration surface for
   downstream systems.
4. Startup state and lifecycle signals must support late subscribers.
5. Mock runtime must expose deterministic hooks for integration tests.

## In Scope

- XR startup orchestration and runtime-scene selection.
- Runtime abstraction contracts for origin, camera, and hand controllers.
- Startup state and signal contracts.
- Mock runtime hooks for deterministic testing.

## Out Of Scope

- Gameplay feature behaviour outside XR runtime orchestration.
- Runtime-specific component behaviour not exposed by `IXRRuntime`.
- Platform certification and runtime-performance tuning policy.

## Acceptance Criteria

1. XR startup success or failure is surfaced to players through documented
   signals.
2. Downstream systems access XR capabilities only through runtime-agnostic
   interfaces.
3. Runtime selection is configurable via exported packed scenes.
4. Late subscribers can read initialisation state after `_Ready`.
5. Mock runtime supports deterministic integration tests without hardware.

## References

- @game/src/XR/XRManager.cs
- @game/src/XR/XRManagerAbstractions.cs
- @game/src/XR/Mock/MockXRRuntimeNode.cs
- [CORE-001: Global Scene](../../core/001-global-scene/index.md)
