---
id: CORE-002
title: Configuration API
---

# Configuration API

## Requirement

Retire the legacy `ConfigProvider` configuration API. Project-wide runtime configuration is defined by
[CORE-006: Microsoft Configuration Integration](../006-microsoft-configuration-integration/index.md), and backward
compatibility with the removed bridge is not required.

## Goal

Keep configuration ownership clear: core provides Microsoft configuration infrastructure, while subsystems read or bind
their own option sections.

## User Requirements

1. Developers have one supported runtime configuration path through CORE-006.
2. Removed legacy configuration APIs do not create a second source of truth.
3. Subsystems can still support explicit custom JSON paths when they own that loading behaviour locally.

## Technical Requirements

1. `ConfigProvider` must not be restored or required as a compatibility bridge.
2. Core configuration registration must expose `IConfiguration` using CORE-006 source ordering and paths.
3. Core configuration code must not bind or reference concrete subsystem option APIs such as `AIOptions`, `STTOptions`,
   or `TTSOptions`.
4. Subsystems that need typed settings must bind/read their own option sections from core-provided `IConfiguration`.
5. Subsystems that need a single explicit custom JSON path must build that Microsoft configuration source locally.
6. The shared configuration helper is `GameConfiguration`; the old `AlleyCatConfiguration` name must not be used.

## In Scope

- Removal of normative `ConfigProvider` compatibility requirements.
- CORE-006 as the only core-owned runtime configuration integration.
- Subsystem-owned option binding from `IConfiguration`.
- Subsystem-owned explicit custom JSON path loading when a subsystem requires it.

## Out Of Scope

- Configuration validation schemas or automatic type conversion beyond basic string parsing.
- Hot-reloading of configuration at runtime without explicit re-fetch.
- Configuration UI for editing user overrides.
- Remote configuration or cloud-based settings.
- Encryption or obfuscation of configuration values.
- Backward compatibility for removed `ConfigProvider` callers.

## Contract

### Runtime Configuration Path

CORE-006 defines the normative default runtime files:

| File | Purpose | Access |
|------|---------|-------|
| `res://AlleyCat.json` | Project defaults (shipped with game) | Read-only |
| `user://AlleyCat.json` | User overrides (persisted in user data directory) | Read-write |

The repository source for `res://AlleyCat.json` is `@game/AlleyCat.json`.

### Ownership Boundary

Core owns:

- `GameConfiguration` as the shared Microsoft configuration builder.
- `IConfiguration` registration with shipped defaults and optional user overrides.
- Path resolution and logging infrastructure needed for configuration startup.

Subsystems own:

- Option models and section binding for their own settings.
- Validation and missing-value behaviour for subsystem-specific required settings.
- Any explicit custom-path JSON configuration loading required by editor or test scenarios.

Core must not reference concrete subsystem option classes.

## Acceptance Criteria

1. The spec defines the user outcome of a single supported runtime configuration path.
2. The spec states that `ConfigProvider` compatibility is removed and not required.
3. Core configuration ownership is limited to `GameConfiguration`, `IConfiguration`, paths, and infrastructure.
4. Subsystem option models, binding, validation, and custom-path loading are assigned to owning subsystems.
5. The spec does not exclude mandatory implementation contracts through `Out Of Scope`.
6. Acceptance criteria verify both user requirements and technical requirements.

**Traceability Map:** User Requirements 1-3 -> AC-1, AC-2; Technical Requirements 1-6 -> AC-3, AC-4, AC-5, AC-6.

## References

### Implementation

- `@game/src/Core/Configuration/GameConfiguration.cs` - Microsoft configuration helper
- `@game/src/Core/Configuration/ConfigurationServiceCollectionExtensions.cs` - Core configuration registration

### Related Specs

- [CORE-006: Microsoft Configuration Integration](../006-microsoft-configuration-integration/index.md)

### Configuration Files

- `res://AlleyCat.json` (`@game/AlleyCat.json`) - Project defaults for CORE-006 runtime configuration
- `user://AlleyCat.json` - User override configuration for CORE-006 runtime configuration
