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

## References

### Implementation

- @game/assets/scenes/global.tscn

### Related Specs

- [XR-001: XRManager](../xr/001-xr-manager/index.md)
