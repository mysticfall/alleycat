---
id: CORE-001
---

# Global Scene

## Overview

The Global Scene (`@game/assets/scenes/global.tscn`) is an automatically loaded Godot autoload that provides global services and shared rendering infrastructure to all loaded scenes in the game. It serves as the foundational layer for VR support and UI rendering.

## Requirement

The Global Scene must provide:

1. Global autoload availability for session-wide systems
2. XR runtime wiring via XRManager (see [XR-001: XRManager](../xr/001-xr-manager/index.md))
3. A dedicated SubViewport for rendering full-screen UI and overlays
4. Game entry point with startup scene exports for runtime loading

## Goal

Define a clear contract for shared XR and UI infrastructure that all gameplay scenes can rely on from startup.

## User Requirements

1. Players must receive consistent XR and UI behaviour across scene changes without re-initialisation glitches.
2. VR overlays and full-screen UI must render reliably through a dedicated global rendering surface.

## Technical Requirements

1. `global.tscn` must remain an autoload scene that is available for the full game session.
2. XR runtime wiring must be delegated through `XRManager` as defined in [XR-001: XRManager](../xr/001-xr-manager/index.md).
3. A dedicated `SubViewport` must be provided for full-screen UI/overlay rendering with transparent background support.
4. Integration points consumed by other systems must be stable and discoverable from the global scene root.
5. The `Game` node must export `StartScenePath` as a path string (`*.tscn`) identifying the scene to load after startup.
6. The `Game` node must export `SplashScreenScene` as a `PackedScene` property for dynamic instantiation.

## In Scope

- Global autoload responsibilities for XR and shared UI rendering.
- XR runtime composition hosting through `XRManager`.
- SubViewport configuration required for full-screen UI and overlay rendering.
- Game node export properties for startup scene loading (`StartScenePath`, `SplashScreenScene`).

## Out Of Scope

- Splash screen flow behaviour, timing, and skip flag handling. These are specified in [UI-001: Splash Screen](../ui/001-splash-screen/index.md).
- Loading screen flow behaviour and timing contracts.
- XR runtime internals beyond the `XRManager` contract.
- Gameplay-specific logic that does not belong to session-wide infrastructure.

## Features

### XR Runtime Integration

Global Scene integrates XRManager and hosts XR runtime composition. The XRManager contract is specified in [XR-001: XRManager](../xr/001-xr-manager/index.md).

### SubViewport

A dedicated viewport for UI rendering:

| Property | Value |
|----------|-------|
| Size | 1800 × 1200 pixels |
| Transparent Background | Enabled |

The layer is configured to render content from the SubViewport to the VR display.

### Game Startup Exports

The Global Scene hosts the `Game` node as its root script, which provides export properties for scene loading:

| Export Property | Type | Purpose |
|---------------|------|---------|
| `StartScenePath` | String (path `\*.tscn`) | Scene to load after startup completes |
| `SplashScreenScene` | `PackedScene` | Splash scene for dynamic instantiation at startup |

Note: Runtime splash behaviour (including `--skip-splash` handling) is defined in [UI-001: Splash Screen](../ui/001-splash-screen/index.md).

## Architecture

The Global Scene implements the autoload pattern, meaning it is automatically instantiated when the game starts and persists throughout the entire session. This provides:

- Global accessibility to XR services
- Persistent UI viewport for overlays
- Centralised XR state management
- Scene loading contract through Game node exports

## Acceptance Criteria

1. `@game/assets/scenes/global.tscn` is configured as an autoload and is available session-wide.
2. The global scene hosts `XRManager` integration in line with [XR-001: XRManager](../xr/001-xr-manager/index.md).
3. A dedicated UI `SubViewport` exists with transparent background enabled and configured size of `1800 × 1200`.
4. The `Game` node exports `StartScenePath` as a string path to the start scene.
5. The `Game` node exports `SplashScreenScene` as a `PackedScene` for startup instantiation.
6. The specification defines both user-visible behaviour requirements and technical implementation contracts.

## References

### Implementation

- @game/assets/scenes/global.tscn
- @game/src/Game.cs

### Related Specs

- [XR-001: XRManager](../xr/001-xr-manager/index.md)
- [UI-001: Splash Screen](../ui/001-splash-screen/index.md)