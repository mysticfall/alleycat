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

## In Scope

- Global autoload responsibilities for XR and shared UI rendering.
- XR runtime composition hosting through `XRManager`.
- SubViewport configuration required for full-screen UI and overlay rendering.

## Out Of Scope

- Feature-specific UI flow behaviour (for example splash/loading timing contracts).
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

## Architecture

The Global Scene implements the autoload pattern, meaning it is automatically instantiated when the game starts and persists throughout the entire session. This provides:

- Global accessibility to XR services
- Persistent UI viewport for overlays
- Centralised XR state management

## Acceptance Criteria

1. `@game/assets/scenes/global.tscn` is configured as an autoload and is available session-wide.
2. The global scene hosts `XRManager` integration in line with [XR-001: XRManager](../xr/001-xr-manager/index.md).
3. A dedicated UI `SubViewport` exists with transparent background enabled and configured size of `1800 × 1200`.
4. The specification defines both user-visible behaviour requirements and technical implementation contracts.

## References

### Implementation

- @game/assets/scenes/global.tscn

### Related Specs

- [XR-001: XRManager](../xr/001-xr-manager/index.md)
