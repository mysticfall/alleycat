---
id: CORE-001
---

# Global Scene

## Overview

The Global Scene (`@game/assets/scenes/global.tscn`) is an automatically loaded Godot autoload that provides core XR infrastructure and global services to all loaded scenes in the game. It serves as the foundational layer for VR support and UI rendering.

## Requirement

The Global Scene must provide:

1. XR interface initialisation with complete headset and controller tracking
2. A dedicated SubViewport for rendering full-screen UI and overlays
3. Equirectangular projection layer for immersive visual content

## Features

### XRDevices

The XRDevices component (`@game/src/XR/XRDevices.cs`) manages all XR-related functionality:

| Node | Type | Description |
|------|------|-------------|
| Origin | XROrigin3D | Root node for all XR tracking |
| MainCamera | XRCamera3D | Primary camera at y=1.7m with 37.85° FOV |
| RightController | XRController3D | Right hand tracker |
| LeftController | XRController3D | Left hand tracker |
| RightController/HandPosition | Node3D | Right hand transform reference |
| LeftController/HandPosition | Node3D | Left hand transform reference |
| XRCompositionLayer | OpenXRCompositionLayerEquirect | Immersive equirectangular projection |

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

- @game/src/XR/XRDevices.cs
- @game/assets/scenes/global.tscn
