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
4. Optical recovery must not create an accidental grab or revoke another hand's
   presentation while the runtime changes source availability.

## Technical Requirements

1. `XRManager` must orchestrate runtime startup and communicate only through
   `IXRRuntime`.
2. Runtime selection must use exported packed scenes and instantiate exactly
   one runtime root.
3. Runtime-agnostic abstractions must remain the integration surface for downstream systems, including the global
   hand-pose mode (`Controller`/`Optical`) and per-side hand-pose sources defined in
   [XR-002: Optical Hand Tracking](../002-optical-hand-tracking/index.md).
4. Raw optical hand-joint snapshots are a runtime-agnostic gameplay surface: they are available to
   non-presentation consumers (for example the optical grab recogniser routed by
   [CTRL-002: Hand Grab Input](../../ctrl/002-hand-grab-input/index.md)) and remain available while visual optical
   output is suppressed by a committed grab on that hand.
5. Startup state and lifecycle signals must support late subscribers.
6. Mock runtime must expose deterministic hooks for integration tests, including committed hand-pose mode transitions
   and per-side hand-pose source samples (valid/invalid/lost/recovered injection) as required by XR-002. The
   committed mode-change notification communicates the one global mode only; downstream lifecycle and presentation
   cleanup remain scoped to their source provenance and publisher, and must not infer per-hand mixed modes.
7. Each per-side source sample must carry a validity boundary and canonical sample epoch suitable for one authoritative
   tick. Consumers may retain the last valid sample only where their own contract permits it; they must not infer an
   open grip, a fresh recognition measurement, or presentation authority from a missing sample.

## In Scope

- XR startup orchestration and runtime-scene selection.
- Runtime abstraction contracts for origin, camera, hand controllers, and the global hand-pose mode with per-side
  hand-pose sources.
- Raw optical hand-joint snapshots as a gameplay-consumer surface, including availability during committed-grab
  visual suppression.
- Startup state and signal contracts.
- Mock runtime hooks for deterministic testing.
- Canonical per-side source-sample validity and epoch boundary for downstream consumers.

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
5. Mock runtime supports deterministic integration tests without hardware, including the hand-pose mode behaviour
   required by XR-002.
6. Raw optical joint snapshots remain available to gameplay consumers while a committed grab suppresses that hand's
   visual optical output, and mock valid/invalid/lost/recovered injection drives the optical grab lifecycle
   deterministically.
7. Technical Requirement 6 is verified without treating a global mode change as per-hand source mixing or permission
   for unrelated publisher cleanup.
8. Technical Requirement 7 is verified: mock samples distinguish valid, invalid, and missing state at one canonical
   epoch, and recovery does not fabricate a recognition or grip state.
9. User Requirement 4 is verified: recovery neither causes an unintended grab nor allows unrelated presentation
   cleanup to change the active hand.

## References

- @game/src/XR/XRManager.cs
- @game/src/XR/XRManagerAbstractions.cs
- @game/src/XR/Mock/MockXRRuntimeNode.cs
- [XR-002: Optical Hand Tracking](../002-optical-hand-tracking/index.md)
- [CTRL-002: Hand Grab Input](../../ctrl/002-hand-grab-input/index.md)
- [CORE-001: Global Singleton](../../core/001-global-scene/index.md)
